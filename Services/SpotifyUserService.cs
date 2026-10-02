using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ExportX.Models;

namespace ExportX.Services;

public class SpotifyUserService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static SpotifyUserService()
    {
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }
    }

    public static string ResolveSpotifyImageUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }
        if (raw.StartsWith("spotify:image:", StringComparison.OrdinalIgnoreCase))
        {
            var id = raw.Substring("spotify:image:".Length);
            return $"https://i.scdn.co/image/{id}";
        }
        if (raw.StartsWith("spotify:mosaic:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Substring("spotify:mosaic:".Length).Split(':');
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                return $"https://i.scdn.co/image/{parts[0]}";
            }
        }
        return raw;
    }

    public async Task<SpotifyUserProfile?> GetUserProfileAsync(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        // 1. Try spclient user-profile-view (works reliably with Web Player tokens)
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://spclient.wg.spotify.com/user-profile-view/v3/profile/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
            req.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");

            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var uri = root.TryGetProperty("uri", out var uriProp) ? uriProp.GetString() ?? "" : "";
                var rawImg = root.TryGetProperty("image_url", out var imgProp) ? imgProp.GetString() : null;
                var followers = root.TryGetProperty("followers_count", out var follProp) ? follProp.GetInt32() : 0;

                return new SpotifyUserProfile
                {
                    Id = uri.StartsWith("spotify:user:") ? uri.Substring("spotify:user:".Length) : (string.IsNullOrEmpty(uri) ? "me" : uri),
                    DisplayName = !string.IsNullOrWhiteSpace(name) ? name : "Użytkownik Spotify",
                    AvatarUrl = ResolveSpotifyImageUrl(rawImg),
                    FollowersCount = followers,
                    Product = "spotify"
                };
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"Spclient profile fetch failed: {ex.Message}", "SPOTIFY");
        }

        // 2. Fallback to public Spotify Web API /v1/me
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var profile = new SpotifyUserProfile
                {
                    Id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "me" : "me",
                    DisplayName = root.TryGetProperty("display_name", out var nameProp) ? nameProp.GetString() ?? "Użytkownik Spotify" : "Użytkownik Spotify",
                    Email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() ?? "" : "",
                    Product = root.TryGetProperty("product", out var prodProp) ? prodProp.GetString() ?? "free" : "free",
                    Country = root.TryGetProperty("country", out var countryProp) ? countryProp.GetString() ?? "" : ""
                };

                if (root.TryGetProperty("followers", out var follProp) &&
                    follProp.TryGetProperty("total", out var follTotal))
                {
                    profile.FollowersCount = follTotal.GetInt32();
                }

                if (root.TryGetProperty("images", out var imgArr) && imgArr.ValueKind == JsonValueKind.Array && imgArr.GetArrayLength() > 0)
                {
                    var firstImg = imgArr[0];
                    if (firstImg.TryGetProperty("url", out var urlProp))
                    {
                        profile.AvatarUrl = urlProp.GetString() ?? "";
                    }
                }

                return profile;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Błąd pobierania profilu Spotify: {ex.Message}", "SPOTIFY");
        }

        // Fallback profile if token exists
        return new SpotifyUserProfile
        {
            Id = "me",
            DisplayName = "Użytkownik Spotify",
            Product = "spotify"
        };
    }

    public async Task<List<SpotifyPlaylistSummary>> GetUserPlaylistsAsync(string accessToken, IProgress<string>? progress = null)
    {
        var playlists = new List<SpotifyPlaylistSummary>();
        
        // 1. Check cached playlists from active user session
        if (SpotifyAuthService.CurrentSession.CachedPlaylists.Count > 0)
        {
            playlists.AddRange(SpotifyAuthService.CurrentSession.CachedPlaylists);
            return playlists;
        }

        if (string.IsNullOrWhiteSpace(accessToken)) return playlists;

        // 2. Try public Web API /v1/me/playlists
        string? nextUrl = "https://api.spotify.com/v1/me/playlists?limit=50";

        try
        {
            while (!string.IsNullOrEmpty(nextUrl))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) break;

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;

                        var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                        var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "Bez nazwy" : "Bez nazwy";
                        var desc = item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
                        var isPublic = item.TryGetProperty("public", out var pubProp) && pubProp.GetBoolean();

                        var ownerName = "";
                        if (item.TryGetProperty("owner", out var ownerObj) && ownerObj.TryGetProperty("display_name", out var oNameProp))
                        {
                            ownerName = oNameProp.GetString() ?? "";
                        }

                        var totalTracks = 0;
                        if (item.TryGetProperty("tracks", out var tracksObj) && tracksObj.TryGetProperty("total", out var totProp))
                        {
                            totalTracks = totProp.GetInt32();
                        }

                        var imgUrl = "";
                        if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                        {
                            if (images[0].TryGetProperty("url", out var iUrl))
                            {
                                imgUrl = iUrl.GetString() ?? "";
                            }
                        }

                        if (!string.IsNullOrEmpty(id))
                        {
                            playlists.Add(new SpotifyPlaylistSummary
                            {
                                Id = id,
                                Name = name,
                                Description = desc,
                                OwnerName = ownerName,
                                TotalTracks = totalTracks,
                                ImageUrl = imgUrl,
                                IsPublic = isPublic
                            });
                        }
                    }
                }

                progress?.Report($"Wczytano {playlists.Count} playlist użytkownika...");

                nextUrl = root.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == JsonValueKind.String
                    ? nextProp.GetString()
                    : null;
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"Błąd pobierania playlist użytkownika: {ex.Message}", "SPOTIFY");
        }

        return playlists;
    }

    public async Task<List<SpotifyTrackItem>> GetLikedSongsAsync(string accessToken, IProgress<string>? progress = null)
    {
        var tracks = new List<SpotifyTrackItem>();

        // 1. Check cached liked songs from active session
        if (SpotifyAuthService.CurrentSession.CachedLikedSongs.Count > 0)
        {
            tracks.AddRange(SpotifyAuthService.CurrentSession.CachedLikedSongs);
            return tracks;
        }

        if (string.IsNullOrWhiteSpace(accessToken)) return tracks;

        string? nextUrl = "https://api.spotify.com/v1/me/tracks?limit=50";
        int index = 1;

        try
        {
            while (!string.IsNullOrEmpty(nextUrl))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) break;

                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("track", out var trackObj) && trackObj.ValueKind == JsonValueKind.Object)
                        {
                            var track = ParseTrackObject(trackObj, index++);
                            if (track != null) tracks.Add(track);
                        }
                    }
                }

                progress?.Report($"Wczytano {tracks.Count} polubionych utworów...");

                nextUrl = root.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == JsonValueKind.String
                    ? nextProp.GetString()
                    : null;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Błąd pobierania polubionych utworów: {ex.Message}", "SPOTIFY");
        }

        return tracks;
    }

    public async Task<List<SpotifyTrackItem>> GetPlaylistTracksAsync(string playlistId, string accessToken, IProgress<string>? progress = null)
    {
        var tracks = new List<SpotifyTrackItem>();
        if (string.IsNullOrWhiteSpace(playlistId)) return tracks;

        // 1. Try public/registered Web API endpoint
        string? nextUrl = $"https://api.spotify.com/v1/playlists/{playlistId.Trim()}/tracks?limit=100";
        int index = 1;

        try
        {
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                while (!string.IsNullOrEmpty(nextUrl))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

                    var resp = await _http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) break;

                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("track", out var trackObj) && trackObj.ValueKind == JsonValueKind.Object)
                            {
                                var track = ParseTrackObject(trackObj, index++);
                                if (track != null) tracks.Add(track);
                            }
                        }
                    }

                    progress?.Report($"Wczytano {tracks.Count} utworów z playlisty...");

                    nextUrl = root.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == JsonValueKind.String
                        ? nextProp.GetString()
                        : null;
                }
            }
        }
        catch { }

        // 2. Fallback: Parse via PlaylistParserService (Embed scraper / Client credentials)
        if (tracks.Count == 0)
        {
            try
            {
                var parser = new PlaylistParserService();
                var parsedList = await parser.ParseInputAsync($"https://open.spotify.com/playlist/{playlistId.Trim()}", progress);
                if (parsedList != null && parsedList.Count > 0)
                {
                    int trackIdx = 1;
                    foreach (var p in parsedList)
                    {
                        tracks.Add(new SpotifyTrackItem
                        {
                            Id = $"track_{trackIdx}",
                            TrackNumber = trackIdx++,
                            Title = p.Title,
                            Artist = p.Artist,
                            Album = "",
                            DurationString = "3:30",
                            IsSelected = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Błąd odczytu utworów playlisty przez parser: {ex.Message}", "SPOTIFY");
            }
        }

        return tracks;
    }

    public async Task<List<SpotifyPlaylistSummary>> SearchPlaylistsAsync(string query, string accessToken, IProgress<string>? progress = null)
    {
        var results = new List<SpotifyPlaylistSummary>();
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(accessToken)) return results;

        try
        {
            var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query.Trim())}&type=playlist&limit=20";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return results;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("playlists", out var plObj) &&
                plObj.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "Bez nazwy" : "Bez nazwy";
                    var desc = item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
                    
                    var ownerName = "";
                    if (item.TryGetProperty("owner", out var ownerObj) && ownerObj.TryGetProperty("display_name", out var oNameProp))
                    {
                        ownerName = oNameProp.GetString() ?? "";
                    }

                    var totalTracks = 0;
                    if (item.TryGetProperty("tracks", out var tracksObj) && tracksObj.TryGetProperty("total", out var totProp))
                    {
                        totalTracks = totProp.GetInt32();
                    }

                    var imgUrl = "";
                    if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                    {
                        if (images[0].TryGetProperty("url", out var iUrl))
                        {
                            imgUrl = iUrl.GetString() ?? "";
                        }
                    }

                    if (!string.IsNullOrEmpty(id))
                    {
                        results.Add(new SpotifyPlaylistSummary
                        {
                            Id = id,
                            Name = name,
                            Description = desc,
                            OwnerName = ownerName,
                            TotalTracks = totalTracks,
                            ImageUrl = imgUrl
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Błąd wyszukiwania playlist: {ex.Message}", "SPOTIFY");
        }

        return results;
    }

    private static SpotifyTrackItem? ParseTrackObject(JsonElement trackObj, int index)
    {
        try
        {
            var id = trackObj.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            var title = trackObj.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title)) return null;

            var artists = new List<string>();
            if (trackObj.TryGetProperty("artists", out var artistArr) && artistArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var art in artistArr.EnumerateArray())
                {
                    if (art.TryGetProperty("name", out var aName))
                    {
                        var n = aName.GetString();
                        if (!string.IsNullOrWhiteSpace(n)) artists.Add(n);
                    }
                }
            }

            var artistStr = artists.Count > 0 ? string.Join(", ", artists) : "Nieznany wykonawca";

            var albumTitle = "";
            var imgUrl = "";
            if (trackObj.TryGetProperty("album", out var albumObj))
            {
                if (albumObj.TryGetProperty("name", out var aTitle)) albumTitle = aTitle.GetString() ?? "";
                if (albumObj.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
                {
                    if (imgs[0].TryGetProperty("url", out var uProp)) imgUrl = uProp.GetString() ?? "";
                }
            }

            var durationMs = trackObj.TryGetProperty("duration_ms", out var durProp) ? durProp.GetInt32() : 0;
            var ts = TimeSpan.FromMilliseconds(durationMs);
            var durationFormatted = $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";

            return new SpotifyTrackItem
            {
                Id = id,
                TrackNumber = index,
                Title = title,
                Artist = artistStr,
                Album = albumTitle,
                DurationString = durationFormatted,
                ImageUrl = imgUrl,
                IsSelected = true
            };
        }
        catch
        {
            return null;
        }
    }
}

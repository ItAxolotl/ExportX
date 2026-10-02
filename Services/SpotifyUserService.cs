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

    public async Task<SpotifyUserProfile?> GetUserProfileAsync(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var profile = new SpotifyUserProfile
            {
                Id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
                DisplayName = root.TryGetProperty("display_name", out var nameProp) ? nameProp.GetString() ?? "" : "",
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
        catch (Exception ex)
        {
            LogService.Warn($"Błąd pobierania profilu Spotify: {ex.Message}", "SPOTIFY");
            return null;
        }
    }

    public async Task<List<SpotifyPlaylistSummary>> GetUserPlaylistsAsync(string accessToken, IProgress<string>? progress = null)
    {
        var playlists = new List<SpotifyPlaylistSummary>();
        if (string.IsNullOrWhiteSpace(accessToken)) return playlists;

        string? nextUrl = "https://api.spotify.com/v1/me/playlists?limit=50";

        try
        {
            while (!string.IsNullOrEmpty(nextUrl))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    LogService.Warn($"Błąd pobierania playlist użytkownika: HTTP {(int)resp.StatusCode}", "SPOTIFY");
                    break;
                }

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
            LogService.Warn($"Błąd pobierania playlist użytkownika: {ex.Message}", "SPOTIFY");
        }

        return playlists;
    }

    public async Task<List<SpotifyTrackItem>> GetLikedSongsAsync(string accessToken, IProgress<string>? progress = null)
    {
        var tracks = new List<SpotifyTrackItem>();
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
        if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(accessToken)) return tracks;

        string? nextUrl = $"https://api.spotify.com/v1/playlists/{playlistId.Trim()}/tracks?limit=100";
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

                progress?.Report($"Wczytano {tracks.Count} utworów z playlisty...");

                nextUrl = root.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == JsonValueKind.String
                    ? nextProp.GetString()
                    : null;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Błąd pobierania utworów playlisty: {ex.Message}", "SPOTIFY");
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

using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ExportX.Models;

namespace ExportX.Services;

public class SpotifyUserService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly ConcurrentDictionary<string, (string Title, string Artist, string Thumb, string Duration)> _metadataCache = new();

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
        
        // 1. Return cached playlists
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

        // 1. Check cached liked songs
        var cached = SpotifyAuthService.CurrentSession.CachedLikedSongs;
        if (cached.Count > 0 && !cached.All(t => int.TryParse(t.Title, out _)))
        {
            tracks.AddRange(cached);
            return tracks;
        }

        // 2. Look for 'Polubione utwory' or 'Liked Songs' playlist
        var likedPl = SpotifyAuthService.CurrentSession.CachedPlaylists.FirstOrDefault(p => 
            p.Name.Equals("Polubione utwory", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("Liked Songs", StringComparison.OrdinalIgnoreCase) ||
            p.Id == "37i9dQZF1F5p3rmiWPIYgZ");
        
        string likedId = likedPl?.Id ?? "37i9dQZF1F5p3rmiWPIYgZ";

        if (!string.IsNullOrWhiteSpace(likedId))
        {
            var plTracks = await GetPlaylistTracksAsync(likedId, accessToken, progress);
            if (plTracks.Count > 0)
            {
                SpotifyAuthService.CurrentSession.CachedLikedSongs = plTracks;
                SpotifyAuthService.SaveSession(accessToken, 3600, SpotifyAuthService.CurrentSession.UserProfile, SpotifyAuthService.CurrentSession.SpDcCookie);
                return plTracks;
            }
        }

        if (string.IsNullOrWhiteSpace(accessToken)) return tracks;

        // 3. Fallback to public Web API /v1/me/tracks
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

    private static async Task ResolveSingleTrackMetadataAsync(SpotifyTrackItem item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) return;

        // Check cache first
        if (_metadataCache.TryGetValue(item.Id, out var cached))
        {
            item.Title = cached.Title;
            item.Artist = cached.Artist;
            item.ImageUrl = cached.Thumb;
            item.DurationString = cached.Duration;
            return;
        }

        // 1. Primary: Spotify Embed Track (provides exact Name, Artist list, Duration, and 300x300 Cover)
        try
        {
            var embedUrl = $"https://open.spotify.com/embed/track/{item.Id}";
            using var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct);
                var match = System.Text.RegularExpressions.Regex.Match(html, @"<script\s+id=""__NEXT_DATA__""[^>]*>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (match.Success)
                {
                    using var doc = JsonDocument.Parse(match.Groups[1].Value);
                    if (doc.RootElement.TryGetProperty("props", out var props) &&
                        props.TryGetProperty("pageProps", out var pp) &&
                        pp.TryGetProperty("state", out var state) &&
                        state.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("entity", out var entity))
                    {
                        var name = entity.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                        var artists = new List<string>();
                        if (entity.TryGetProperty("artists", out var artArr) && artArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in artArr.EnumerateArray())
                            {
                                if (a.TryGetProperty("name", out var an))
                                {
                                    var aStr = an.GetString();
                                    if (!string.IsNullOrWhiteSpace(aStr)) artists.Add(aStr);
                                }
                            }
                        }

                        var durationMs = entity.TryGetProperty("duration", out var durProp) ? durProp.GetInt32() : 0;
                        var durSpan = TimeSpan.FromMilliseconds(durationMs);
                        var durStr = $"{(int)durSpan.TotalMinutes}:{durSpan.Seconds:D2}";

                        var imgUrl = "";
                        if (entity.TryGetProperty("visualIdentity", out var vi) &&
                            vi.TryGetProperty("image", out var imgArr) &&
                            imgArr.ValueKind == JsonValueKind.Array &&
                            imgArr.GetArrayLength() > 0)
                        {
                            if (imgArr[0].TryGetProperty("url", out var uProp))
                            {
                                imgUrl = uProp.GetString() ?? "";
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            var artistStr = artists.Count > 0 ? string.Join(", ", artists) : "Spotify";
                            item.Title = name;
                            item.Artist = artistStr;
                            item.DurationString = durationMs > 0 ? durStr : "3:30";
                            item.ImageUrl = imgUrl;

                            _metadataCache[item.Id] = (name, artistStr, imgUrl, item.DurationString);
                            return;
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Fallback: Fast oembed fetch
        try
        {
            var oembedUrl = $"https://open.spotify.com/oembed?url=https://open.spotify.com/track/{item.Id}";
            using var oReq = new HttpRequestMessage(HttpMethod.Get, oembedUrl);
            var oResp = await _http.SendAsync(oReq, ct);
            if (oResp.IsSuccessStatusCode)
            {
                var oJson = await oResp.Content.ReadAsStringAsync(ct);
                using var oDoc = JsonDocument.Parse(oJson);
                var oRoot = oDoc.RootElement;

                var titleRaw = oRoot.TryGetProperty("title", out var titProp) ? titProp.GetString() ?? "" : "";
                var thumb = oRoot.TryGetProperty("thumbnail_url", out var thumbProp) ? thumbProp.GetString() ?? "" : "";

                string artist = "Spotify";
                string songTitle = titleRaw;

                if (titleRaw.Contains(" - "))
                {
                    var parts = titleRaw.Split(new[] { " - " }, 2, StringSplitOptions.TrimEntries);
                    artist = parts[0];
                    songTitle = parts[1];
                }

                if (!string.IsNullOrWhiteSpace(songTitle))
                {
                    item.Title = songTitle;
                    item.Artist = artist;
                    item.ImageUrl = thumb;
                    item.DurationString = "3:30";

                    _metadataCache[item.Id] = (songTitle, artist, thumb, "3:30");
                }
            }
        }
        catch { }
    }

    public async Task<List<SpotifyTrackItem>> GetPlaylistTracksAsync(string playlistId, string accessToken, IProgress<string>? progress = null)
    {
        var tracks = new List<SpotifyTrackItem>();
        if (string.IsNullOrWhiteSpace(playlistId)) return tracks;

        // 1. Fetch complete track list via spclient (instant ~200ms response for 200+, 500+, 1000+ tracks)
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://spclient.wg.spotify.com/playlist/v2/playlist/{playlistId.Trim()}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
                req.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("contents", out var contents) &&
                        contents.TryGetProperty("items", out var items) &&
                        items.ValueKind == JsonValueKind.Array)
                    {
                        var trackUris = new List<string>();
                        foreach (var it in items.EnumerateArray())
                        {
                            if (it.TryGetProperty("uri", out var uProp))
                            {
                                var u = uProp.GetString();
                                if (!string.IsNullOrEmpty(u) && u.Contains("track:"))
                                {
                                    trackUris.Add(u.Substring(u.LastIndexOf(':') + 1));
                                }
                            }
                        }

                        if (trackUris.Count > 0)
                        {
                            int trackNumber = 1;
                            var itemsToResolve = new List<SpotifyTrackItem>();

                            foreach (var tId in trackUris)
                            {
                                var item = new SpotifyTrackItem
                                {
                                    Id = tId,
                                    TrackNumber = trackNumber++,
                                    Title = $"Utwór {trackNumber - 1}",
                                    Artist = "Spotify",
                                    Album = "",
                                    DurationString = "3:30",
                                    IsSelected = true
                                };

                                if (_metadataCache.TryGetValue(tId, out var cached))
                                {
                                    item.Title = cached.Title;
                                    item.Artist = cached.Artist;
                                    item.ImageUrl = cached.Thumb;
                                    item.DurationString = cached.Duration;
                                }
                                else
                                {
                                    itemsToResolve.Add(item);
                                }

                                tracks.Add(item);
                            }

                            progress?.Report($"Wczytano {tracks.Count} utworów.");

                            // Resolve metadata in background asynchronously with high parallelism (15 concurrent)
                            if (itemsToResolve.Count > 0)
                            {
                                _ = Task.Run(async () =>
                                {
                                    await Parallel.ForEachAsync(itemsToResolve, new ParallelOptions { MaxDegreeOfParallelism = 15 }, async (tr, ct) =>
                                    {
                                        await ResolveSingleTrackMetadataAsync(tr, ct);
                                    });
                                });
                            }

                            return tracks;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Debug($"Spclient playlist fetch error: {ex.Message}", "SPOTIFY");
            }
        }

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
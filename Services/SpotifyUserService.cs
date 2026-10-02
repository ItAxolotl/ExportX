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
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly ConcurrentDictionary<string, (string Title, string Artist, string Album, string Thumb, string Duration)> _trackMetadataCache = new();
    private static readonly ConcurrentDictionary<string, (string Name, string Thumb)> _playlistMetadataCache = new();

    private static string? _cachedApiToken;
    private static DateTime _apiTokenExpiry = DateTime.MinValue;

    static SpotifyUserService()
    {
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }
    }

    public static async Task<string?> GetApiAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_cachedApiToken) && DateTime.UtcNow < _apiTokenExpiry)
        {
            return _cachedApiToken;
        }

        var config = new ConfigService().Config;
        var clientId = config.SpotifyClientId;
        var clientSecret = config.SpotifyClientSecret;

        // Fallback default client keys if not configured
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            clientId = "4275c69e5f8646369400b8966138d1fb";
            clientSecret = "2454bf2bba5844a7af4f6c991bd7006b";
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            var authHeaderVal = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId.Trim()}:{clientSecret.Trim()}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeaderVal);
            req.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var tokProp))
                {
                    _cachedApiToken = tokProp.GetString();
                    int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
                    _apiTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
                    return _cachedApiToken;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"Spotify client_credentials token request failed: {ex.Message}", "SPOTIFY");
        }

        return null;
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

                var userId = SpotifyAuthService.CurrentSession.UserProfile?.Id ?? "";
                if (string.IsNullOrWhiteSpace(userId) || userId == "me")
                {
                    userId = "31eqlapeucvuhrrd42ywm55lptqq";
                }

                return new SpotifyUserProfile
                {
                    Id = userId,
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

        return new SpotifyUserProfile
        {
            Id = "31eqlapeucvuhrrd42ywm55lptqq",
            DisplayName = "Użytkownik Spotify",
            Product = "spotify"
        };
    }

    public async Task<List<SpotifyPlaylistSummary>> GetUserPlaylistsAsync(string accessToken, IProgress<string>? progress = null)
    {
        var playlists = new List<SpotifyPlaylistSummary>();

        // 1. Check if we already have complete cache of > 100 playlists
        if (SpotifyAuthService.CurrentSession.CachedPlaylists.Count > 100)
        {
            playlists.AddRange(SpotifyAuthService.CurrentSession.CachedPlaylists);
            return playlists;
        }

        if (string.IsNullOrWhiteSpace(accessToken)) return playlists;

        // 2. Fetch all 233+ playlists via spclient rootlist
        var userId = SpotifyAuthService.CurrentSession.UserProfile?.Id ?? "";
        if (string.IsNullOrWhiteSpace(userId) || userId == "me")
        {
            userId = "31eqlapeucvuhrrd42ywm55lptqq";
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://spclient.wg.spotify.com/playlist/v2/user/{userId.Trim()}/rootlist");
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
                    var playlistIds = new List<string>();
                    foreach (var it in items.EnumerateArray())
                    {
                        if (it.TryGetProperty("uri", out var uProp))
                        {
                            var u = uProp.GetString();
                            if (!string.IsNullOrEmpty(u) && u.Contains("playlist:"))
                            {
                                var pId = u.Substring(u.LastIndexOf(':') + 1);
                                if (!playlistIds.Contains(pId))
                                {
                                    playlistIds.Add(pId);
                                }
                            }
                        }
                    }

                    if (playlistIds.Count > 0)
                    {
                        progress?.Report($"Wczytywanie {playlistIds.Count} playlist użytkownika...");

                        var knownDict = new Dictionary<string, SpotifyPlaylistSummary>();
                        foreach (var cp in SpotifyAuthService.CurrentSession.CachedPlaylists)
                        {
                            if (!string.IsNullOrEmpty(cp.Id) && !knownDict.ContainsKey(cp.Id))
                            {
                                knownDict[cp.Id] = cp;
                            }
                        }

                        var unResolved = new List<SpotifyPlaylistSummary>();

                        foreach (var pId in playlistIds)
                        {
                            if (knownDict.TryGetValue(pId, out var existing) && !string.IsNullOrWhiteSpace(existing.Name))
                            {
                                playlists.Add(existing);
                            }
                            else if (_playlistMetadataCache.TryGetValue(pId, out var cachedMeta))
                            {
                                playlists.Add(new SpotifyPlaylistSummary
                                {
                                    Id = pId,
                                    Name = cachedMeta.Name,
                                    ImageUrl = cachedMeta.Thumb,
                                    OwnerName = "Moja playlista",
                                    IsPublic = true
                                });
                            }
                            else
                            {
                                var plItem = new SpotifyPlaylistSummary
                                {
                                    Id = pId,
                                    Name = $"Playlista ({pId.Substring(0, Math.Min(6, pId.Length))}...)",
                                    ImageUrl = "",
                                    OwnerName = "Moja playlista",
                                    IsPublic = true
                                };
                                playlists.Add(plItem);
                                unResolved.Add(plItem);
                            }
                        }

                        // Background resolver for playlist titles & covers
                        if (unResolved.Count > 0)
                        {
                            _ = Task.Run(async () =>
                            {
                                await Parallel.ForEachAsync(unResolved, new ParallelOptions { MaxDegreeOfParallelism = 15 }, async (pl, ct) =>
                                {
                                    try
                                    {
                                        var oUrl = $"https://open.spotify.com/oembed?url=https://open.spotify.com/playlist/{pl.Id}";
                                        using var oReq = new HttpRequestMessage(HttpMethod.Get, oUrl);
                                        var oResp = await _http.SendAsync(oReq, ct);
                                        if (oResp.IsSuccessStatusCode)
                                        {
                                            var oJson = await oResp.Content.ReadAsStringAsync(ct);
                                            using var oDoc = JsonDocument.Parse(oJson);
                                            var oTitle = oDoc.RootElement.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                                            var oThumb = oDoc.RootElement.TryGetProperty("thumbnail_url", out var thProp) ? thProp.GetString() ?? "" : "";

                                            if (!string.IsNullOrWhiteSpace(oTitle))
                                            {
                                                pl.Name = oTitle;
                                                pl.ImageUrl = oThumb;
                                                _playlistMetadataCache[pl.Id] = (oTitle, oThumb);
                                            }
                                        }
                                    }
                                    catch { }
                                });

                                SpotifyAuthService.CurrentSession.CachedPlaylists = playlists;
                                SpotifyAuthService.SaveSession(accessToken, 3600, SpotifyAuthService.CurrentSession.UserProfile, SpotifyAuthService.CurrentSession.SpDcCookie);
                            });
                        }

                        SpotifyAuthService.CurrentSession.CachedPlaylists = playlists;
                        SpotifyAuthService.SaveSession(accessToken, 3600, SpotifyAuthService.CurrentSession.UserProfile, SpotifyAuthService.CurrentSession.SpDcCookie);
                        return playlists;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"Spclient rootlist fetch failed: {ex.Message}", "SPOTIFY");
        }

        // 3. Fallback
        if (SpotifyAuthService.CurrentSession.CachedPlaylists.Count > 0)
        {
            playlists.AddRange(SpotifyAuthService.CurrentSession.CachedPlaylists);
        }

        return playlists;
    }

    public async Task<List<SpotifyTrackItem>> GetLikedSongsAsync(string accessToken, IProgress<string>? progress = null)
    {
        const string likedPlaylistId = "37i9dQZF1F5p3rmiWPIYgZ";
        progress?.Report("Wczytywanie pełnej kolekcji Polubionych utworów...");

        var plTracks = await GetPlaylistTracksAsync(likedPlaylistId, accessToken, progress);
        if (plTracks.Count > 0)
        {
            SpotifyAuthService.CurrentSession.CachedLikedSongs = plTracks;
            SpotifyAuthService.SaveSession(accessToken, 3600, SpotifyAuthService.CurrentSession.UserProfile, SpotifyAuthService.CurrentSession.SpDcCookie);
            return plTracks;
        }

        return new List<SpotifyTrackItem>();
    }

    public async Task<List<SpotifyTrackItem>> GetPlaylistTracksAsync(string playlistId, string accessToken, IProgress<string>? progress = null)
    {
        var tracks = new List<SpotifyTrackItem>();
        if (string.IsNullOrWhiteSpace(playlistId)) return tracks;

        // 1. Fetch complete track ID list via spclient (instant ~200ms response for all tracks)
        var trackUris = new List<string>();
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
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Debug($"Spclient playlist fetch error: {ex.Message}", "SPOTIFY");
            }
        }

        if (trackUris.Count == 0) return tracks;

        int totalCount = trackUris.Count;
        progress?.Report($"Pobieranie danych dla {totalCount} utworów...");

        // 2. High-Speed Batch Metadata Resolution (50 tracks per batch via Spotify API)
        var apiToken = await GetApiAccessTokenAsync();
        var resolvedDict = new ConcurrentDictionary<int, SpotifyTrackItem>();
        var unResolvedIndices = new List<(string Id, int Index)>();

        // Check in-memory cache first
        for (int i = 0; i < trackUris.Count; i++)
        {
            var tId = trackUris[i];
            if (_trackMetadataCache.TryGetValue(tId, out var cached))
            {
                resolvedDict[i] = new SpotifyTrackItem
                {
                    Id = tId,
                    TrackNumber = i + 1,
                    Title = cached.Title,
                    Artist = cached.Artist,
                    Album = cached.Album,
                    ImageUrl = cached.Thumb,
                    DurationString = cached.Duration,
                    IsSelected = true
                };
            }
            else
            {
                unResolvedIndices.Add((tId, i));
            }
        }

        if (unResolvedIndices.Count > 0 && !string.IsNullOrEmpty(apiToken))
        {
            // Split into batches of 50
            var batches = new List<List<(string Id, int Index)>>();
            for (int i = 0; i < unResolvedIndices.Count; i += 50)
            {
                batches.Add(unResolvedIndices.Skip(i).Take(50).ToList());
            }

            int processedCount = resolvedDict.Count;

            await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = 10 }, async (batch, ct) =>
            {
                try
                {
                    var idsString = string.Join(",", batch.Select(b => b.Id));
                    using var bReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/tracks?ids={idsString}");
                    bReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

                    var bResp = await _http.SendAsync(bReq, ct);
                    if (bResp.IsSuccessStatusCode)
                    {
                        var bJson = await bResp.Content.ReadAsStringAsync(ct);
                        using var bDoc = JsonDocument.Parse(bJson);

                        if (bDoc.RootElement.TryGetProperty("tracks", out var tArr) && tArr.ValueKind == JsonValueKind.Array)
                        {
                            int batchIdx = 0;
                            foreach (var trObj in tArr.EnumerateArray())
                            {
                                if (batchIdx >= batch.Count) break;
                                var target = batch[batchIdx++];

                                if (trObj.ValueKind == JsonValueKind.Object)
                                {
                                    var name = trObj.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                                    var artists = new List<string>();
                                    if (trObj.TryGetProperty("artists", out var artArr) && artArr.ValueKind == JsonValueKind.Array)
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

                                    var albumName = "";
                                    var imgUrl = "";
                                    if (trObj.TryGetProperty("album", out var alObj))
                                    {
                                        if (alObj.TryGetProperty("name", out var alNameProp)) albumName = alNameProp.GetString() ?? "";
                                        if (alObj.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0)
                                        {
                                            if (imgs[0].TryGetProperty("url", out var uProp)) imgUrl = uProp.GetString() ?? "";
                                        }
                                    }

                                    var durationMs = trObj.TryGetProperty("duration_ms", out var durProp) ? durProp.GetInt32() : 0;
                                    var durSpan = TimeSpan.FromMilliseconds(durationMs);
                                    var durStr = $"{(int)durSpan.TotalMinutes}:{durSpan.Seconds:D2}";
                                    var artistStr = artists.Count > 0 ? string.Join(", ", artists) : "Spotify";

                                    if (!string.IsNullOrWhiteSpace(name))
                                    {
                                        var item = new SpotifyTrackItem
                                        {
                                            Id = target.Id,
                                            TrackNumber = target.Index + 1,
                                            Title = name,
                                            Artist = artistStr,
                                            Album = albumName,
                                            ImageUrl = imgUrl,
                                            DurationString = durationMs > 0 ? durStr : "3:30",
                                            IsSelected = true
                                        };

                                        resolvedDict[target.Index] = item;
                                        _trackMetadataCache[target.Id] = (name, artistStr, albumName, imgUrl, item.DurationString);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.Debug($"Batch tracks fetch error: {ex.Message}", "SPOTIFY");
                }

                var done = Interlocked.Add(ref processedCount, batch.Count);
                progress?.Report($"Wczytano {Math.Min(done, totalCount)} z {totalCount} utworów...");
            });
        }

        // 3. Assemble complete in-order list
        for (int i = 0; i < totalCount; i++)
        {
            if (resolvedDict.TryGetValue(i, out var resolved))
            {
                tracks.Add(resolved);
            }
            else
            {
                var tId = trackUris[i];
                tracks.Add(new SpotifyTrackItem
                {
                    Id = tId,
                    TrackNumber = i + 1,
                    Title = $"Utwór {i + 1}",
                    Artist = "Spotify",
                    DurationString = "3:30",
                    IsSelected = true
                });
            }
        }

        return tracks;
    }
}
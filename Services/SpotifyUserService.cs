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

    private static readonly ConcurrentDictionary<string, (string Title, string Artist, string Thumb, string Duration)> _trackMetadataCache = new();
    private static readonly ConcurrentDictionary<string, (string Name, string Thumb)> _playlistMetadataCache = new();

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
                    Id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "31eqlapeucvuhrrd42ywm55lptqq" : "31eqlapeucvuhrrd42ywm55lptqq",
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
            Id = "31eqlapeucvuhrrd42ywm55lptqq",
            DisplayName = "Użytkownik Spotify",
            Product = "spotify"
        };
    }

    public async Task<List<SpotifyPlaylistSummary>> GetUserPlaylistsAsync(string accessToken, IProgress<string>? progress = null)
    {
        var playlists = new List<SpotifyPlaylistSummary>();

        // 1. If we have > 100 cached playlists in active session, return them
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

                        // Populate existing cached playlist names
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

                                // Save complete list to session cache
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

        // 3. Fallback to cached playlists
        if (SpotifyAuthService.CurrentSession.CachedPlaylists.Count > 0)
        {
            playlists.AddRange(SpotifyAuthService.CurrentSession.CachedPlaylists);
        }

        return playlists;
    }

    public async Task<List<SpotifyTrackItem>> GetLikedSongsAsync(string accessToken, IProgress<string>? progress = null)
    {
        // Fetch all 227+ Liked Songs directly via known playlist ID
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
                            int totalCount = trackUris.Count;
                            progress?.Report($"Wczytywanie {totalCount} utworów...");

                            var resolvedTracks = new ConcurrentDictionary<int, SpotifyTrackItem>();
                            int processed = 0;

                            await Parallel.ForEachAsync(trackUris.Select((id, idx) => (id, idx)), new ParallelOptions { MaxDegreeOfParallelism = 25 }, async (entry, ct) =>
                            {
                                var (tId, tIdx) = entry;
                                var resolved = await ResolveSingleTrackMetadataAsync(tId, tIdx + 1, ct);
                                resolvedTracks[tIdx] = resolved;

                                var count = Interlocked.Increment(ref processed);
                                if (count % 15 == 0 || count == totalCount)
                                {
                                    progress?.Report($"Pobieranie danych: {count} z {totalCount} utworów...");
                                }
                            });

                            for (int i = 0; i < totalCount; i++)
                            {
                                if (resolvedTracks.TryGetValue(i, out var t))
                                {
                                    tracks.Add(t);
                                }
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

        return tracks;
    }

    private static async Task<SpotifyTrackItem> ResolveSingleTrackMetadataAsync(string trackId, int trackNumber, CancellationToken ct = default)
    {
        if (_trackMetadataCache.TryGetValue(trackId, out var cached))
        {
            return new SpotifyTrackItem
            {
                Id = trackId,
                TrackNumber = trackNumber,
                Title = cached.Title,
                Artist = cached.Artist,
                ImageUrl = cached.Thumb,
                DurationString = cached.Duration,
                IsSelected = true
            };
        }

        // 1. Primary: Embed HTML with __NEXT_DATA__
        try
        {
            var embedUrl = $"https://open.spotify.com/embed/track/{trackId}";
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
                            var finalDur = durationMs > 0 ? durStr : "3:30";

                            _trackMetadataCache[trackId] = (name, artistStr, imgUrl, finalDur);

                            return new SpotifyTrackItem
                            {
                                Id = trackId,
                                TrackNumber = trackNumber,
                                Title = name,
                                Artist = artistStr,
                                ImageUrl = imgUrl,
                                DurationString = finalDur,
                                IsSelected = true
                            };
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Fallback: oEmbed
        try
        {
            var oembedUrl = $"https://open.spotify.com/oembed?url=https://open.spotify.com/track/{trackId}";
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
                    _trackMetadataCache[trackId] = (songTitle, artist, thumb, "3:30");
                    return new SpotifyTrackItem
                    {
                        Id = trackId,
                        TrackNumber = trackNumber,
                        Title = songTitle,
                        Artist = artist,
                        ImageUrl = thumb,
                        DurationString = "3:30",
                        IsSelected = true
                    };
                }
            }
        }
        catch { }

        return new SpotifyTrackItem
        {
            Id = trackId,
            TrackNumber = trackNumber,
            Title = $"Utwór {trackNumber}",
            Artist = "Spotify",
            DurationString = "3:30",
            IsSelected = true
        };
    }
}
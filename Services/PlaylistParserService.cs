using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExportX.Models;

namespace ExportX.Services;

public class ParsedTrackInfo
{
    public string QueryOrUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
}

public class PlaylistParserService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static string? _cachedSpotifyToken;
    private static DateTime _spotifyTokenExpiry = DateTime.MinValue;

    static PlaylistParserService()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }

    public static async Task<string?> GetSpotifyAccessTokenAsync(string? clientId = null, string? clientSecret = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(clientSecret))
        {
            var userToken = SpotifyAuthService.CurrentSession.AccessToken;
            if (!string.IsNullOrEmpty(userToken) && DateTime.UtcNow < SpotifyAuthService.CurrentSession.ExpirationUtc)
            {
                return userToken;
            }
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            var config = new ConfigService().Config;
            clientId = config.SpotifyClientId;
            clientSecret = config.SpotifyClientSecret;
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(_cachedSpotifyToken) && DateTime.UtcNow < _spotifyTokenExpiry)
        {
            return _cachedSpotifyToken;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            var authHeaderVal = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId.Trim()}:{clientSecret.Trim()}"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeaderVal);
            req.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var resp = await _httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var tokProp))
                {
                    _cachedSpotifyToken = tokProp.GetString();
                    int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
                    _spotifyTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
                    return _cachedSpotifyToken;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Błąd autoryzacji Spotify Client Credentials: {ex.Message}", "SPOTIFY");
        }

        return null;
    }

    public static async Task<(bool Success, string Message)> TestSpotifyCredentialsAsync(string clientId, string clientSecret)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            var authHeaderVal = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId.Trim()}:{clientSecret.Trim()}"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeaderVal);
            req.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            var resp = await _httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var tokProp) && !string.IsNullOrEmpty(tokProp.GetString()))
                {
                    return (true, "Połączenie z API Spotify udane! Obsługa nielimitowanych playlist (500+ utworów) aktywna.");
                }
            }
            return (false, $"Błąd uwierzytelnienia Spotify (Kod: {(int)resp.StatusCode}). Sprawdź Client ID i Client Secret.");
        }
        catch (Exception ex)
        {
            return (false, $"Błąd połączenia: {ex.Message}");
        }
    }

    public async Task<List<ParsedTrackInfo>> ParseInputAsync(string input, IProgress<string>? progress = null)
    {
        input = input.Trim();
        var results = new List<ParsedTrackInfo>();

        if (string.IsNullOrWhiteSpace(input)) return results;

        if (IsSpotifyUrl(input))
        {
            progress?.Report("Odpytywanie Spotify...");
            var spotifyTracks = await ParseSpotifyUrlAsync(input, progress);
            if (spotifyTracks.Count > 0)
            {
                return spotifyTracks;
            }
        }
        else if (IsYouTubePlaylist(input))
        {
            progress?.Report("Wczytywanie playlisty YouTube...");
            var ytTracks = await ParseYouTubePlaylistAsync(input, progress);
            if (ytTracks.Count > 0)
            {
                return ytTracks;
            }
        }
        else if (IsYouTubeVideo(input))
        {
            progress?.Report("Wczytywanie informacji o filmie YouTube...");
            var videoInfo = await ParseYouTubeVideoAsync(input);
            if (videoInfo != null)
            {
                results.Add(videoInfo);
                return results;
            }

            results.Add(new ParsedTrackInfo
            {
                QueryOrUrl = input,
                Title = input,
                Artist = ""
            });
            return results;
        }
        else if (IsDirectUrl(input))
        {
            results.Add(new ParsedTrackInfo
            {
                QueryOrUrl = input,
                Title = input,
                Artist = ""
            });
            return results;
        }

        // Split "Artist - Title" if present
        var (artist, title) = SplitArtistTitle(input);
        results.Add(new ParsedTrackInfo
        {
            QueryOrUrl = input,
            Title = string.IsNullOrWhiteSpace(title) ? input : title,
            Artist = artist
        });

        return results;
    }

    public static bool IsSpotifyUrl(string input)
    {
        return input.Contains("spotify.com/", StringComparison.OrdinalIgnoreCase) ||
               input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsYouTubePlaylist(string input)
    {
        return (input.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase))
               && (input.Contains("list=", StringComparison.OrdinalIgnoreCase) || input.Contains("/playlist", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsYouTubeVideo(string input)
    {
        return (input.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("music.youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
                input.Contains("youtube.com/shorts/", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDirectUrl(string input)
    {
        return input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static (string artist, string title) SplitArtistTitle(string raw)
    {
        if (raw.Contains(" - "))
        {
            var parts = raw.Split([" - "], 2, StringSplitOptions.TrimEntries);
            return (parts[0], parts[1]);
        }
        return (string.Empty, raw);
    }

    private async Task<ParsedTrackInfo?> ParseYouTubeVideoAsync(string videoUrl)
    {
        try
        {
            var ytDlp = ToolLocatorService.FindYtDlp();
            var psi = new ProcessStartInfo
            {
                FileName = ytDlp,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add("node");
            if (ConfigService.HasValidCookies())
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(ConfigService.GetCookiesPath());
            }
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("%(title)s\t%(uploader)s");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--no-update");
            psi.ArgumentList.Add(videoUrl);

            using var process = Process.Start(psi);
            if (process == null) return null;

            var line = await process.StandardOutput.ReadLineAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(line))
            {
                var parts = line.Split('\t');
                var fullTitle = parts.Length > 0 ? parts[0].Trim() : "";
                var uploader = parts.Length > 1 ? parts[1].Trim() : "";

                var (art, tit) = SplitArtistTitle(fullTitle);
                return new ParsedTrackInfo
                {
                    QueryOrUrl = videoUrl,
                    Title = string.IsNullOrEmpty(tit) ? fullTitle : tit,
                    Artist = string.IsNullOrEmpty(art) ? uploader : art
                };
            }
        }
        catch { }

        return null;
    }

    private async Task<List<ParsedTrackInfo>> ParseYouTubePlaylistAsync(string playlistUrl, IProgress<string>? progress)
    {
        var list = new List<ParsedTrackInfo>();
        try
        {
            var ytDlp = ToolLocatorService.FindYtDlp();
            var psi = new ProcessStartInfo
            {
                FileName = ytDlp,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            psi.ArgumentList.Add("--flat-playlist");
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add("node");
            if (ConfigService.HasValidCookies())
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(ConfigService.GetCookiesPath());
            }
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("%(title)s\t%(id)s\t%(uploader)s");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--no-update");
            psi.ArgumentList.Add(playlistUrl);

            using var process = Process.Start(psi);
            if (process == null) return list;

            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                var title = parts.Length > 0 ? parts[0].Trim() : "";
                var id = parts.Length > 1 ? parts[1].Trim() : "";
                var uploader = parts.Length > 2 ? parts[2].Trim() : "";

                if (string.IsNullOrEmpty(title) || title == "[Deleted video]" || title == "[Private video]") continue;

                var directUrl = !string.IsNullOrEmpty(id) ? $"https://www.youtube.com/watch?v={id}" : playlistUrl;
                var (art, tit) = SplitArtistTitle(title);

                list.Add(new ParsedTrackInfo
                {
                    QueryOrUrl = directUrl,
                    Title = string.IsNullOrEmpty(tit) ? title : tit,
                    Artist = string.IsNullOrEmpty(art) ? uploader : art
                });

                progress?.Report($"Wczytano z playlisty YouTube: {list.Count} utworów...");
            }

            await process.WaitForExitAsync();
        }
        catch
        {
            list.Clear();
            list.Add(new ParsedTrackInfo { QueryOrUrl = playlistUrl, Title = playlistUrl });
        }

        return list;
    }

    private async Task<List<ParsedTrackInfo>> ParseSpotifyUrlAsync(string spotifyUrl, IProgress<string>? progress)
    {
        var list = new List<ParsedTrackInfo>();

        try
        {
            var match = Regex.Match(spotifyUrl, @"(playlist|album|track)[/:]([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var type = match.Groups[1].Value.ToLowerInvariant();
                var id = match.Groups[2].Value;

                // 1. Try with Spotify Client Credentials (supports 500+ and 1000+ songs pagination!)
                var token = await GetSpotifyAccessTokenAsync();

                if (!string.IsNullOrEmpty(token))
                {
                    if (type == "playlist")
                    {
                        int offset = 0;
                        bool hasMore = true;

                        while (hasMore)
                        {
                            var apiReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/playlists/{id}/tracks?limit=100&offset={offset}");
                            apiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                            var apiResp = await _httpClient.SendAsync(apiReq);
                            if (!apiResp.IsSuccessStatusCode) break;

                            var apiJson = await apiResp.Content.ReadAsStringAsync();
                            using var apiDoc = JsonDocument.Parse(apiJson);

                            if (apiDoc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                            {
                                int batchCount = 0;
                                foreach (var item in items.EnumerateArray())
                                {
                                    if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
                                    {
                                        var name = track.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                                        var artistList = new List<string>();
                                        if (track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var a in artists.EnumerateArray())
                                            {
                                                if (a.TryGetProperty("name", out var an))
                                                {
                                                    var artistName = an.GetString();
                                                    if (!string.IsNullOrWhiteSpace(artistName)) artistList.Add(artistName);
                                                }
                                            }
                                        }

                                        if (!string.IsNullOrWhiteSpace(name))
                                        {
                                            var (dispArt, searchArt) = FileImportService.CleanArtistString(string.Join(";", artistList));
                                            var query = !string.IsNullOrWhiteSpace(searchArt) ? $"{searchArt} - {name}" : name;

                                            list.Add(new ParsedTrackInfo
                                            {
                                                QueryOrUrl = query,
                                                Title = name,
                                                Artist = dispArt
                                            });
                                            batchCount++;
                                        }
                                    }
                                }

                                progress?.Report($"Wczytano ze Spotify API: {list.Count} utworów...");

                                if (apiDoc.RootElement.TryGetProperty("next", out var nextProp) && nextProp.ValueKind != JsonValueKind.Null && batchCount > 0)
                                {
                                    offset += batchCount;
                                }
                                else
                                {
                                    hasMore = false;
                                }
                            }
                            else
                            {
                                hasMore = false;
                            }
                        }

                        if (list.Count > 0)
                        {
                            LogService.Success($"Wczytano {list.Count} utworów z playlisty Spotify przez API.", "SPOTIFY");
                            return list;
                        }
                    }
                    else if (type == "album")
                    {
                        int offset = 0;
                        bool hasMore = true;

                        while (hasMore)
                        {
                            var apiReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/albums/{id}/tracks?limit=50&offset={offset}");
                            apiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                            var apiResp = await _httpClient.SendAsync(apiReq);
                            if (!apiResp.IsSuccessStatusCode) break;

                            var apiJson = await apiResp.Content.ReadAsStringAsync();
                            using var apiDoc = JsonDocument.Parse(apiJson);

                            if (apiDoc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                            {
                                int batchCount = 0;
                                foreach (var track in items.EnumerateArray())
                                {
                                    var name = track.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                                    var artistList = new List<string>();
                                    if (track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var a in artists.EnumerateArray())
                                        {
                                            if (a.TryGetProperty("name", out var an))
                                            {
                                                var artistName = an.GetString();
                                                if (!string.IsNullOrWhiteSpace(artistName)) artistList.Add(artistName);
                                            }
                                        }
                                    }

                                    if (!string.IsNullOrWhiteSpace(name))
                                    {
                                        var (dispArt, searchArt) = FileImportService.CleanArtistString(string.Join(";", artistList));
                                        var query = !string.IsNullOrWhiteSpace(searchArt) ? $"{searchArt} - {name}" : name;

                                        list.Add(new ParsedTrackInfo
                                        {
                                            QueryOrUrl = query,
                                            Title = name,
                                            Artist = dispArt
                                        });
                                        batchCount++;
                                    }
                                }

                                progress?.Report($"Wczytano z albumu Spotify API: {list.Count} utworów...");

                                if (apiDoc.RootElement.TryGetProperty("next", out var nextProp) && nextProp.ValueKind != JsonValueKind.Null && batchCount > 0)
                                {
                                    offset += batchCount;
                                }
                                else
                                {
                                    hasMore = false;
                                }
                            }
                            else
                            {
                                hasMore = false;
                            }
                        }

                        if (list.Count > 0) return list;
                    }
                    else if (type == "track")
                    {
                        var apiReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/tracks/{id}");
                        apiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                        var apiResp = await _httpClient.SendAsync(apiReq);
                        if (apiResp.IsSuccessStatusCode)
                        {
                            var apiJson = await apiResp.Content.ReadAsStringAsync();
                            using var apiDoc = JsonDocument.Parse(apiJson);
                            var name = apiDoc.RootElement.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                            var artistList = new List<string>();
                            if (apiDoc.RootElement.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var a in artists.EnumerateArray())
                                {
                                    if (a.TryGetProperty("name", out var an))
                                    {
                                        var artistName = an.GetString();
                                        if (!string.IsNullOrWhiteSpace(artistName)) artistList.Add(artistName);
                                    }
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                var (dispArt, searchArt) = FileImportService.CleanArtistString(string.Join(";", artistList));
                                var query = !string.IsNullOrWhiteSpace(searchArt) ? $"{searchArt} - {name}" : name;

                                list.Add(new ParsedTrackInfo
                                {
                                    QueryOrUrl = query,
                                    Title = name,
                                    Artist = dispArt
                                });
                                return list;
                            }
                        }
                    }
                }
            }

            // 2. Fallback: Spotify oEmbed (for single tracks)
            if (spotifyUrl.Contains("/track/") || spotifyUrl.StartsWith("spotify:track:"))
            {
                var oembedUrl = $"https://open.spotify.com/oembed?url={Uri.EscapeDataString(spotifyUrl)}";
                var jsonStr = await _httpClient.GetStringAsync(oembedUrl);
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                {
                    var fullTitle = titleProp.GetString() ?? "";
                    var (artist, title) = SplitArtistTitle(fullTitle);
                    list.Add(new ParsedTrackInfo
                    {
                        QueryOrUrl = fullTitle,
                        Title = string.IsNullOrEmpty(title) ? fullTitle : title,
                        Artist = artist
                    });
                }
                return list;
            }

            // 3. Fallback: Spotify Embed HTML scraping
            var embedUrl = spotifyUrl;
            if (!embedUrl.Contains("/embed/"))
            {
                embedUrl = embedUrl.Replace("open.spotify.com/", "open.spotify.com/embed/");
            }

            var html = await _httpClient.GetStringAsync(embedUrl);

            var nextDataMatch = Regex.Match(html, @"<script\s+id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline);
            if (nextDataMatch.Success)
            {
                var nextJson = nextDataMatch.Groups[1].Value;
                using var doc = JsonDocument.Parse(nextJson);
                if (doc.RootElement.TryGetProperty("props", out var props) &&
                    props.TryGetProperty("pageProps", out var pageProps) &&
                    pageProps.TryGetProperty("state", out var state) &&
                    state.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("entity", out var entity) &&
                    entity.TryGetProperty("trackList", out var trackList) &&
                    trackList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var track in trackList.EnumerateArray())
                    {
                        var title = track.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        var subtitle = track.TryGetProperty("subtitle", out var s) ? s.GetString() ?? "" : "";

                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            var fullQuery = string.IsNullOrWhiteSpace(subtitle) ? title : $"{subtitle} - {title}";
                            list.Add(new ParsedTrackInfo
                            {
                                QueryOrUrl = fullQuery,
                                Title = title,
                                Artist = subtitle
                            });
                        }
                    }
                }
            }

            if (list.Count == 0)
            {
                var matches = Regex.Matches(html, @"""title""\s*:\s*""([^""]+)""\s*,\s*""subtitle""\s*:\s*""([^""]+)""");
                foreach (Match m in matches)
                {
                    var title = m.Groups[1].Value;
                    var subtitle = m.Groups[2].Value;
                    if (!string.IsNullOrWhiteSpace(title) && !list.Any(x => x.Title == title && x.Artist == subtitle))
                    {
                        list.Add(new ParsedTrackInfo
                        {
                            QueryOrUrl = $"{subtitle} - {title}",
                            Title = title,
                            Artist = subtitle
                        });
                    }
                }
            }

            if (list.Count > 0)
            {
                LogService.Info($"Wczytano {list.Count} utworów z publicznego widoku Spotify.", "SPOTIFY");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Błąd pobierania danych ze Spotify: {ex.Message}", "SPOTIFY");
        }

        if (list.Count == 0)
        {
            list.Add(new ParsedTrackInfo { QueryOrUrl = spotifyUrl, Title = spotifyUrl });
        }

        return list;
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ExportX.Models;
using ExportX.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace ExportX.Views;

public partial class SpotifyLoginWindow : Window
{
    private readonly DispatcherTimer _pollTimer = new();
    private readonly SpotifyUserService _spotifyUserService = new();
    private bool _isSuccess = false;
    private bool _isChecking = false;
    private readonly bool _autoExtract;

    public SpotifyLoginWindow(bool autoExtract = false)
    {
        InitializeComponent();
        _autoExtract = autoExtract;

        _pollTimer.Interval = TimeSpan.FromSeconds(2.0);
        _pollTimer.Tick += PollTimer_Tick;

        Loaded += SpotifyLoginWindow_Loaded;
        Closed += SpotifyLoginWindow_Closed;
    }

    private async void SpotifyLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
        if (_autoExtract)
        {
            ExtractFromExternalBrowser_Click(this, new RoutedEventArgs());
        }
    }

    private void SpotifyLoginWindow_Closed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var profilePath = ConfigService.GetWebViewProfilePath();
            var env = await CoreWebView2Environment.CreateAsync(null, profilePath);
            await LoginWebView.EnsureCoreWebView2Async(env);

            LoginWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            LoginWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            LoginWebView.CoreWebView2.NavigationCompleted += LoginWebView_NavigationCompleted;
            LoginWebView.CoreWebView2.SourceChanged += LoginWebView_SourceChanged;
            LoginWebView.CoreWebView2.WebMessageReceived += LoginWebView_WebMessageReceived;
            LoginWebView.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;

            LoadingOverlay.Visibility = Visibility.Collapsed;
            LoginWebView.CoreWebView2.Navigate("https://accounts.spotify.com/en/login?continue=https%3A%2F%2Fopen.spotify.com%2Fcollection%2Fplaylists");
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            LogService.Warn($"Nie udało się uruchomić WebView2 dla Spotify: {ex.Message}", "SPOTIFY");
            OverlayTitle.Text = "⚠️ Moduł przeglądarki WebView2";
            OverlaySubtitle.Text = "Możesz zalogować się w swojej normalnej przeglądarce i pobrać sesję jednym kliknięciem.";
            FallbackPanel.Visibility = Visibility.Visible;
            LoadingOverlay.Visibility = Visibility.Visible;
        }
    }

    private async Task<string?> GetSpDcCookieAsync()
    {
        try
        {
            if (LoginWebView.CoreWebView2 == null) return null;
            var list = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://spotify.com");
            var sp = list.FirstOrDefault(c => c.Name == "sp_dc");
            if (sp != null && !string.IsNullOrWhiteSpace(sp.Value)) return sp.Value;

            list = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.spotify.com");
            sp = list.FirstOrDefault(c => c.Name == "sp_dc");
            if (sp != null && !string.IsNullOrWhiteSpace(sp.Value)) return sp.Value;

            list = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://open.spotify.com");
            sp = list.FirstOrDefault(c => c.Name == "sp_dc");
            if (sp != null && !string.IsNullOrWhiteSpace(sp.Value)) return sp.Value;
        }
        catch { }
        return null;
    }

    private async void CoreWebView2_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (_isSuccess) return;

        try
        {
            var uri = e.Request?.Uri ?? "";

            // 1. Intercept response from get_access_token endpoint
            if (uri.Contains("get_access_token") && e.Response != null && e.Response.StatusCode == 200)
            {
                try
                {
                    var stream = await e.Response.GetContentAsync();
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        var body = await reader.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(body) && body.Contains("accessToken"))
                        {
                            await Dispatcher.InvokeAsync(async () => await ProcessTokenJsonAsync(body));
                            return;
                        }
                    }
                }
                catch { }
            }

            // 2. Intercept Authorization Bearer tokens if user is authenticated (has sp_dc cookie)
            if (e.Request != null && e.Request.Headers != null)
            {
                string? auth = null;
                if (e.Request.Headers.Contains("authorization")) auth = e.Request.Headers.GetHeader("authorization");
                else if (e.Request.Headers.Contains("Authorization")) auth = e.Request.Headers.GetHeader("Authorization");

                if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var token = auth.Substring("Bearer ".Length).Trim();
                    if (token.Length > 40 && (token.StartsWith("BQ") || token.Length > 100))
                    {
                        var spDc = await GetSpDcCookieAsync();
                        if (!string.IsNullOrWhiteSpace(spDc))
                        {
                            await Dispatcher.InvokeAsync(async () => await ProcessTokenAsync(token, 3600));
                            return;
                        }
                    }
                }
            }
        }
        catch { }
    }

    private async void LoginWebView_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        await CheckSessionStatusAsync();
    }

    private async void LoginWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch { }
            if (string.IsNullOrEmpty(msg))
            {
                try { msg = e.WebMessageAsJson; } catch { }
            }

            if (!string.IsNullOrEmpty(msg) && msg.Contains("accessToken"))
            {
                await Dispatcher.InvokeAsync(async () => await ProcessTokenJsonAsync(msg));
            }
        }
        catch { }
    }

    private async void LoginWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        await CheckSessionStatusAsync();
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        await CheckSessionStatusAsync();
    }

    private async Task CheckSessionStatusAsync()
    {
        if (_isSuccess || _isChecking || LoginWebView.CoreWebView2 == null) return;

        _isChecking = true;
        try
        {
            // 1. Synchronous check of session / config DOM elements
            const string syncScript = @"(() => {
                try {
                    var s = document.getElementById('session');
                    if (s && s.textContent) return s.textContent;
                } catch(e) {}
                try {
                    var c = document.getElementById('config');
                    if (c && c.textContent) return c.textContent;
                } catch(e) {}
                return '';
            })()";

            var domJson = await LoginWebView.CoreWebView2.ExecuteScriptAsync(syncScript);
            if (!string.IsNullOrWhiteSpace(domJson) && domJson != "null" && domJson != "\"\"" && domJson.Contains("accessToken"))
            {
                await ProcessTokenJsonAsync(domJson);
                if (_isSuccess) return;
            }

            // 2. Async fetch via postMessage
            const string script = @"(function() {
                try {
                    fetch('/get_access_token?reason=transport&productType=web_player')
                        .then(function(r) { return r.json(); })
                        .then(function(d) {
                            if (d && d.accessToken && !d.isAnonymous) {
                                window.chrome.webview.postMessage(JSON.stringify(d));
                            }
                        })
                        .catch(function(e) {});
                } catch(e) {}

                try {
                    var s = document.getElementById('session');
                    if (s && s.textContent) {
                        var d = JSON.parse(s.textContent);
                        if (d && d.accessToken && !d.isAnonymous) {
                            window.chrome.webview.postMessage(JSON.stringify(d));
                        }
                    }
                } catch(e) {}
            })();";

            _ = await LoginWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            LogService.Debug($"Błąd sprawdzania sesji: {ex.Message}", "SPOTIFY");
        }
        finally
        {
            _isChecking = false;
        }
    }

    private async Task ProcessTokenJsonAsync(string rawJson)
    {
        if (_isSuccess) return;

        try
        {
            string jsonToParse = rawJson.Trim();
            try
            {
                using var initialDoc = JsonDocument.Parse(jsonToParse);
                if (initialDoc.RootElement.ValueKind == JsonValueKind.String)
                {
                    jsonToParse = initialDoc.RootElement.GetString() ?? jsonToParse;
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(jsonToParse) && jsonToParse.Contains("accessToken"))
            {
                using var doc = JsonDocument.Parse(jsonToParse);
                var root = doc.RootElement;

                bool isAnonymous = root.TryGetProperty("isAnonymous", out var anonProp) && anonProp.GetBoolean();
                if (isAnonymous)
                {
                    // Guest/anonymous token - do NOT treat as logged in!
                    return;
                }

                var accessToken = root.TryGetProperty("accessToken", out var tokProp) ? tokProp.GetString() : null;
                int expiresIn = 3600;

                if (root.TryGetProperty("accessTokenExpirationTimestampMs", out var expProp))
                {
                    var expMs = expProp.GetInt64();
                    var totalSec = (expMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000;
                    if (totalSec > 60) expiresIn = (int)totalSec;
                }

                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    await ProcessTokenAsync(accessToken, expiresIn);
                }
            }
        }
        catch { }
    }

    private void SafeCloseWithSuccess()
    {
        try
        {
            if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            {
                DialogResult = true;
            }
        }
        catch { }

        try
        {
            Close();
        }
        catch { }
    }

    private async Task<List<SpotifyPlaylistSummary>> ExtractPlaylistsFromWebViewAsync()
    {
        var list = new List<SpotifyPlaylistSummary>();
        if (LoginWebView.CoreWebView2 == null) return list;

        const string extractScript = @"(() => {
            try {
                const list = [];
                const seen = new Set();

                function cleanText(t) {
                    if (!t) return '';
                    return t.replace(/[\r\n\t]+/g, ' ').replace(/\s+/g, ' ').trim();
                }

                function getImgSrc(el) {
                    if (!el) return '';
                    const img = el.querySelector('img');
                    if (!img) return '';
                    let src = img.currentSrc || img.src || '';
                    if (!src || src.startsWith('data:')) {
                        const srcset = img.getAttribute('srcset') || img.getAttribute('data-srcset');
                        if (srcset) {
                            const parts = srcset.split(',').map(s => s.trim().split(' ')[0]).filter(Boolean);
                            if (parts.length > 0) src = parts[parts.length - 1];
                        }
                    }
                    if (!src || src.startsWith('data:')) {
                        src = img.getAttribute('data-src') || '';
                    }
                    return src;
                }

                function isEditorial(title, subtitle) {
                    const t = (title + ' ' + subtitle).toLowerCase();
                    const editorialKeywords = [
                        'new music', 'radar', 'odkryj', 'top 50', 'top tracks', 'viral',
                        'editorial', 'hity', 'listy przebojów', 'polecane', 'popularne',
                        'hip hop alert', 'up next', 'alternatywna polska', 'najpopularniejsze',
                        'zrobiono dla', 'tylko dla ciebie', 'daily mix', 'radio'
                    ];
                    return editorialKeywords.some(k => t.includes(k));
                }

                function add(a, defaultOwner, forceInclude) {
                    try {
                        if (!a) return;
                        const href = a.getAttribute('href') || a.href || '';
                        const m = href.match(/\/playlist\/([a-zA-Z0-9]{15,35})/);
                        if (!m) return;
                        const id = m[1];
                        if (seen.has(id)) return;

                        let title = '';
                        const aria = a.getAttribute('aria-label');
                        if (aria && aria.trim().length > 0) {
                            title = cleanText(aria);
                        }
                        if (!title || title.toLowerCase() === 'playlista' || title.toLowerCase() === 'playlist') {
                            const textEl = a.querySelector('[data-encore-id=""text""], p, span, h3, h2, div');
                            if (textEl && textEl.textContent) title = cleanText(textEl.textContent);
                        }
                        if (!title || title.toLowerCase() === 'playlista' || title.toLowerCase() === 'playlist') {
                            title = cleanText(a.innerText || a.textContent || '');
                        }
                        if (!title || title.toLowerCase() === 'playlista' || title.toLowerCase() === 'playlist') {
                            const img = a.querySelector('img');
                            if (img && img.alt) title = cleanText(img.alt);
                        }

                        if (title.includes('\n')) {
                            title = title.split('\n')[0].trim();
                        }

                        if (!title || title.length < 2 || title.toLowerCase() === 'playlista' || title.toLowerCase() === 'playlist') {
                            return;
                        }

                        // Determine subtitle / owner
                        let sub = '';
                        const subEl = a.closest('div[role=""listitem""], div[role=""row""], div[data-testid=""rootlist-item""]')?.querySelector('span[data-encore-id=""text""]:not(:first-child), p:not(:first-child)');
                        if (subEl && subEl.textContent) sub = cleanText(subEl.textContent);

                        if (!forceInclude && isEditorial(title, sub)) {
                            return;
                        }

                        let imgUrl = getImgSrc(a) || getImgSrc(a.closest('div[role=""listitem""], div[role=""row""], div[data-testid=""rootlist-item""], div[data-testid=""card-click-handler""]')) || getImgSrc(a.parentElement);

                        seen.add(id);
                        list.push({
                            Id: id,
                            Name: title,
                            ImageUrl: imgUrl,
                            OwnerName: defaultOwner || (sub ? sub : 'Moja playlista'),
                            IsPublic: true
                        });
                    } catch(e) {}
                }

                // 1. Sidebar Library
                const sidebar = document.querySelector('nav, aside, div[data-testid=""rootlist-container""], div[aria-label*=""Biblioteka"" i], div[aria-label*=""Library"" i]');
                if (sidebar) {
                    sidebar.querySelectorAll('a[href*=""/playlist/""]').forEach(a => add(a, 'Biblioteka', true));
                }

                // 2. Collection main view
                document.querySelectorAll('main div[data-testid=""grid-container""] a[href*=""/playlist/""], main div[role=""grid""] a[href*=""/playlist/""]').forEach(a => {
                    add(a, 'Moja playlista', true);
                });

                // 3. Shortcuts on Home
                document.querySelectorAll('div[data-testid=""shortcut-item""] a[href*=""/playlist/""]').forEach(a => {
                    add(a, 'Skrót', false);
                });

                return JSON.stringify(list);
            } catch(e) {
                return '[]';
            }
        })()";

        try
        {
            var json = await LoginWebView.CoreWebView2.ExecuteScriptAsync(extractScript);
            var parsed = ParseScriptResultPlaylists(json);
            if (parsed.Count > 0)
            {
                list.AddRange(parsed);
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"Błąd ekstrakcji playlist: {ex.Message}", "SPOTIFY");
        }

        return list;
    }

    private async Task<List<SpotifyTrackItem>> ExtractLikedSongsFromWebViewAsync()
    {
        var tracks = new List<SpotifyTrackItem>();
        if (LoginWebView.CoreWebView2 == null) return tracks;

        const string script = @"(() => {
            try {
                const list = [];
                const seen = new Set();
                const rows = document.querySelectorAll('div[data-testid=""tracklist-row""]');
                let idx = 1;

                function getImgSrc(el) {
                    if (!el) return '';
                    const img = el.querySelector('img');
                    if (!img) return '';
                    let src = img.currentSrc || img.src || '';
                    if (!src || src.startsWith('data:')) {
                        const srcset = img.getAttribute('srcset') || img.getAttribute('data-srcset');
                        if (srcset) {
                            const parts = srcset.split(',').map(s => s.trim().split(' ')[0]).filter(Boolean);
                            if (parts.length > 0) src = parts[parts.length - 1];
                        }
                    }
                    return src;
                }

                rows.forEach(row => {
                    try {
                        // Title: strictly from the track link
                        const trackLink = row.querySelector('a[href*=""/track/""], a[data-testid=""internal-track-link""]');
                        let title = trackLink ? trackLink.textContent.trim() : '';

                        if (!title) {
                            const titleCol = row.querySelector('div[aria-colindex=""2""] div[dir=""auto""], div[aria-colindex=""2""] span');
                            if (titleCol) title = titleCol.textContent.trim();
                        }

                        // Artists: all links to /artist/
                        const artistLinks = row.querySelectorAll('a[href*=""/artist/""]');
                        let artists = [];
                        artistLinks.forEach(a => {
                            const t = a.textContent.trim();
                            if (t && !artists.includes(t)) artists.push(t);
                        });
                        let artist = artists.join(', ');

                        if (!artist) {
                            const artSpan = row.querySelector('span[data-encore-id=""text""]');
                            if (artSpan) artist = artSpan.textContent.trim();
                        }

                        // Album
                        const albumLink = row.querySelector('a[href*=""/album/""]');
                        let album = albumLink ? albumLink.textContent.trim() : '';

                        // Duration
                        const durEl = row.querySelector('div[data-encore-id=""text""]:last-child, div[aria-colindex=""5""], div[aria-colindex=""4""]');
                        let duration = '3:30';
                        if (durEl) {
                            const m = durEl.textContent.match(/\d+:\d{2}/);
                            if (m) duration = m[0];
                        }

                        let img = getImgSrc(row);

                        const dedupeKey = (title + '|' + artist).toLowerCase();
                        if (title && title !== 'Tytuł' && title !== 'Title' && !seen.has(dedupeKey)) {
                            seen.add(dedupeKey);
                            list.push({
                                Id: 'liked_' + idx,
                                TrackNumber: idx,
                                Title: title,
                                Artist: artist || 'Nieznany wykonawca',
                                Album: album,
                                DurationString: duration,
                                ImageUrl: img,
                                IsSelected: true
                            });
                            idx++;
                        }
                    } catch(e) {}
                });
                return JSON.stringify(list);
            } catch(e) { return '[]'; }
        })()";

        try
        {
            var json = await LoginWebView.CoreWebView2.ExecuteScriptAsync(script);
            if (!string.IsNullOrWhiteSpace(json) && json != "null" && json != "\"[]\"")
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    var inner = root.GetString();
                    if (!string.IsNullOrWhiteSpace(inner))
                    {
                        var items = JsonSerializer.Deserialize<List<SpotifyTrackItem>>(inner);
                        if (items != null) tracks.AddRange(items);
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    var items = JsonSerializer.Deserialize<List<SpotifyTrackItem>>(json);
                    if (items != null) tracks.AddRange(items);
                }
            }
        }
        catch { }

        return tracks;
    }

    private static List<SpotifyPlaylistSummary> ParseScriptResultPlaylists(string? rawJson)
    {
        var list = new List<SpotifyPlaylistSummary>();
        if (string.IsNullOrWhiteSpace(rawJson) || rawJson == "null" || rawJson == "\"[]\"" || rawJson == "[]")
            return list;

        try
        {
            string json = rawJson.Trim();
            using var doc1 = JsonDocument.Parse(json);
            
            JsonElement root = doc1.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner) || inner == "[]") return list;
                using var doc2 = JsonDocument.Parse(inner);
                root = doc2.RootElement.Clone();
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;

                    var id = "";
                    if (el.TryGetProperty("Id", out var idProp) || el.TryGetProperty("id", out idProp))
                        id = idProp.GetString() ?? "";

                    var name = "";
                    if (el.TryGetProperty("Name", out var nameProp) || el.TryGetProperty("name", out nameProp))
                        name = nameProp.GetString() ?? "";

                    var imgUrl = "";
                    if (el.TryGetProperty("ImageUrl", out var imgProp) || el.TryGetProperty("imageUrl", out imgProp) || el.TryGetProperty("image_url", out imgProp))
                        imgUrl = imgProp.GetString() ?? "";

                    var owner = "";
                    if (el.TryGetProperty("OwnerName", out var ownProp) || el.TryGetProperty("ownerName", out ownProp) || el.TryGetProperty("owner", out ownProp))
                        owner = ownProp.GetString() ?? "";

                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        if (!list.Any(p => p.Id == id))
                        {
                            list.Add(new SpotifyPlaylistSummary
                            {
                                Id = id,
                                Name = name,
                                ImageUrl = imgUrl,
                                OwnerName = string.IsNullOrWhiteSpace(owner) ? "Moja playlista" : owner,
                                IsPublic = true
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"Błąd parsowania playlist ze skryptu: {ex.Message}", "SPOTIFY");
        }

        return list;
    }

    private async Task ProcessTokenAsync(string accessToken, int expiresIn)
    {
        if (_isSuccess) return;

        var profile = await _spotifyUserService.GetUserProfileAsync(accessToken);
        if (profile != null && profile.DisplayName == "Micael Widell")
        {
            // Ignore demo/guest curator account
            return;
        }

        _isSuccess = true;
        _pollTimer.Stop();

        await Dispatcher.InvokeAsync(async () =>
        {
            StatusBadgeText.Text = "✅ ZALOGOWANO POMYŚLNIE!";
            StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
            StatusNote.Text = "Wczytywanie Twoich prywatnych playlist...";

            string? spDc = await GetSpDcCookieAsync();
            List<SpotifyPlaylistSummary> extractedPlaylists = new();
            List<SpotifyTrackItem> extractedLikedSongs = new();

            // Navigate to open.spotify.com/collection/playlists if not already there, to ensure user's actual library is rendered
            var currentUrl = LoginWebView.Source?.ToString() ?? "";
            if (!currentUrl.Contains("collection/playlists") && !currentUrl.Contains("open.spotify.com"))
            {
                LoginWebView.CoreWebView2?.Navigate("https://open.spotify.com/collection/playlists");
            }

            // Retry playlist extraction up to 4 times to let React DOM render
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(1000);
                extractedPlaylists = await ExtractPlaylistsFromWebViewAsync();
                if (extractedPlaylists.Count > 0) break;
            }

            // Also extract liked tracks if on tracks view
            extractedLikedSongs = await ExtractLikedSongsFromWebViewAsync();

            // Also check profile avatar / name from DOM if default
            if (profile == null || profile.DisplayName == "Użytkownik Spotify")
            {
                try
                {
                    if (LoginWebView.CoreWebView2 != null)
                    {
                        const string userScript = @"(() => {
                            try {
                                const btn = document.querySelector('button[data-testid=""user-widget-link""]') || document.querySelector('figure[data-testid=""user-widget-avatar""]');
                                const img = btn?.querySelector('img')?.src || '';
                                const name = btn?.getAttribute('aria-label') || '';
                                return JSON.stringify({ name: name, avatar: img });
                            } catch(e) { return '{}'; }
                        })()";
                        var userJson = await LoginWebView.CoreWebView2.ExecuteScriptAsync(userScript);
                        if (!string.IsNullOrWhiteSpace(userJson) && userJson != "null")
                        {
                            using var uDoc = JsonDocument.Parse(userJson);
                            JsonElement uRoot = uDoc.RootElement;
                            if (uRoot.ValueKind == JsonValueKind.String)
                            {
                                var inner = uRoot.GetString();
                                if (!string.IsNullOrEmpty(inner))
                                {
                                    using var uDoc2 = JsonDocument.Parse(inner);
                                    uRoot = uDoc2.RootElement.Clone();
                                }
                            }
                            if (uRoot.ValueKind == JsonValueKind.Object)
                            {
                                var uName = uRoot.TryGetProperty("name", out var nP) ? nP.GetString() : null;
                                var uAvatar = uRoot.TryGetProperty("avatar", out var aP) ? aP.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(uName))
                                {
                                    profile = new SpotifyUserProfile
                                    {
                                        Id = "me",
                                        DisplayName = uName.Replace("Konto użytkownika ", "").Replace("User account ", "").Trim(),
                                        AvatarUrl = uAvatar ?? "",
                                        Product = "spotify"
                                    };
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            SpotifyAuthService.SaveSession(accessToken, expiresIn, profile, spDc, extractedPlaylists, extractedLikedSongs);

            MessageBox.Show(
                $"Zalogowano pomyślnie do Spotify jako: {profile?.DisplayName ?? "Użytkownik"}!\nZnaleziono {extractedPlaylists.Count} playlist z Twojego konta.",
                "Logowanie Spotify udane",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            SafeCloseWithSuccess();
        });
    }

    private async void SaveSessionFromWebView_Click(object sender, RoutedEventArgs e)
    {
        if (LoginWebView.CoreWebView2 == null) return;

        StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonCyan");
        StatusBadgeText.Text = "⏳ WERYFIKACJA...";
        StatusNote.Text = "Pobieranie aktywnej sesji Spotify...";

        // 1. Check if we have token from synchronous DOM
        await CheckSessionStatusAsync();
        if (_isSuccess) return;

        // 2. Read cookies
        var spDc = await GetSpDcCookieAsync();

        var currentUrl = LoginWebView.Source?.ToString() ?? "";
        if (!currentUrl.Contains("open.spotify.com"))
        {
            LoginWebView.CoreWebView2.Navigate("https://open.spotify.com/collection/playlists");
            StatusNote.Text = "Przekierowywanie do Twojej biblioteki Spotify...";
            await Task.Delay(1500);
        }

        // Execute force postMessage script
        const string forceScript = @"(function() {
            try {
                fetch('/get_access_token?reason=transport&productType=web_player')
                    .then(function(r) { return r.json(); })
                    .then(function(d) {
                        if (d && d.accessToken) {
                            window.chrome.webview.postMessage(JSON.stringify(d));
                        }
                    });
            } catch(e) {}
        })();";
        _ = LoginWebView.CoreWebView2.ExecuteScriptAsync(forceScript);

        // Check if token exists in session data or cookies
        await Task.Delay(1000);
        await CheckSessionStatusAsync();

        if (!_isSuccess && !string.IsNullOrWhiteSpace(SpotifyAuthService.CurrentSession.AccessToken))
        {
            // Already have valid token, extract playlists & liked songs
            var playlists = await ExtractPlaylistsFromWebViewAsync();
            var likedTracks = await ExtractLikedSongsFromWebViewAsync();

            SpotifyAuthService.SaveSession(
                SpotifyAuthService.CurrentSession.AccessToken, 
                3600, 
                SpotifyAuthService.CurrentSession.UserProfile, 
                spDc, 
                playlists, 
                likedTracks);

            MessageBox.Show(
                $"Zsynchronizowano sesję Spotify!\nZnaleziono {playlists.Count} playlist.",
                "Logowanie Spotify udane",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            SafeCloseWithSuccess();
            return;
        }
    }

    private async Task<bool> InjectCookiesIntoWebViewAsync(string cookiesFilePath)
    {
        if (LoginWebView.CoreWebView2 == null || !File.Exists(cookiesFilePath)) return false;

        try
        {
            var cookieManager = LoginWebView.CoreWebView2.CookieManager;
            var lines = await File.ReadAllLinesAsync(cookiesFilePath);
            int count = 0;

            foreach (var line in lines)
            {
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 7)
                {
                    var domain = parts[0];
                    var path = parts[2];
                    var isSecure = parts[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                    var name = parts[5];
                    var val = parts[6];

                    if (domain.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var cleanDomain = domain.TrimStart('.');
                            var cookie = cookieManager.CreateCookie(name, val, cleanDomain, path);
                            cookie.IsSecure = isSecure;
                            cookieManager.AddOrUpdateCookie(cookie);
                            count++;
                        }
                        catch { }
                    }
                }
            }

            if (count > 0)
            {
                LoginWebView.CoreWebView2.Navigate("https://open.spotify.com/");
                return true;
            }
        }
        catch { }

        return false;
    }

    private async void ExtractFromExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var targetBrowser = AuthService.DetectActiveOrTargetBrowser();
            var procName = AuthService.GetProcessNameForBrowser(targetBrowser);
            var isRunning = Process.GetProcessesByName(procName).Length > 0;

            if (isRunning)
            {
                var ask = MessageBox.Show(
                    "Czy chcesz, aby ExportX zrestartował przeglądarkę i zautoryzował twoje konto Spotify?\n\nTwoje otwarte karty zostaną automatycznie przywrócone!",
                    "Autoryzacja konta Spotify",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (ask == MessageBoxResult.Yes)
                {
                    StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonCyan");
                    StatusBadgeText.Text = "⏳ POŁĄCZENIE W TOKU...";
                    StatusNote.Text = $"Pobieranie sesji Spotify z {targetBrowser.ToUpperInvariant()}...";

                    var (ok, msg, path) = await AuthService.ExtractSpotifyCookiesRawFileAsync(targetBrowser, progress =>
                    {
                        Dispatcher.Invoke(() => StatusNote.Text = progress);
                    });

                    if (ok && !string.IsNullOrEmpty(path))
                    {
                        StatusNote.Text = "Weryfikowanie sesji w silniku przeglądarki...";
                        var injected = await InjectCookiesIntoWebViewAsync(path);
                        if (injected)
                        {
                            StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonCyan");
                            StatusBadgeText.Text = "⏳ LOGOWANIE...";
                            StatusNote.Text = "Wczytywanie konta Spotify...";
                            return;
                        }

                        // Fallback verification
                        var (tokOk, tokMsg, profile) = await AuthService.GetSpotifyTokenFromCookiesFileAsync(path);
                        if (tokOk)
                        {
                            StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
                            StatusBadgeText.Text = "🟢 ZALOGOWANO POMYŚLNIE!";
                            MessageBox.Show($"🎉 Sukces! Pomyślnie połączono Twoje konto Spotify z przeglądarki {targetBrowser.ToUpperInvariant()}!\nZalogowano jako: {profile?.DisplayName}", "Zalogowano do Spotify", MessageBoxButton.OK, MessageBoxImage.Information);
                            SafeCloseWithSuccess();
                            return;
                        }
                    }

                    StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonPink");
                    StatusBadgeText.Text = "⚠️ BŁĄD POBIERANIA";
                    MessageBox.Show(msg, "Błąd pobierania sesji Spotify", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                StatusBadgeText.Text = "OCZEKIWANIE NA LOGOWANIE";
                StatusNote.Text = "Wskazówka: Zaloguj się w oknie powyżej lub użyj 'IMPORTUJ COOKIES.TXT'.";
                return;
            }

            // Browser is not running -> extract directly
            StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonCyan");
            StatusBadgeText.Text = "⏳ POBIERANIE SESJI...";
            StatusNote.Text = $"Pobieranie danych Spotify z {targetBrowser.ToUpperInvariant()}...";

            var (directOk, directMsg, directPath) = await AuthService.ExtractSpotifyCookiesRawFileAsync(targetBrowser, progress =>
            {
                Dispatcher.Invoke(() => StatusNote.Text = progress);
            });

            if (directOk && !string.IsNullOrEmpty(directPath))
            {
                StatusNote.Text = "Weryfikowanie sesji w silniku przeglądarki...";
                var injected = await InjectCookiesIntoWebViewAsync(directPath);
                if (injected)
                {
                    StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonCyan");
                    StatusBadgeText.Text = "⏳ LOGOWANIE...";
                    StatusNote.Text = "Wczytywanie konta Spotify...";
                    return;
                }

                var (tokOk, tokMsg, directProfile) = await AuthService.GetSpotifyTokenFromCookiesFileAsync(directPath);
                if (tokOk)
                {
                    StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
                    StatusBadgeText.Text = "🟢 ZALOGOWANO POMYŚLNIE!";
                    MessageBox.Show($"🎉 Sukces! Pomyślnie połączono Twoje konto Spotify z przeglądarki {targetBrowser.ToUpperInvariant()}!\nZalogowano jako: {directProfile?.DisplayName}", "Zalogowano do Spotify", MessageBoxButton.OK, MessageBoxImage.Information);
                    SafeCloseWithSuccess();
                    return;
                }
            }

            StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonPink");
            StatusBadgeText.Text = "⚠️ BŁĄD POBIERANIA";
            MessageBox.Show(directMsg, "Informacja", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Błąd podczas pobierania sesji z przeglądarki: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportCookiesFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ofd = new OpenFileDialog
            {
                Title = "Wybierz plik ciasteczek Spotify (cookies.txt)",
                Filter = "Pliki tekstowe cookies (*.txt)|*.txt|Wszystkie pliki (*.*)|*.*",
                Multiselect = false
            };

            if (ofd.ShowDialog(this) == true)
            {
                StatusNote.Text = "Wczytywanie pliku ciasteczek Spotify...";
                var injected = await InjectCookiesIntoWebViewAsync(ofd.FileName);
                if (injected)
                {
                    StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonCyan");
                    StatusBadgeText.Text = "⏳ LOGOWANIE...";
                    StatusNote.Text = "Wczytywanie konta Spotify z ciasteczek...";
                    return;
                }

                var (ok, msg, profile) = await AuthService.GetSpotifyTokenFromCookiesFileAsync(ofd.FileName);
                if (ok)
                {
                    StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
                    StatusBadgeText.Text = "🟢 ZALOGOWANO POMYŚLNIE!";
                    MessageBox.Show($"Pomyślnie zaimportowano ciasteczka Spotify!\nZalogowano jako: {profile?.DisplayName}", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
                    SafeCloseWithSuccess();
                }
                else
                {
                    MessageBox.Show(msg, "Błąd importu ciasteczek", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Błąd podczas importowania pliku: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PasteSpDc_Click(object sender, RoutedEventArgs e)
    {
        var clipboardText = "";
        try
        {
            if (Clipboard.ContainsText()) clipboardText = Clipboard.GetText().Trim();
        }
        catch { }

        var promptWin = new PasteListDialog(DownloadFormat.MP3, AudioBitrate.B320)
        {
            Owner = this,
            Title = "🍪 WPROWADŹ CIASTECZKO SP_DC SPOTIFY"
        };

        if (promptWin.ShowDialog() == true && promptWin.ResultLines.Count > 0)
        {
            var input = promptWin.ResultLines[0].Trim();
            var match = System.Text.RegularExpressions.Regex.Match(input, @"(?:sp_dc=)?([A-Za-z0-9_\-\.%+=]+)");
            var spDcVal = match.Success ? match.Groups[1].Value : input;

            StatusNote.Text = "Weryfikowanie ciasteczka sp_dc...";
            var (ok, msg, profile) = await AuthService.GetSpotifyTokenFromSpDcAsync(spDcVal);

            if (ok)
            {
                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
                StatusBadgeText.Text = "🟢 ZALOGOWANO POMYŚLNIE!";
                MessageBox.Show($"Ciasteczko sp_dc zweryfikowane pomyślnie!\nZalogowano jako: {profile?.DisplayName}", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
                SafeCloseWithSuccess();
            }
            else
            {
                MessageBox.Show(msg, "Błąd ciasteczka sp_dc", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OpenInExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = "https://accounts.spotify.com/en/login?continue=https%3A%2F%2Fopen.spotify.com%2Fcollection%2Fplaylists";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogService.Info("Otwarto oficjalną stronę logowania Spotify w przeglądarce.", "SPOTIFY");
        }
        catch { }
    }

    private void RefreshPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoginWebView.CoreWebView2?.Reload();
        }
        catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            {
                DialogResult = false;
            }
        }
        catch { }
        try { Close(); } catch { }
    }
}

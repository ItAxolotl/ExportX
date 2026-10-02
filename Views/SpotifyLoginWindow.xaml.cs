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
            LoginWebView.CoreWebView2.Navigate("https://accounts.spotify.com/en/login?continue=https%3A%2F%2Fopen.spotify.com%2F");
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

            string? spDc = await GetSpDcCookieAsync();
            List<SpotifyPlaylistSummary> extractedPlaylists = new();

            try
            {
                if (LoginWebView.CoreWebView2 != null)
                {
                    const string extractScript = @"(() => {
                        try {
                            const list = [];
                            const seen = new Set();

                            function add(a, tag) {
                                try {
                                    const href = a.getAttribute('href') || '';
                                    const m = href.match(/\/playlist\/([a-zA-Z0-9]+)/);
                                    if (!m) return;
                                    const id = m[1];
                                    if (seen.has(id)) return;

                                    let title = (a.getAttribute('aria-label') || a.innerText || a.textContent || '').trim();
                                    const lines = title.split('\n').map(l => l.trim()).filter(l => l.length > 0);
                                    if (lines.length > 0) title = lines[0];
                                    if (!title || title.toLowerCase() === 'playlist' || title.toLowerCase() === 'playlista') return;

                                    let img = '';
                                    const imgEl = a.querySelector('img') || a.closest('div')?.querySelector('img') || a.parentElement?.querySelector('img');
                                    if (imgEl && imgEl.src) img = imgEl.src;

                                    seen.add(id);
                                    list.push({
                                        Id: id,
                                        Name: title,
                                        ImageUrl: img,
                                        OwnerName: tag || 'Moja playlista',
                                        IsPublic: true
                                    });
                                } catch(e) {}
                            }

                            // 1. Home quick-access shortcut grid (the 8 main user playlists)
                            const shortcuts = document.querySelectorAll('div[data-testid=""grid-container""] a[href*=""/playlist/""], div[data-testid=""shortcut-item""] a[href*=""/playlist/""], div[role=""grid""] a[href*=""/playlist/""]');
                            shortcuts.forEach(a => add(a, 'Moja playlista'));

                            // 2. Left sidebar library
                            const libraryLinks = document.querySelectorAll('nav[aria-label*=""biblioteka"" i] a[href*=""/playlist/""], nav[aria-label*=""library"" i] a[href*=""/playlist/""], div[data-testid=""rootlist-container""] a[href*=""/playlist/""], div[aria-label*=""biblioteka"" i] a[href*=""/playlist/""]');
                            libraryLinks.forEach(a => add(a, 'Biblioteka'));

                            // 3. Fallback: Any other non-promo playlists
                            document.querySelectorAll('a[href*=""/playlist/""]').forEach(a => {
                                const section = a.closest('section');
                                const heading = section?.querySelector('h2, [data-encore-id=""text""]')?.textContent || '';
                                if (heading.match(/New Music|Radar|Odkryj|Popularne|Top|Listy przeboj\xF3w|Polecane|Editorial/i)) return;
                                add(a, 'Playlista');
                            });

                            return JSON.stringify(list);
                        } catch(e) {
                            return '[]';
                        }
                    })()";

                    var json = await LoginWebView.CoreWebView2.ExecuteScriptAsync(extractScript);
                    if (!string.IsNullOrWhiteSpace(json) && json != "null" && json != "\"[]\"")
                    {
                        var unescaped = JsonSerializer.Deserialize<string>(json);
                        if (!string.IsNullOrWhiteSpace(unescaped))
                        {
                            var items = JsonSerializer.Deserialize<List<SpotifyPlaylistSummary>>(unescaped);
                            if (items != null && items.Count > 0)
                            {
                                extractedPlaylists.AddRange(items);
                            }
                        }
                    }
                }
            }
            catch { }

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
                            var unescaped = JsonSerializer.Deserialize<string>(userJson);
                            if (!string.IsNullOrWhiteSpace(unescaped))
                            {
                                using var uDoc = JsonDocument.Parse(unescaped);
                                var uName = uDoc.RootElement.TryGetProperty("name", out var nP) ? nP.GetString() : null;
                                var uAvatar = uDoc.RootElement.TryGetProperty("avatar", out var aP) ? aP.GetString() : null;
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

            SpotifyAuthService.SaveSession(accessToken, expiresIn, profile, spDc, extractedPlaylists);

            MessageBox.Show(
                $"Zalogowano pomyślnie do Spotify jako: {profile?.DisplayName ?? "Użytkownik"}!\nTwoje playlisty i polubione utwory są teraz dostępne.",
                "Logowanie Spotify udane",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            DialogResult = true;
            Close();
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
            LoginWebView.CoreWebView2.Navigate("https://open.spotify.com/");
            StatusNote.Text = "Przekierowywanie do odtwarzacza Spotify...";
        }
        else
        {
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

            // Also reload if not captured within 500ms
            await Task.Delay(500);
            if (!_isSuccess)
            {
                LoginWebView.CoreWebView2.Reload();
            }
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
                            DialogResult = true;
                            Close();
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
                    DialogResult = true;
                    Close();
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
                    DialogResult = true;
                    Close();
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
                DialogResult = true;
                Close();
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
            var url = "https://accounts.spotify.com/en/login?continue=https%3A%2F%2Fopen.spotify.com%2F";
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
        DialogResult = false;
        Close();
    }
}

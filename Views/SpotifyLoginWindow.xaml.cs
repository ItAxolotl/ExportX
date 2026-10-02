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
            // Query Spotify Web internal token endpoint
            const string script = @"(async () => {
                try {
                    const res = await fetch('https://open.spotify.com/get_access_token', { credentials: 'include' });
                    if (res.ok) {
                        const data = await res.json();
                        return JSON.stringify(data);
                    }
                } catch(e) {}
                return 'null';
            })()";

            var resultJsonStr = await LoginWebView.CoreWebView2.ExecuteScriptAsync(script);
            if (!string.IsNullOrWhiteSpace(resultJsonStr) && resultJsonStr != "null" && resultJsonStr != "\"null\"")
            {
                var innerJson = resultJsonStr.StartsWith("\"") && resultJsonStr.EndsWith("\"")
                    ? JsonSerializer.Deserialize<string>(resultJsonStr)
                    : resultJsonStr;

                if (!string.IsNullOrWhiteSpace(innerJson))
                {
                    using var doc = JsonDocument.Parse(innerJson);
                    var root = doc.RootElement;

                    bool isAnonymous = root.TryGetProperty("isAnonymous", out var anonProp) && anonProp.GetBoolean();
                    var accessToken = root.TryGetProperty("accessToken", out var tokProp) ? tokProp.GetString() : null;
                    int expiresIn = 3600;

                    if (root.TryGetProperty("accessTokenExpirationTimestampMs", out var expProp))
                    {
                        var expMs = expProp.GetInt64();
                        var totalSec = (expMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000;
                        if (totalSec > 60) expiresIn = (int)totalSec;
                    }

                    if (!isAnonymous && !string.IsNullOrWhiteSpace(accessToken))
                    {
                        _isSuccess = true;
                        _pollTimer.Stop();

                        StatusBadgeText.Text = "✅ ZALOGOWANO POMYŚLNIE!";
                        StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");

                        // Extract sp_dc cookie if available
                        string? spDc = null;
                        try
                        {
                            var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://open.spotify.com");
                            var spCookie = cookies.FirstOrDefault(c => c.Name == "sp_dc");
                            if (spCookie != null) spDc = spCookie.Value;
                        }
                        catch { }

                        var profile = await _spotifyUserService.GetUserProfileAsync(accessToken);
                        SpotifyAuthService.SaveSession(accessToken, expiresIn, profile, spDc);

                        MessageBox.Show(
                            $"Zalogowano pomyślnie do Spotify jako: {profile?.DisplayName ?? "Użytkownik"}!\nTwoje playlisty i polubione utwory są teraz dostępne.",
                            "Logowanie Spotify udane",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        DialogResult = true;
                        Close();
                        return;
                    }
                }
            }
        }
        catch { }
        finally
        {
            _isChecking = false;
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

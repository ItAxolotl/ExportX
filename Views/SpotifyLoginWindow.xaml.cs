using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ExportX.Services;
using Microsoft.Web.WebView2.Core;

namespace ExportX.Views;

public partial class SpotifyLoginWindow : Window
{
    private readonly DispatcherTimer _pollTimer = new();
    private readonly SpotifyUserService _spotifyUserService = new();
    private bool _isSuccess = false;
    private bool _isChecking = false;

    public SpotifyLoginWindow()
    {
        InitializeComponent();

        _pollTimer.Interval = TimeSpan.FromSeconds(2.0);
        _pollTimer.Tick += PollTimer_Tick;

        Loaded += SpotifyLoginWindow_Loaded;
        Closed += SpotifyLoginWindow_Closed;
    }

    private async void SpotifyLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
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
            OverlayTitle.Text = "⚠️ Błąd uruchamiania okna przeglądarki";
            OverlaySubtitle.Text = "Brak wymaganego składnika Microsoft Edge WebView2 Runtime na tym komputerze.";
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
            var currentUrl = LoginWebView.Source?.ToString() ?? "";
            
            // Script to query Spotify Web internal token endpoint
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
                // Unescape JSON string returned by ExecuteScriptAsync
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
                        StatusBadge.Background = (System.Windows.Media.SolidColorBrush)FindResource("BrushNeonLime");

                        // Fetch profile details
                        var profile = await _spotifyUserService.GetUserProfileAsync(accessToken);
                        SpotifyAuthService.SaveSession(accessToken, expiresIn, profile);

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

    private void RefreshPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoginWebView.CoreWebView2?.Reload();
        }
        catch { }
    }

    private async void SaveManualToken_Click(object sender, RoutedEventArgs e)
    {
        var token = ManualTokenTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show("Podaj poprawny token dostępu Spotify.", "Pusty token", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var profile = await _spotifyUserService.GetUserProfileAsync(token);
        if (profile == null)
        {
            MessageBox.Show("Nie udało się zweryfikować tokenu. Upewnij się, że token jest ważny i nie wygasł.", "Błąd tokenu", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SpotifyAuthService.SaveSession(token, 3600, profile);
        MessageBox.Show($"Token zweryfikowany pomyślnie! Zalogowano jako: {profile.DisplayName}", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ExportX.Services;
using Microsoft.Web.WebView2.Core;

namespace ExportX.Views;

public partial class YouTubeLoginWindow : Window
{
    private bool _isInitialized;
    private bool _isSaving;
    private DispatcherTimer? _cookieWatcherTimer;
    public bool SessionSaved { get; private set; }

    public YouTubeLoginWindow()
    {
        InitializeComponent();
        SafeSetWindowIcon();
        Loaded += YouTubeLoginWindow_Loaded;
        Closing += YouTubeLoginWindow_Closing;
    }

    private void YouTubeLoginWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cookieWatcherTimer?.Stop();
    }

    private void SafeSetWindowIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon != null)
                {
                    using var bitmap = sysIcon.ToBitmap();
                    var hBitmap = bitmap.GetHbitmap();
                    try
                    {
                        this.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap,
                            IntPtr.Zero,
                            Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
            }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    private async void YouTubeLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateDeleteButtonState();
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var profileFolder = ConfigService.GetWebViewProfilePath();
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profileFolder);
            await LoginWebView.EnsureCoreWebView2Async(env);

            LoginWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(246, 244, 237);
            LoginWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            LoginWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            LoginWebView.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;

            // Open the official Google Account Chooser URL
            var accountChooserUrl = "https://accounts.google.com/AccountChooser?service=youtube&continue=https%3A%2F%2Fwww.youtube.com";
            LoginWebView.Source = new Uri(accountChooserUrl);

            _isInitialized = true;
            LoadingOverlay.Visibility = Visibility.Collapsed;

            // Start auto-detection watcher
            StartCookieWatcher();
        }
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            OverlayTitle.Text = "⚠️ Moduł przeglądarki WebView2";
            OverlaySubtitle.Text = $"Nie udało się uruchomić wbudowanego widoku ({ex.Message}).";
            FallbackPanel.Visibility = Visibility.Visible;
            LogService.Warn($"Błąd inicjalizacji WebView2: {ex.Message}", "AUTH");
        }
    }

    private void StartCookieWatcher()
    {
        _cookieWatcherTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _cookieWatcherTimer.Tick += async (_, _) =>
        {
            await CheckAndAutoSaveSessionAsync();
        };
        _cookieWatcherTimer.Start();
    }

    private async void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        await CheckAndAutoSaveSessionAsync();
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        await CheckAndAutoSaveSessionAsync();
    }

    private async Task CheckAndAutoSaveSessionAsync()
    {
        if (!_isInitialized || LoginWebView.CoreWebView2 == null || _isSaving) return;

        try
        {
            var cookieManager = LoginWebView.CoreWebView2.CookieManager;
            var cookies = await cookieManager.GetCookiesAsync("https://www.youtube.com");

            bool hasAuthCookie = cookies.Any(c => 
                c.Name == "LOGIN_INFO" || 
                c.Name == "SID" || 
                c.Name == "SSID" || 
                c.Name == "SAPISID" || 
                c.Name == "__Secure-1PSID" ||
                c.Name == "__Secure-3PSID");

            if (hasAuthCookie)
            {
                _isSaving = true;
                _cookieWatcherTimer?.Stop();

                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
                StatusBadgeText.Text = "🟢 ZALOGOWANO POMYŚLNIE!";
                SessionInfoText.Text = "✔ Zalogowano! Automatyczne zapisywanie sesji...";

                await SaveSessionInternalAsync(cookieManager);
            }
        }
        catch { }
    }

    private async Task SaveSessionInternalAsync(CoreWebView2CookieManager cookieManager)
    {
        try
        {
            var ytCookies = await cookieManager.GetCookiesAsync("https://www.youtube.com");
            var googleCookies = await cookieManager.GetCookiesAsync("https://accounts.google.com");
            var rootCookies = await cookieManager.GetCookiesAsync("https://google.com");

            var allCookies = ytCookies
                .Concat(googleCookies)
                .Concat(rootCookies)
                .GroupBy(c => $"{c.Domain}_{c.Path}_{c.Name}")
                .Select(g => g.First())
                .ToList();

            var cookiesPath = ConfigService.GetCookiesPath();
            var dir = Path.GetDirectoryName(cookiesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine("# http://curl.haxx.se/rfc/cookie_spec.html");
            sb.AppendLine("# This file was auto-generated by ExportX after Account Chooser selection.");
            sb.AppendLine();

            foreach (var c in allCookies)
            {
                var domain = c.Domain;
                var flag = domain.StartsWith(".") ? "TRUE" : "FALSE";
                var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
                var secure = c.IsSecure ? "TRUE" : "FALSE";
                var expires = c.Expires > DateTime.MinValue && c.Expires < DateTime.MaxValue
                    ? new DateTimeOffset(c.Expires).ToUnixTimeSeconds().ToString()
                    : DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds().ToString();

                sb.AppendLine($"{domain}\t{flag}\t{path}\t{secure}\t{expires}\t{c.Name}\t{c.Value}");
            }

            File.WriteAllText(cookiesPath, sb.ToString(), new UTF8Encoding(false));
            LogService.Success($"Zalogowano automatycznie do konta YouTube! Zapisano {allCookies.Count} ciasteczek.", "AUTH");

            SessionSaved = true;
            await Task.Delay(900);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _isSaving = false;
            LogService.Error($"Błąd podczas automatycznego zapisu sesji: {ex.Message}", "AUTH");
        }
    }

    private async void ManualSave_Click(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || LoginWebView.CoreWebView2 == null)
        {
            MessageBox.Show("Przeglądarka nie jest jeszcze zainicjalizowana.", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isSaving = true;
        await SaveSessionInternalAsync(LoginWebView.CoreWebView2.CookieManager);
    }

    private void OpenInExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = "https://accounts.google.com/AccountChooser?service=youtube&continue=https%3A%2F%2Fwww.youtube.com";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogService.Info("Otwarto stronę wyboru konta w domyślnej przeglądarce.", "AUTH");
        }
        catch { }
    }

    private async void ExtractFromExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ytDlpPath = ToolLocatorService.FindYtDlp();
            var targetCookiesPath = ConfigService.GetCookiesPath();
            var dir = Path.GetDirectoryName(targetCookiesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var defaultBrowser = AuthService.DetectDefaultBrowser();
            var browsers = new[] { defaultBrowser, "firefox", "chrome", "edge", "brave", "opera", "vivaldi" }.Distinct().ToList();

            foreach (var browser in browsers)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("--cookies-from-browser");
                psi.ArgumentList.Add(browser);
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(targetCookiesPath);
                psi.ArgumentList.Add("--skip-download");
                psi.ArgumentList.Add("https://www.youtube.com");

                try
                {
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (ConfigService.HasValidCookies())
                        {
                            SessionSaved = true;
                            LogService.Success($"Pomyślnie pobrano sesję z przeglądarki ({browser}).", "AUTH");
                            MessageBox.Show($"Zalogowano pomyślnie z przeglądarki ({browser})!", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
                            DialogResult = true;
                            Close();
                            return;
                        }
                    }
                }
                catch { }
            }

            MessageBox.Show("Nie udało się pobrać sesji z zewnętrznej przeglądarki.\n\nZaloguj się bezpośrednio w oknie powyżej (wystarczy kliknąć swoje konto), a sesja zapisze się automatycznie.", "Informacja", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Błąd: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show("Czy na pewno chcesz usunąć zapisaną sesję YouTube z aplikacji?", "Usuń sesję", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
        {
            try
            {
                ConfigService.DeleteCookies();
                if (_isInitialized && LoginWebView.CoreWebView2 != null)
                {
                    LoginWebView.CoreWebView2.CookieManager.DeleteAllCookies();
                }

                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonPink");
                StatusBadgeText.Text = "⚪ SESJA USUNIĘTA";
                SessionInfoText.Text = "Sesja została usunięta.";
                UpdateDeleteButtonState();
                LogService.Info("Usunięto zapisaną sesję YouTube.", "AUTH");
            }
            catch { }
        }
    }

    private void UpdateDeleteButtonState()
    {
        DeleteSessionBtn.IsEnabled = ConfigService.HasValidCookies();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

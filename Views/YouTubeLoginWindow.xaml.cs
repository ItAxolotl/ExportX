using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ExportX.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

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

            // Open the official Google ServiceLogin URL for YouTube
            var loginUrl = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
            LoginWebView.Source = new Uri(loginUrl);

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
            Interval = TimeSpan.FromSeconds(2.0)
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
            var ytCookies = await cookieManager.GetCookiesAsync("https://www.youtube.com");
            var googleCookies = await cookieManager.GetCookiesAsync("https://accounts.google.com");

            var currentUrl = LoginWebView.Source?.ToString() ?? "";

            // Check for genuine YouTube auth tokens with real cryptographic values (not dummy cookies)
            bool hasRealLoginInfo = ytCookies.Any(c => c.Name == "LOGIN_INFO" && !string.IsNullOrWhiteSpace(c.Value) && c.Value.Length > 15);
            bool hasRealGoogleSession = ytCookies.Concat(googleCookies).Any(c => 
                (c.Name == "__Secure-1PSID" || c.Name == "__Secure-3PSID" || c.Name == "SID") && 
                !string.IsNullOrWhiteSpace(c.Value) && c.Value.Length > 20);

            // Genuine login is verified when LOGIN_INFO exists or user has redirected back to youtube.com with Google credentials
            bool isLoggedIn = hasRealLoginInfo || (hasRealGoogleSession && currentUrl.Contains("youtube.com"));

            if (isLoggedIn)
            {
                _isSaving = true;
                _cookieWatcherTimer?.Stop();

                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
                StatusBadgeText.Text = "🟢 ZALOGOWANO POMYŚLNIE!";
                SessionInfoText.Text = "✔ Zalogowano! Zapisywanie sesji i zamykanie okna...";

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
            sb.AppendLine("# This file was auto-generated by ExportX after YouTube authentication.");
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

            var fullText = sb.ToString();
            if (ConfigService.HasValidAuthContent(fullText))
            {
                File.WriteAllText(cookiesPath, fullText, new UTF8Encoding(false));
                LogService.Success($"Zalogowano pomyślnie do konta YouTube! Zapisano {allCookies.Count} ciasteczek.", "AUTH");

                SessionSaved = true;
                await Task.Delay(800);

                DialogResult = true;
                Close();
            }
            else
            {
                _isSaving = false;
                StartCookieWatcher();
            }
        }
        catch (Exception ex)
        {
            _isSaving = false;
            LogService.Error($"Błąd podczas zapisu sesji: {ex.Message}", "AUTH");
        }
    }

    private async void ManualSave_Click(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || LoginWebView.CoreWebView2 == null)
        {
            MessageBox.Show("Przeglądarka nie jest jeszcze zainicjalizowana.", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cookieManager = LoginWebView.CoreWebView2.CookieManager;
        var ytCookies = await cookieManager.GetCookiesAsync("https://www.youtube.com");
        var googleCookies = await cookieManager.GetCookiesAsync("https://accounts.google.com");

        bool hasAuth = ytCookies.Any(c => c.Name == "LOGIN_INFO" && !string.IsNullOrWhiteSpace(c.Value) && c.Value.Length > 15) ||
                       ytCookies.Concat(googleCookies).Any(c => (c.Name == "__Secure-1PSID" || c.Name == "SID") && !string.IsNullOrWhiteSpace(c.Value) && c.Value.Length > 20);

        if (!hasAuth)
        {
            MessageBox.Show(
                "Nie wykryto jeszcze aktywnej sesji logowania w oknie.\n\nZaloguj się najpierw na swoje konto Google powyżej i upewnij się, że strona YouTube się załadowała.",
                "Brak sesji",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _isSaving = true;
        await SaveSessionInternalAsync(cookieManager);
    }

    private void ImportCookiesFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ofd = new OpenFileDialog
            {
                Title = "Wybierz wyeksportowany plik cookies.txt (np. z przeglądarki)",
                Filter = "Pliki tekstowe cookies (*.txt)|*.txt|Wszystkie pliki (*.*)|*.*",
                Multiselect = false
            };

            if (ofd.ShowDialog(this) == true)
            {
                var content = File.ReadAllText(ofd.FileName);
                if (ConfigService.HasValidAuthContent(content))
                {
                    var targetCookiesPath = ConfigService.GetCookiesPath();
                    var dir = Path.GetDirectoryName(targetCookiesPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    File.WriteAllText(targetCookiesPath, content, new UTF8Encoding(false));
                    SessionSaved = true;
                    LogService.Success("Pomyślnie zaimportowano i zweryfikowano plik sesji cookies.txt!", "AUTH");
                    MessageBox.Show("Plik ciasteczek został pomyślnie zaimportowany! Konto YouTube jest aktywne.", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(
                        "Wybrany plik cookies.txt nie zawiera poprawnych danych logowania do YouTube/Google.\n\nUpewnij się, że wyeksportowano ciasteczka z zalogowanego profilu YouTube (wymagane LOGIN_INFO lub __Secure-1PSID).",
                        "Niepoprawny plik",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Błąd podczas importowania pliku: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenInExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogService.Info("Otwarto oficjalną stronę logowania w domyślnej przeglądarce.", "AUTH");
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
            var browsers = new[] { defaultBrowser, "brave", "chrome", "edge", "firefox", "opera", "vivaldi" }.Distinct().ToList();

            while (true)
            {
                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonCyan");
                StatusBadgeText.Text = "⏳ SPRAWDZANIE PRZEGLĄDAREK...";
                SessionInfoText.Text = "Wyszukiwanie zalogowanej sesji YouTube w Twoich przeglądarkach...";

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
                                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
                                StatusBadgeText.Text = "🟢 ZALOGOWANO POMYŚLNIE!";
                                SessionInfoText.Text = $"✔ Sukces! Pomyślnie pobrano konto z przeglądarki {browser.ToUpperInvariant()}!";
                                SessionSaved = true;
                                LogService.Success($"Pomyślnie pobrano sesję z przeglądarki ({browser}).", "AUTH");
                                MessageBox.Show($"🎉 Sukces! Pomyślnie pobrano Twoją sesję z przeglądarki {browser.ToUpperInvariant()} bez wpisywania hasła!", "Zalogowano", MessageBoxButton.OK, MessageBoxImage.Information);
                                DialogResult = true;
                                Close();
                                return;
                            }
                        }
                    }
                    catch { }
                }

                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonYellow");
                StatusBadgeText.Text = "⚠️ WYMAGANE ZAMKNIĘCIE PRZEGLĄDARKI";

                var prompt = MessageBox.Show(
                    "Twoja przeglądarka (Brave / Chrome / Edge) blokuje dostęp do bazy danych, dopóki jest włączona.\n\n" +
                    "Aby pobrać sesję w 1 sekundę BEZ wpisywania hasła i kodów weryfikacyjnych:\n" +
                    "1. Zamknij teraz swoją przeglądarkę (np. Brave / Chrome).\n" +
                    "2. Kliknij 'Ponów próbę'.\n\n" +
                    "Czy chcesz spróbować ponownie po zamknięciu przeglądarki?",
                    "Pobieranie sesji z przeglądarki",
                    MessageBoxButton.RetryCancel,
                    MessageBoxImage.Information);

                if (prompt != MessageBoxResult.Retry)
                {
                    StatusBadgeText.Text = "GOTOWY DO LOGOWANIA";
                    SessionInfoText.Text = "Wskazówka: Zaloguj się w oknie powyżej lub użyj 'IMPORTUJ COOKIES.TXT'.";
                    break;
                }
            }
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

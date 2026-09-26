using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExportX.Services;
using Microsoft.Web.WebView2.Core;

namespace ExportX.Views;

public partial class YouTubeLoginWindow : Window
{
    private bool _isInitialized;

    public bool SessionSaved { get; private set; }

    public YouTubeLoginWindow()
    {
        InitializeComponent();
        SafeSetWindowIcon();
        Loaded += YouTubeLoginWindow_Loaded;
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
                            BitmapSizeOptions.FromEmptyOptions());
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
            }
        }
        catch
        {
            // Ignore icon loading errors safely
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    private async void YouTubeLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
        UpdateDeleteButtonState();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var profileFolder = ConfigService.GetWebViewProfilePath();
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profileFolder);
            await LoginWebView.EnsureCoreWebView2Async(env);

            LoginWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            LoginWebView.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;

            // Navigate to YouTube login
            LoginWebView.Source = new Uri("https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com");
            _isInitialized = true;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show($"Nie udało się uruchomić modułu przeglądarki WebView2:\n\n{ex.Message}", "Błąd WebView2", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        await CheckLoginStateAsync();
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        await CheckLoginStateAsync();
    }

    private async Task CheckLoginStateAsync()
    {
        if (!_isInitialized || LoginWebView.CoreWebView2 == null) return;

        try
        {
            var cookieManager = LoginWebView.CoreWebView2.CookieManager;
            var cookies = await cookieManager.GetCookiesAsync("https://www.youtube.com");

            bool hasAuthCookie = cookies.Any(c => c.Name == "LOGIN_INFO" || c.Name == "SID" || c.Name == "SSID" || c.Name == "SAPISID" || c.Name == "__Secure-1PSID");

            if (hasAuthCookie)
            {
                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonLime");
                StatusBadgeText.Text = "🟢 WYKRYTO AKTYWNĄ SESJĘ YOUTUBE";
                SessionInfoText.Text = "✔ Wykryto zalogowane konto! Kliknij zielony przycisk 'ZAPISZ SESJĘ I ZAMKNIJ', aby zapisać.";
            }
            else
            {
                StatusBadge.Background = (SolidColorBrush)FindResource("BrushNeonYellow");
                StatusBadgeText.Text = "🟡 OCZEKIWANIE NA ZALOGOWANIE";
                SessionInfoText.Text = "Zaloguj się na swoje konto Google / YouTube w powyższym oknie.";
            }
        }
        catch
        {
            // Ignore check errors
        }
    }

    private async void SaveSession_Click(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || LoginWebView.CoreWebView2 == null)
        {
            MessageBox.Show("Przeglądarka nie jest jeszcze gotowa.", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var cookieManager = LoginWebView.CoreWebView2.CookieManager;
            var ytCookies = await cookieManager.GetCookiesAsync("https://www.youtube.com");
            var googleCookies = await cookieManager.GetCookiesAsync("https://accounts.google.com");
            var googleRootCookies = await cookieManager.GetCookiesAsync("https://google.com");

            var allCookies = ytCookies
                .Concat(googleCookies)
                .Concat(googleRootCookies)
                .GroupBy(c => $"{c.Domain}_{c.Path}_{c.Name}")
                .Select(g => g.First())
                .ToList();

            if (allCookies.Count == 0)
            {
                var res = MessageBox.Show("Nie wykryto żadnych ciasteczek sesji. Czy na pewno chcesz zapisać pustą sesję?", "Brak ciasteczek", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
            }

            var cookiesPath = ConfigService.GetCookiesPath();
            var dir = Path.GetDirectoryName(cookiesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine("# http://curl.haxx.se/rfc/cookie_spec.html");
            sb.AppendLine("# This file was generated by ExportX for YouTube Age-Restriction & Session Authentication.");
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
            LogService.Success($"Zapisano sesję YouTube ({allCookies.Count} ciasteczek) do pliku: {cookiesPath}", "AUTH");

            SessionSaved = true;
            MessageBox.Show("Sesja YouTube została pomyślnie zapisana!\n\nOd teraz wszystkie utwory z ograniczeniem wiekowym (+18) oraz prywatne playlisty będą pobierane automatycznie z Twojego konta.", "Zalogowano pomyślnie", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Błąd podczas zapisywania sesji:\n\n{ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
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
                SessionInfoText.Text = "Sesja została usunięta. Zaloguj się ponownie, jeśli chcesz korzystać z konta.";
                UpdateDeleteButtonState();
                LogService.Info("Usunięto zapisaną sesję YouTube.", "AUTH");

                MessageBox.Show("Sesja YouTube została usunięta.", "Wylogowano", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Błąd podczas usuwania sesji:\n\n{ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

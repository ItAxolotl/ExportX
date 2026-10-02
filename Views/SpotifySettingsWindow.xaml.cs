using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ExportX.Services;

namespace ExportX.Views;

public partial class SpotifySettingsWindow : Window
{
    private readonly ConfigService _configService = new();

    public SpotifySettingsWindow()
    {
        InitializeComponent();
        LoadExistingConfig();
    }

    private void LoadExistingConfig()
    {
        var config = _configService.Config;
        ClientIdTextBox.Text = config.SpotifyClientId;
        ClientSecretTextBox.Text = config.SpotifyClientSecret;

        if (!string.IsNullOrWhiteSpace(config.SpotifyClientId) && !string.IsNullOrWhiteSpace(config.SpotifyClientSecret))
        {
            StatusResultBorder.Background = (SolidColorBrush)FindResource("BrushNeonLime");
            StatusResultText.Text = "✅ Klucze Spotify API są skonfigurowane w programie.";
        }
    }

    private void OpenSpotifyDashboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://developer.spotify.com/dashboard",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nie udało się otworzyć przeglądarki: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var id = ClientIdTextBox.Text.Trim();
        var secret = ClientSecretTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
        {
            StatusResultBorder.Background = (SolidColorBrush)FindResource("BrushNeonPink");
            StatusResultText.Text = "❌ Wprowadź zarówno Client ID, jak i Client Secret przed testem.";
            return;
        }

        StatusResultBorder.Background = (SolidColorBrush)FindResource("BrushNeonYellow");
        StatusResultText.Text = "⏳ Testowanie połączenia z serwerami Spotify Accounts...";

        var (success, msg) = await PlaylistParserService.TestSpotifyCredentialsAsync(id, secret);

        if (success)
        {
            StatusResultBorder.Background = (SolidColorBrush)FindResource("BrushNeonLime");
            StatusResultText.Text = $"✅ {msg}";
        }
        else
        {
            StatusResultBorder.Background = (SolidColorBrush)FindResource("BrushNeonPink");
            StatusResultText.Text = $"❌ {msg}";
        }
    }

    private void ClearKeys_Click(object sender, RoutedEventArgs e)
    {
        ClientIdTextBox.Clear();
        ClientSecretTextBox.Clear();
        var config = _configService.Config;
        config.SpotifyClientId = string.Empty;
        config.SpotifyClientSecret = string.Empty;
        _configService.Save();

        StatusResultBorder.Background = new SolidColorBrush(Color.FromRgb(243, 244, 246));
        StatusResultText.Text = "Wyczyszczono klucze Spotify.";
        LogService.Info("Usunięto klucze Spotify API.", "SPOTIFY");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        var id = ClientIdTextBox.Text.Trim();
        var secret = ClientSecretTextBox.Text.Trim();

        var config = _configService.Config;
        config.SpotifyClientId = id;
        config.SpotifyClientSecret = secret;
        _configService.Save();

        LogService.Success("Zapisano ustawienia Spotify API.", "SPOTIFY");
        DialogResult = true;
        Close();
    }
}

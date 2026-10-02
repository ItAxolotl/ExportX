using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ExportX.Models;
using ExportX.Services;

namespace ExportX.Views;

public partial class SpotifyBrowserWindow : Window
{
    private readonly SpotifyUserService _spotifyUserService = new();
    private readonly ObservableCollection<SpotifyPlaylistSummary> _playlists = new();
    private readonly ObservableCollection<SpotifyTrackItem> _tracks = new();

    public List<ParsedTrackInfo> SelectedTracksToImport { get; private set; } = new();
    public bool ShouldStartImmediately { get; private set; } = false;

    private SpotifyPlaylistSummary? _currentSelectedPlaylist;
    private bool _isLikedSongsMode = false;

    public SpotifyBrowserWindow()
    {
        InitializeComponent();

        PlaylistsListBox.ItemsSource = _playlists;
        TracksDataGrid.ItemsSource = _tracks;

        Loaded += SpotifyBrowserWindow_Loaded;
    }

    private async void SpotifyBrowserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (SpotifyAuthService.CurrentSession.UserProfile?.DisplayName == "Micael Widell")
        {
            SpotifyAuthService.ClearSession();
        }

        UpdateAccountUi();
        var token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (!string.IsNullOrEmpty(token))
        {
            var profile = await _spotifyUserService.GetUserProfileAsync(token);
            if (profile != null && profile.DisplayName != "Micael Widell")
            {
                SpotifyAuthService.SaveSession(token, 3600, profile, SpotifyAuthService.CurrentSession.SpDcCookie);
                UpdateAccountUi();
            }
        }
        await LoadUserPlaylistsAsync();
    }

    private void UpdateAccountUi()
    {
        var session = SpotifyAuthService.CurrentSession;
        if (SpotifyAuthService.IsLoggedIn && session.UserProfile != null)
        {
            var user = session.UserProfile;
            AccountStatusText.Text = $"🟢 Zalogowano jako: {user.DisplayName} ({user.Product.ToUpperInvariant()}) | Obserwujących: {user.FollowersCount}";
            LoginButton.Content = "🔄 ZMIEŃ KONTO";
            LogoutButton.Visibility = Visibility.Visible;
        }
        else
        {
            AccountStatusText.Text = "Zaloguj się do Spotify, aby przeglądać swoje prywatne playlisty i polubione utwory.";
            LoginButton.Content = "🔑 ZALOGUJ DO SPOTIFY";
            LogoutButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<string?> EnsureAccessTokenAsync()
    {
        var token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            // Prompt user to log in
            var res = MessageBox.Show(
                "Aby przeglądać playlisty i utwory ze Spotify, musisz się zalogować lub skonfigurować klucze API.\n\nCzy chcesz otworzyć okno logowania Spotify?",
                "Wymagane logowanie Spotify",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                var loginWin = new SpotifyLoginWindow { Owner = this };
                if (loginWin.ShowDialog() == true)
                {
                    UpdateAccountUi();
                    return await SpotifyAuthService.GetValidAccessTokenAsync();
                }
            }
            return null;
        }
        return token;
    }

    private async Task LoadUserPlaylistsAsync()
    {
        _isLikedSongsMode = false;
        var token = await SpotifyAuthService.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            _playlists.Clear();
            GlobalStatusText.Text = "Zaloguj się do Spotify, aby wczytać swoje playlisty.";
            return;
        }

        PlaylistsLoadingOverlay.Visibility = Visibility.Visible;
        PlaylistsLoadingText.Text = "Wczytywanie playlist użytkownika...";
        GlobalStatusText.Text = "Pobieranie playlist ze Spotify...";

        var progress = new Progress<string>(msg =>
        {
            Dispatcher.InvokeAsync(() => PlaylistsLoadingText.Text = msg);
        });

        try
        {
            var list = await _spotifyUserService.GetUserPlaylistsAsync(token, progress);
            _playlists.Clear();
            foreach (var p in list)
            {
                _playlists.Add(p);
            }

            GlobalStatusText.Text = $"Wczytano {_playlists.Count} playlist użytkownika.";
            if (_playlists.Count > 0 && PlaylistsListBox.SelectedItem == null)
            {
                PlaylistsListBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Błąd pobierania playlist: {ex.Message}";
        }
        finally
        {
            PlaylistsLoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadLikedSongsAsync()
    {
        var token = await EnsureAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        _isLikedSongsMode = true;
        _currentSelectedPlaylist = null;
        PlaylistsListBox.SelectedItem = null;

        SelectedPlaylistTitleText.Text = "❤️ POLUBIONE UTWORY (LIKED SONGS)";
        SelectedPlaylistSubtitleText.Text = "Twoja prywatna kolekcja polubionych utworów na Spotify";
        SelectedCoverImage.Source = null;

        TracksLoadingOverlay.Visibility = Visibility.Visible;
        TracksLoadingText.Text = "Pobieranie polubionych utworów ze Spotify...";
        GlobalStatusText.Text = "Wczytywanie Twojej biblioteki polubionych utworów...";

        var progress = new Progress<string>(msg =>
        {
            Dispatcher.InvokeAsync(() => TracksLoadingText.Text = msg);
        });

        try
        {
            var tracks = await _spotifyUserService.GetLikedSongsAsync(token, progress);
            _tracks.Clear();
            foreach (var t in tracks)
            {
                _tracks.Add(t);
            }

            UpdateSelectionSummary();
            GlobalStatusText.Text = $"Wczytano {tracks.Count} polubionych utworów. Zaznacz pozycje do pobrania.";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Błąd wczytywania polubionych utworów: {ex.Message}";
        }
        finally
        {
            TracksLoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void PlaylistsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistsListBox.SelectedItem is SpotifyPlaylistSummary playlist)
        {
            _isLikedSongsMode = false;
            _currentSelectedPlaylist = playlist;

            SelectedPlaylistTitleText.Text = playlist.Name;
            SelectedPlaylistSubtitleText.Text = $"Autor: {playlist.OwnerName} | Łącznie: {playlist.TotalTracks} utworów | {(playlist.IsPublic ? "Publiczna" : "Prywatna")}";

            if (!string.IsNullOrEmpty(playlist.ImageUrl))
            {
                try
                {
                    SelectedCoverImage.Source = new BitmapImage(new Uri(playlist.ImageUrl));
                }
                catch { SelectedCoverImage.Source = null; }
            }
            else
            {
                SelectedCoverImage.Source = null;
            }

            await LoadTracksForPlaylistAsync(playlist.Id);
        }
    }

    private async Task LoadTracksForPlaylistAsync(string playlistId)
    {
        var token = await EnsureAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        TracksLoadingOverlay.Visibility = Visibility.Visible;
        TracksLoadingText.Text = "Wczytywanie listy utworów...";
        GlobalStatusText.Text = "Pobieranie utworów z wybranej playlisty...";

        var progress = new Progress<string>(msg =>
        {
            Dispatcher.InvokeAsync(() => TracksLoadingText.Text = msg);
        });

        try
        {
            var tracks = await _spotifyUserService.GetPlaylistTracksAsync(playlistId, token, progress);
            _tracks.Clear();
            foreach (var t in tracks)
            {
                _tracks.Add(t);
            }

            UpdateSelectionSummary();
            GlobalStatusText.Text = $"Gotowe! Załadowano {tracks.Count} utworów z playlisty '{_currentSelectedPlaylist?.Name}'.";
        }
        catch (Exception ex)
        {
            GlobalStatusText.Text = $"Błąd wczytywania utworów: {ex.Message}";
        }
        finally
        {
            TracksLoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void TabMyPlaylists_Click(object sender, RoutedEventArgs e)
    {
        TabMyPlaylistsBtn.Style = (Style)FindResource("GreenStartButton");
        TabLikedSongsBtn.Style = (Style)FindResource("BrutalistButton");
        TabLikedSongsBtn.Background = (System.Windows.Media.SolidColorBrush)FindResource("BrushPaperWhite");
        await LoadUserPlaylistsAsync();
    }

    private async void TabLikedSongs_Click(object sender, RoutedEventArgs e)
    {
        TabLikedSongsBtn.Style = (Style)FindResource("GreenStartButton");
        TabMyPlaylistsBtn.Style = (Style)FindResource("BrutalistButton");
        TabMyPlaylistsBtn.Background = (System.Windows.Media.SolidColorBrush)FindResource("BrushPaperWhite");
        await LoadLikedSongsAsync();
    }

    private async void PlaylistSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var query = PlaylistSearchTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            SearchPlaceholder.Visibility = Visibility.Collapsed;

            // If it's a direct Spotify URL, extract playlist ID and load directly
            if (PlaylistParserService.IsSpotifyUrl(query))
            {
                var match = System.Text.RegularExpressions.Regex.Match(query, @"playlist[/:]+([a-zA-Z0-9]+)");
                if (match.Success)
                {
                    var pId = match.Groups[1].Value;
                    await LoadTracksForPlaylistAsync(pId);
                    return;
                }
            }

            // Search by query
            var token = await EnsureAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return;

            PlaylistsLoadingOverlay.Visibility = Visibility.Visible;
            PlaylistsLoadingText.Text = $"Szukanie '{query}' w Spotify...";
            GlobalStatusText.Text = $"Wyszukiwanie playlist dla: {query}...";

            var results = await _spotifyUserService.SearchPlaylistsAsync(query, token);
            _playlists.Clear();
            foreach (var r in results)
            {
                _playlists.Add(r);
            }

            PlaylistsLoadingOverlay.Visibility = Visibility.Collapsed;
            GlobalStatusText.Text = $"Znaleziono {results.Count} pasujących playlist.";
            if (_playlists.Count > 0)
            {
                PlaylistsListBox.SelectedIndex = 0;
            }
        }
        else
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(PlaylistSearchTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _tracks) t.IsSelected = true;
        UpdateSelectionSummary();
    }

    private void UnselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _tracks) t.IsSelected = false;
        UpdateSelectionSummary();
    }

    private void TrackCheckbox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        int total = _tracks.Count;
        int selected = _tracks.Count(t => t.IsSelected);
        TracksSelectionSummaryText.Text = $"Utworów: {total} | Zaznaczono: {selected}";
        AddToQueueBtn.Content = selected == total
            ? $"➕ DODAJ CAŁĄ PLAYLISTĘ ({total})"
            : $"➕ DODAJ ZAZNACZONE ({selected})";
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var loginWin = new SpotifyLoginWindow { Owner = this };
        if (loginWin.ShowDialog() == true)
        {
            UpdateAccountUi();
            _ = LoadUserPlaylistsAsync();
        }
    }

    private void ExtractFromBrowser_Click(object sender, RoutedEventArgs e)
    {
        var loginWin = new SpotifyLoginWindow(autoExtract: true) { Owner = this };
        if (loginWin.ShowDialog() == true)
        {
            UpdateAccountUi();
            _ = LoadUserPlaylistsAsync();
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            "Czy na pewno chcesz wylogować konto Spotify z aplikacji?",
            "Wylogowanie Spotify",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            SpotifyAuthService.ClearSession();
            UpdateAccountUi();
            _playlists.Clear();
            _tracks.Clear();
            GlobalStatusText.Text = "Wylogowano konto Spotify.";
        }
    }

    private void DeveloperApi_Click(object sender, RoutedEventArgs e)
    {
        var win = new SpotifySettingsWindow { Owner = this };
        win.ShowDialog();
        _ = LoadUserPlaylistsAsync();
    }

    private void OpenInSpotify_Click(object sender, RoutedEventArgs e)
    {
        var url = _currentSelectedPlaylist?.SpotifyUrl ?? "https://open.spotify.com";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private void RefreshPlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (_isLikedSongsMode)
        {
            _ = LoadLikedSongsAsync();
        }
        else
        {
            _ = LoadUserPlaylistsAsync();
        }
    }

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        ExecuteImport(false);
    }

    private void AddAndStart_Click(object sender, RoutedEventArgs e)
    {
        ExecuteImport(true);
    }

    private void ExecuteImport(bool startImmediately)
    {
        var selected = _tracks.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Zaznacz przynajmniej jeden utwór z listy do dodania.", "Brak zaznaczenia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedTracksToImport = selected.Select(t => new ParsedTrackInfo
        {
            Title = t.Title,
            Artist = t.Artist,
            QueryOrUrl = $"{t.Artist} - {t.Title}"
        }).ToList();

        ShouldStartImmediately = startImmediately;
        DialogResult = true;
        Close();
    }
}

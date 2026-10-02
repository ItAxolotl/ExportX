using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExportX.Models;
using ExportX.Services;
using ExportX.Views;
using Microsoft.Win32;

namespace ExportX;

public class ComboBoxDisplayItem<T>
{
    public T Value { get; set; }
    public string DisplayName { get; set; }

    public ComboBoxDisplayItem(T value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public override string ToString() => DisplayName;
}

public partial class MainWindow : Window
{
    private readonly ConfigService _configService = new();
    private readonly PlaylistParserService _parserService = new();
    private readonly FileImportService _importService = new();
    private readonly DownloaderEngine _downloaderEngine = new();
    private readonly ObservableCollection<DownloadItem> _items = new();
    private bool _isLogsVisible = false;
    private bool _isInitializing = true;

    public MainWindow()
    {
        InitializeComponent();

        QueueDataGrid.ItemsSource = _items;
        LogsListBox.ItemsSource = LogService.LogEntries;

        _downloaderEngine.ItemStatusChanged += OnItemStatusChanged;
        _downloaderEngine.QueueFinished += OnQueueFinished;
        LogService.LogAdded += OnLogAdded;

        InitializeUI();
    }

    private void InitializeUI()
    {
        _isInitializing = true;
        var config = _configService.Config;

        // Safely set window icon from executable icon
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon != null)
                {
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
        }
        catch { }

        // Path (set before anything else!)
        if (string.IsNullOrWhiteSpace(config.OutputDirectory) || !Directory.Exists(config.OutputDirectory))
        {
            config.OutputDirectory = ConfigService.GetDefaultOutputDirectory();
        }
        OutputPathTextBox.Text = config.OutputDirectory;

        // Formats with user-friendly labels
        var formatItems = Enum.GetValues<DownloadFormat>()
            .Select(f => new ComboBoxDisplayItem<DownloadFormat>(f, f.GetDisplayName()))
            .ToList();
        DefaultFormatComboBox.ItemsSource = formatItems;
        DefaultFormatComboBox.SelectedValue = config.DefaultFormat;

        // Bitrates with user-friendly labels (320 kbps first or natural order)
        var bitrateItems = Enum.GetValues<AudioBitrate>()
            .Select(b => new ComboBoxDisplayItem<AudioBitrate>(b, b.GetDisplayName()))
            .ToList();
        DefaultBitrateComboBox.ItemsSource = bitrateItems;
        DefaultBitrateComboBox.SelectedValue = config.DefaultBitrate;

        // Threads (1 to 16)
        ThreadsComboBox.ItemsSource = Enumerable.Range(1, 16).ToList();
        ThreadsComboBox.SelectedItem = Math.Clamp(config.MaxParallelDownloads, 1, 16);

        // Checkboxes
        Anti403CheckBox.IsChecked = config.EnableAnti403;
        AutoSkipCheckBox.IsChecked = config.AutoSkipExisting;
        EmbedThumbnailsCheckBox.IsChecked = config.EmbedThumbnail;
        EmbedMetadataCheckBox.IsChecked = config.EmbedMetadata;

        _isInitializing = false;
        SaveCurrentConfig();

        UpdateStats();
        UpdateYouTubeLoginButtonState();
        if (ConfigService.IsPortableMode)
        {
            this.Title += " [PORTABLE]";
            LogService.Info("Tryb przenośny (PORTABLE MODE) aktywny. Konfiguracja i dane zapisywane lokalnie.", "PORTABLE");
        }

        LogService.Info("Aplikacja ExportX uruchomiona pomyślnie.", "APP");
        LogService.Info($"Folder zapisu: {config.OutputDirectory}", "APP");

        // Background auto-provision of missing tools on zero-setup launch
        _ = Task.Run(async () =>
        {
            await ToolLocatorService.EnsureToolsAsync(msg =>
            {
                Dispatcher.InvokeAsync(() => GlobalStatusInfo.Text = msg);
            });
        });
    }

    private void UpdateYouTubeLoginButtonState()
    {
        if (ConfigService.HasValidCookies())
        {
            YouTubeLoginBtn.Content = "🟢 YOUTUBE (ZALOGOWANO)";
            YouTubeLoginBtn.Background = (SolidColorBrush)FindResource("BrushNeonLime");
            YouTubeLoginBtn.ToolTip = "Konto YouTube jest aktywne. Kliknij, aby zarządzać sesją lub wylogować.";
        }
        else
        {
            YouTubeLoginBtn.Content = "🔑 KONTO YOUTUBE";
            YouTubeLoginBtn.Background = (SolidColorBrush)FindResource("BrushNeonYellow");
            YouTubeLoginBtn.ToolTip = "Zaloguj się do YouTube/Google, aby odblokować pobieranie filmów +18 i playlist prywatnych";
        }
    }

    private async void YouTubeLogin_Click(object sender, RoutedEventArgs e)
    {
        if (ConfigService.HasValidCookies())
        {
            var res = MessageBox.Show(
                "Twoje konto YouTube jest aktualnie połączone i aktywne.\n\nCzy chcesz wylogować się z aplikacji lub zmienić konto?",
                "Zarządzanie kontem YouTube",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                ConfigService.DeleteCookies();
                UpdateYouTubeLoginButtonState();
                LogService.Info("Wylogowano konto YouTube.", "AUTH");
                GlobalStatusInfo.Text = "Wylogowano konto YouTube.";
            }
            return;
        }

        YouTubeLoginBtn.Content = "⏳ WYBIERZ KONTO W PRZEGLĄDARCE...";
        YouTubeLoginBtn.Background = (SolidColorBrush)FindResource("BrushNeonCyan");
        GlobalStatusInfo.Text = "Otwarto stronę wyboru konta w Twojej przeglądarce. Wybierz swoje konto...";

        var success = await AuthService.StartBrowserLoginFlowAsync(status =>
        {
            Dispatcher.InvokeAsync(() => GlobalStatusInfo.Text = status);
        });

        UpdateYouTubeLoginButtonState();

        if (success || ConfigService.HasValidCookies())
        {
            GlobalStatusInfo.Text = "✅ Zalogowano pomyślnie z kontem Google / YouTube!";
            MessageBox.Show(
                "Sukces!\n\nTwoje konto Google zostało pomyślnie połączone z aplikacją ExportX.\n\nFilmy z ograniczeniem wiekowym (+18) i playlisty prywatne są teraz odblokowane.",
                "Zalogowano pomyślnie",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            GlobalStatusInfo.Text = "Nie ukończono logowania w przeglądarce.";
        }
    }

    private void OnLogAdded(LogEntry entry)
    {
        LastLogSnippet.Text = entry.FormattedLine;

        if (_isLogsVisible && LogsListBox.Items.Count > 0)
        {
            LogsListBox.ScrollIntoView(LogsListBox.Items[^1]);
        }
    }

    private void ToggleLogs_Click(object sender, RoutedEventArgs e)
    {
        _isLogsVisible = !_isLogsVisible;
        LogsPanel.Visibility = _isLogsVisible ? Visibility.Visible : Visibility.Collapsed;
        ToggleLogsBtn.Content = _isLogsVisible ? "📓 DZIENNIK ZDARZEŃ (LOGI) ▲" : "📓 DZIENNIK ZDARZEŃ (LOGI) ▼";
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        var allLines = string.Join(Environment.NewLine, LogService.LogEntries.Select(x => x.FormattedLine));
        if (!string.IsNullOrEmpty(allLines))
        {
            Clipboard.SetText(allLines);
            MessageBox.Show("Zawartość dziennika zdarzeń została skopiowana do schowka.", "Skopiowano logi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(LogService.LogFilePath))
            {
                Process.Start("notepad.exe", LogService.LogFilePath);
            }
            else
            {
                var dir = Path.GetDirectoryName(LogService.LogFilePath);
                if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nie udało się otworzyć pliku logów: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        LogService.Clear();
        LastLogSnippet.Text = "Wyczyszczono dziennik zdarzeń.";
    }

    private async void UpdateEngine_Click(object sender, RoutedEventArgs e)
    {
        UpdateEngineBtn.IsEnabled = false;
        LogService.Info("Rozpoczynanie aktualizacji yt-dlp (yt-dlp -U)...", "UPDATE");
        GlobalStatusInfo.Text = "Aktualizowanie silnika yt-dlp...";

        var (ok, output) = await ToolLocatorService.UpdateYtDlpAsync();

        if (ok)
        {
            LogService.Success($"Aktualizacja zakończona sukcesem: {output}", "UPDATE");
            MessageBox.Show($"Silnik pobierający zaktualizowany pomyślnie!\n\n{output}", "Aktualizacja zakończona", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            LogService.Warn($"Wynik aktualizacji: {output}", "UPDATE");
            MessageBox.Show($"Informacja o aktualizacji:\n{output}", "Aktualizacja silnika", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        UpdateEngineBtn.IsEnabled = true;
        GlobalStatusInfo.Text = "Gotowy do działania.";
    }

    private void SaveCurrentConfig()
    {
        if (_isInitializing) return;

        var config = _configService.Config;
        var dir = OutputPathTextBox.Text;
        config.OutputDirectory = string.IsNullOrWhiteSpace(dir) ? ConfigService.GetDefaultOutputDirectory() : dir;

        if (DefaultFormatComboBox.SelectedValue is DownloadFormat fmt) config.DefaultFormat = fmt;
        if (DefaultBitrateComboBox.SelectedValue is AudioBitrate br) config.DefaultBitrate = br;
        if (ThreadsComboBox.SelectedItem is int threads) config.MaxParallelDownloads = threads;
        config.EnableAnti403 = Anti403CheckBox.IsChecked == true;
        config.AutoSkipExisting = AutoSkipCheckBox.IsChecked == true;
        config.EmbedThumbnail = EmbedThumbnailsCheckBox.IsChecked == true;
        config.EmbedMetadata = EmbedMetadataCheckBox.IsChecked == true;

        _configService.Save();
    }

    private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Wybierz folder docelowy dla pobieranych plików",
            InitialDirectory = OutputPathTextBox.Text
        };

        if (dialog.ShowDialog() == true)
        {
            OutputPathTextBox.Text = dialog.FolderName;
            SaveCurrentConfig();
            LogService.Info($"Zmieniono folder zapisu na: {dialog.FolderName}", "CONFIG");
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = OutputPathTextBox.Text;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            path = ConfigService.GetDefaultOutputDirectory();
            OutputPathTextBox.Text = path;
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        Process.Start("explorer.exe", path);
    }

    private async void AddSingleItem_Click(object sender, RoutedEventArgs e)
    {
        var text = SearchOrUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        SearchOrUrlTextBox.Clear();
        SearchPlaceholder.Visibility = Visibility.Visible;

        await AddQueryOrUrlAsync(text);
    }

    private void SearchOrUrlTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddSingleItem_Click(sender, e);
        }
        else
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchOrUrlTextBox.Text) 
                ? Visibility.Visible 
                : Visibility.Collapsed;
        }
    }

    private async Task AddQueryOrUrlAsync(string input)
    {
        GlobalStatusInfo.Text = "Analizowanie źródła...";
        LogService.Info($"Dodawanie pozycji: {input}", "ADD");
        var progress = new Progress<string>(msg =>
        {
            GlobalStatusInfo.Text = msg;
            LogService.Debug(msg, "PARSER");
        });

        var fmt = DefaultFormatComboBox.SelectedValue is DownloadFormat f ? f : DownloadFormat.MP3;
        var br = DefaultBitrateComboBox.SelectedValue is AudioBitrate b ? b : AudioBitrate.B320;

        try
        {
            var parsed = await _parserService.ParseInputAsync(input, progress);
            foreach (var item in parsed)
            {
                AddItemToCollection(item.QueryOrUrl, item.Title, item.Artist, fmt, br);
            }
            GlobalStatusInfo.Text = $"Dodano {parsed.Count} pozycji.";
            LogService.Success($"Dodano {parsed.Count} pozycji do kolejki.", "ADD");
        }
        catch (Exception ex)
        {
            AddItemToCollection(input, input, "", fmt, br);
            GlobalStatusInfo.Text = $"Dodano pozycję (Błąd parsowania: {ex.Message})";
            LogService.Warn($"Błąd parsowania źródła ({ex.Message}), dodano jako zapytanie bezpośrednie.", "PARSER");
        }

        UpdateStats();
    }

    private void AddItemToCollection(string query, string title, string artist, DownloadFormat format, AudioBitrate bitrate)
    {
        var item = new DownloadItem
        {
            Index = _items.Count + 1,
            QueryOrUrl = query,
            Title = string.IsNullOrWhiteSpace(title) ? query : title,
            Artist = artist,
            SelectedFormat = format,
            SelectedBitrate = bitrate,
            Status = DownloadStatus.Pending,
            StatusMessage = "W kolejce"
        };

        _items.Add(item);
    }

    private void OpenPasteListDialog_Click(object sender, RoutedEventArgs e)
    {
        var fmt = DefaultFormatComboBox.SelectedValue is DownloadFormat f ? f : DownloadFormat.MP3;
        var br = DefaultBitrateComboBox.SelectedValue is AudioBitrate b ? b : AudioBitrate.B320;

        var dialog = new PasteListDialog(fmt, br)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var line in dialog.ResultLines)
            {
                if (line.Contains(" - "))
                {
                    var parts = line.Split([" - "], 2, StringSplitOptions.TrimEntries);
                    AddItemToCollection(line, parts[1], parts[0], dialog.SelectedFormat, dialog.SelectedBitrate);
                }
                else
                {
                    AddItemToCollection(line, line, "", dialog.SelectedFormat, dialog.SelectedBitrate);
                }
            }

            UpdateStats();
            GlobalStatusInfo.Text = $"Wklejono {dialog.ResultLines.Count} pozycji do kolejki.";
            LogService.Success($"Wklejono {dialog.ResultLines.Count} pozycji z okna dialogowego.", "ADD");
        }
    }

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Wybierz plik z listą utworów",
            Filter = "Wszystkie obsługiwane listy (*.csv;*.txt;*.m3u;*.m3u8)|*.csv;*.txt;*.m3u;*.m3u8|Pliki CSV / Spotify (*.csv)|*.csv|Pliki tekstowe TXT (*.txt)|*.txt|Playlisty M3U / M3U8 (*.m3u;*.m3u8)|*.m3u;*.m3u8|Wszystkie pliki (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            GlobalStatusInfo.Text = "Wczytywanie pliku...";
            LogService.Info($"Importowanie pliku: {ofd.FileName}", "IMPORT");
            try
            {
                var list = await _importService.ImportFileAsync(ofd.FileName);
                var fmt = DefaultFormatComboBox.SelectedValue is DownloadFormat f ? f : DownloadFormat.MP3;
                var br = DefaultBitrateComboBox.SelectedValue is AudioBitrate b ? b : AudioBitrate.B320;

                foreach (var item in list)
                {
                    AddItemToCollection(item.QueryOrUrl, item.Title, item.Artist, fmt, br);
                }

                UpdateStats();
                GlobalStatusInfo.Text = $"Wczytano {list.Count} utworów z pliku {Path.GetFileName(ofd.FileName)}";
                LogService.Success($"Wczytano pomyślnie {list.Count} utworów z {Path.GetFileName(ofd.FileName)}", "IMPORT");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Błąd podczas wczytywania pliku: {ex.Message}", "Błąd importu", MessageBoxButton.OK, MessageBoxImage.Error);
                GlobalStatusInfo.Text = "Błąd importu pliku.";
                LogService.Error($"Błąd wczytywania pliku: {ex.Message}", "IMPORT");
            }
        }
    }

    private void DefaultFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (DefaultFormatComboBox.SelectedValue is DownloadFormat fmt)
        {
            DefaultBitrateComboBox.IsEnabled = !fmt.IsVideo();
            SaveCurrentConfig();
        }
    }

    private void BatchChangeFormat_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = QueueDataGrid.SelectedItems.OfType<DownloadItem>().ToList();
        if (selectedItems.Count == 0)
        {
            MessageBox.Show("Zaznacz w tabeli utwory, którym chcesz zmienić format (użyj Ctrl lub Shift do zaznaczenia wielu).", "Brak zaznaczenia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newFormat = DefaultFormatComboBox.SelectedValue is DownloadFormat f ? f : DownloadFormat.MP3;
        var newBitrate = DefaultBitrateComboBox.SelectedValue is AudioBitrate b ? b : AudioBitrate.B320;

        foreach (var item in selectedItems)
        {
            item.SelectedFormat = newFormat;
            item.SelectedBitrate = newBitrate;
        }

        GlobalStatusInfo.Text = $"Zaktualizowano format dla {selectedItems.Count} zaznaczonych pozycji.";
        LogService.Info($"Zaktualizowano format na {newFormat} dla {selectedItems.Count} pozycji.", "FORMAT");
    }

    private void ClearList_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        if (_downloaderEngine.IsRunning)
        {
            MessageBox.Show("Zatrzymaj najpierw pobieranie, aby wyczyścić listę.", "Pobieranie trwa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show("Czy na pewno chcesz usunąć wszystkie pozycje z kolejki?", "Wyczyść listę", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _items.Clear();
            UpdateStats();
            GlobalStatusInfo.Text = "Wyczyszczono listę.";
            LogService.Info("Wyczyszczono całą listę pobierania.", "LIST");
        }
    }

    private void QueueDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is DownloadItem item)
        {
            // Cycle formats: MP3 -> MP4 -> FLAC -> M4A -> WAV -> OPUS -> MP3
            var allFormats = Enum.GetValues<DownloadFormat>();
            int nextIdx = ((int)item.SelectedFormat + 1) % allFormats.Length;
            item.SelectedFormat = allFormats[nextIdx];
            GlobalStatusInfo.Text = $"Zmieniono format utworu #{item.Index} na {item.SelectedFormat}";
            LogService.Info($"Zmieniono format utworu #{item.Index} ({item.Title}) na {item.SelectedFormat}", "FORMAT");
        }
    }

    private async void StartDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            MessageBox.Show("Dodaj utwory lub linki do listy przed uruchomieniem pobierania.", "Pusta kolejka", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputDir = OutputPathTextBox.Text;
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
        {
            outputDir = ConfigService.GetDefaultOutputDirectory();
            OutputPathTextBox.Text = outputDir;
        }

        SaveCurrentConfig();

        int threads = ThreadsComboBox.SelectedItem is int t ? t : 4;
        bool anti403 = Anti403CheckBox.IsChecked == true;
        bool autoSkip = AutoSkipCheckBox.IsChecked == true;
        bool embedThumb = EmbedThumbnailsCheckBox.IsChecked == true;
        bool embedMeta = EmbedMetadataCheckBox.IsChecked == true;

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        GlobalStatusInfo.Text = "Trwa pobieranie...";

        LogService.Info($"▶ Rozpoczęto pobieranie {_items.Count} utworów. Folder docelowy: {outputDir}", "START");

        await _downloaderEngine.StartQueueAsync(
            _items,
            outputDir,
            threads,
            anti403,
            autoSkip,
            embedThumb,
            embedMeta
        );
    }

    private void StopDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloaderEngine.Stop();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        GlobalStatusInfo.Text = "Zatrzymano pobieranie.";
        LogService.Warn("Zatrzymano pobieranie na żądanie użytkownika.", "STOP");
    }

    private void OnItemStatusChanged(DownloadItem item)
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdateStats();
        });
    }

    private void OnQueueFinished()
    {
        Dispatcher.InvokeAsync(() =>
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            UpdateStats();
            GlobalStatusInfo.Text = "Wszystkie zadania z kolejki zostały przetworzone.";
        });
    }

    private void UpdateStats()
    {
        int total = _items.Count;
        int pending = _items.Count(x => x.Status == DownloadStatus.Pending);
        int downloading = _items.Count(x => x.Status == DownloadStatus.Downloading || x.Status == DownloadStatus.Converting || x.Status == DownloadStatus.Searching);
        int completed = _items.Count(x => x.Status == DownloadStatus.Completed);
        int skipped = _items.Count(x => x.Status == DownloadStatus.Skipped);
        int errors = _items.Count(x => x.Status == DownloadStatus.Error);

        StatTotalText.Text = $"📌 WSZYSTKICH: {total}";
        StatPendingText.Text = $"⏳ W KOLEJCE: {pending}";
        StatDownloadingText.Text = $"⚡ POBIERANIE: {downloading}";
        StatCompletedText.Text = $"✅ GOTOWE: {completed}";
        StatSkippedText.Text = $"⏭ POMINIĘTE: {skipped}";
        StatErrorText.Text = $"❌ BŁĘDY: {errors}";

        EmptyQueuePlaceholder.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (total > 0)
        {
            double finished = completed + skipped;
            double pct = (finished / total) * 100;
            MasterProgressBar.Value = pct;
            MasterProgressPercentText.Text = $"{pct:F0}%";
        }
        else
        {
            MasterProgressBar.Value = 0;
            MasterProgressPercentText.Text = "0%";
        }
    }

    private void DeleteSingleRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DownloadItem item)
        {
            _items.Remove(item);
            ReindexItems();
            UpdateStats();
            LogService.Info($"Usunięto utwór: {item.Title}", "LIST");
        }
    }

    private void RetryErrors_Click(object sender, RoutedEventArgs e)
    {
        var errorItems = _items.Where(x => x.Status == DownloadStatus.Error || x.Status == DownloadStatus.Stopped).ToList();
        if (errorItems.Count == 0)
        {
            MessageBox.Show("Brak utworów ze statusem błędu do ponowienia.", "Brak błędów", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var item in errorItems)
        {
            item.Status = DownloadStatus.Pending;
            item.ProgressPercentage = 0;
            item.StatusMessage = "Oczekuje na ponowienie";
            item.ErrorMessage = "";
        }

        UpdateStats();
        GlobalStatusInfo.Text = $"Zresetowano {errorItems.Count} błędnych utworów. Kliknij START POBIERANIA.";
        LogService.Info($"Zresetowano status dla {errorItems.Count} błędnych utworów.", "RETRY");
    }

    private void ContextRetry_Click(object sender, RoutedEventArgs e)
    {
        var selected = QueueDataGrid.SelectedItems.OfType<DownloadItem>().ToList();
        if (selected.Count == 0 && QueueDataGrid.SelectedItem is DownloadItem single)
        {
            selected.Add(single);
        }

        foreach (var item in selected)
        {
            item.Status = DownloadStatus.Pending;
            item.ProgressPercentage = 0;
            item.StatusMessage = "Oczekuje na ponowienie";
            item.ErrorMessage = "";
            LogService.Info($"Oznaczono #{item.Index} ({item.Title}) do ponownego pobrania.", "QUEUE");
        }

        UpdateStats();
        GlobalStatusInfo.Text = $"Oznaczono {selected.Count} utworów do ponownego pobrania. Kliknij START.";
    }

    private void ContextChangeFormat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string formatStr &&
            Enum.TryParse<DownloadFormat>(formatStr, out var fmt))
        {
            foreach (var item in QueueDataGrid.SelectedItems.OfType<DownloadItem>())
            {
                item.SelectedFormat = fmt;
            }
            UpdateStats();
        }
    }

    private void ContextOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is DownloadItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.OutputPath) && File.Exists(item.OutputPath))
            {
                Process.Start("explorer.exe", $"/select,\"{item.OutputPath}\"");
            }
            else
            {
                var dir = OutputPathTextBox.Text;
                if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
            }
        }
    }

    private void ContextOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is DownloadItem item)
        {
            var url = PlaylistParserService.IsDirectUrl(item.QueryOrUrl)
                ? item.QueryOrUrl
                : $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(item.Title + " " + item.Artist)}";

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }
    }

    private void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = QueueDataGrid.SelectedItems.OfType<DownloadItem>().ToList();
        foreach (var item in selected)
        {
            _items.Remove(item);
        }
        ReindexItems();
        UpdateStats();
    }

    private void ReindexItems()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].Index = i + 1;
        }
    }
}
using System.Windows;
using System.Windows.Controls;
using ExportX.Models;

namespace ExportX.Views;

public partial class PasteListDialog : Window
{
    public List<string> ResultLines { get; private set; } = new();
    public DownloadFormat SelectedFormat { get; private set; } = DownloadFormat.MP3;
    public AudioBitrate SelectedBitrate { get; private set; } = AudioBitrate.B320;

    public PasteListDialog(DownloadFormat defaultFormat, AudioBitrate defaultBitrate)
    {
        InitializeComponent();

        SelectedFormat = defaultFormat;
        SelectedBitrate = defaultBitrate;

        // Populate Formats
        FormatComboBox.ItemsSource = Enum.GetValues<DownloadFormat>();
        FormatComboBox.SelectedItem = defaultFormat;

        // Populate Bitrates
        BitrateComboBox.ItemsSource = Enum.GetValues<AudioBitrate>();
        BitrateComboBox.SelectedItem = defaultBitrate;

        UpdateLineCount();
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLineCount();
    }

    private void UpdateLineCount()
    {
        var lines = GetLines();
        LineCountText.Text = $"LINII: {lines.Count}";
    }

    private List<string> GetLines()
    {
        return InputTextBox.Text
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
            .ToList();
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatComboBox.SelectedItem is DownloadFormat fmt)
        {
            SelectedFormat = fmt;
            BitrateComboBox.IsEnabled = !fmt.IsVideo();
        }
    }

    private void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(InputTextBox.Text))
            {
                InputTextBox.Text = text;
            }
            else
            {
                InputTextBox.AppendText("\n" + text);
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        ResultLines = GetLines();
        if (FormatComboBox.SelectedItem is DownloadFormat fmt)
        {
            SelectedFormat = fmt;
        }
        if (BitrateComboBox.SelectedItem is AudioBitrate br)
        {
            SelectedBitrate = br;
        }

        if (ResultLines.Count == 0)
        {
            MessageBox.Show("Wprowadź lub wklej przynajmniej jeden utwór lub link.", "Brak danych", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}

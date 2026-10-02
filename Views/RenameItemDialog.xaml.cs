using System.IO;
using System.Windows;
using System.Windows.Input;
using ExportX.Models;

namespace ExportX.Views;

public partial class RenameItemDialog : Window
{
    private readonly DownloadItem _item;

    public string ResultCustomFileName { get; private set; } = string.Empty;

    public RenameItemDialog(DownloadItem item)
    {
        InitializeComponent();
        _item = item;

        var displayArtist = string.IsNullOrWhiteSpace(item.Artist) ? "" : $"{item.Artist} - ";
        TrackInfoText.Text = $"{displayArtist}{item.Title}";

        if (!string.IsNullOrWhiteSpace(item.CustomFileName))
        {
            FileNameTextBox.Text = item.CustomFileName;
        }
        else if (!string.IsNullOrWhiteSpace(item.Artist))
        {
            FileNameTextBox.Text = $"{item.Artist} - {item.Title}";
        }
        else
        {
            FileNameTextBox.Text = item.Title;
        }

        FileNameTextBox.Focus();
        FileNameTextBox.SelectAll();
    }

    private void PresetTitle_Click(object sender, RoutedEventArgs e)
    {
        FileNameTextBox.Text = Sanitize(_item.Title);
    }

    private void PresetArtistTitle_Click(object sender, RoutedEventArgs e)
    {
        FileNameTextBox.Text = string.IsNullOrWhiteSpace(_item.Artist) 
            ? Sanitize(_item.Title) 
            : $"{Sanitize(_item.Artist)} - {Sanitize(_item.Title)}";
    }

    private void PresetTitleArtist_Click(object sender, RoutedEventArgs e)
    {
        FileNameTextBox.Text = string.IsNullOrWhiteSpace(_item.Artist) 
            ? Sanitize(_item.Title) 
            : $"{Sanitize(_item.Title)} - {Sanitize(_item.Artist)}";
    }

    private void PresetDefault_Click(object sender, RoutedEventArgs e)
    {
        FileNameTextBox.Clear();
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        ResultCustomFileName = string.Empty;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultCustomFileName = Sanitize(FileNameTextBox.Text.Trim());
        DialogResult = true;
        Close();
    }

    private void FileNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }

    private static string Sanitize(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(input.Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }
}

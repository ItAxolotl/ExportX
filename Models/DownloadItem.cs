using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ExportX.Models;

public class DownloadItem : INotifyPropertyChanged
{
    private static readonly SolidColorBrush BrushPending = new((Color)ColorConverter.ConvertFromString("#E5E7EB"));
    private static readonly SolidColorBrush BrushSearching = new((Color)ColorConverter.ConvertFromString("#67E8F9"));
    private static readonly SolidColorBrush BrushDownloading = new((Color)ColorConverter.ConvertFromString("#FDBA74"));
    private static readonly SolidColorBrush BrushConverting = new((Color)ColorConverter.ConvertFromString("#D8B4FE"));
    private static readonly SolidColorBrush BrushCompleted = new((Color)ColorConverter.ConvertFromString("#4ADE80"));
    private static readonly SolidColorBrush BrushSkipped = new((Color)ColorConverter.ConvertFromString("#D1D5DB"));
    private static readonly SolidColorBrush BrushError = new((Color)ColorConverter.ConvertFromString("#FCA5A5"));
    private static readonly SolidColorBrush BrushStopped = new((Color)ColorConverter.ConvertFromString("#FDA4AF"));

    static DownloadItem()
    {
        BrushPending.Freeze();
        BrushSearching.Freeze();
        BrushDownloading.Freeze();
        BrushConverting.Freeze();
        BrushCompleted.Freeze();
        BrushSkipped.Freeze();
        BrushError.Freeze();
        BrushStopped.Freeze();
    }

    private int _index;
    private string _queryOrUrl = string.Empty;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private DownloadFormat _selectedFormat = DownloadFormat.MP3;
    private AudioBitrate _selectedBitrate = AudioBitrate.B320;
    private DownloadStatus _status = DownloadStatus.Pending;
    private double _progressPercentage;
    private string _speed = string.Empty;
    private string _eta = string.Empty;
    private string _totalSize = string.Empty;
    private string _statusMessage = "W kolejce";
    private string _errorMessage = string.Empty;
    private string _outputPath = string.Empty;
    private bool _isSelected;

    public int Index
    {
        get => _index;
        set => SetField(ref _index, value);
    }

    public string QueryOrUrl
    {
        get => _queryOrUrl;
        set => SetField(ref _queryOrUrl, value);
    }

    public string Title
    {
        get => string.IsNullOrWhiteSpace(_title) ? _queryOrUrl : _title;
        set => SetField(ref _title, value);
    }

    public string Artist
    {
        get => _artist;
        set => SetField(ref _artist, value);
    }

    public DownloadFormat SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (SetField(ref _selectedFormat, value))
            {
                OnPropertyChanged(nameof(FormatDisplayName));
                OnPropertyChanged(nameof(IsAudioFormat));
            }
        }
    }

    public AudioBitrate SelectedBitrate
    {
        get => _selectedBitrate;
        set
        {
            if (SetField(ref _selectedBitrate, value))
            {
                OnPropertyChanged(nameof(BitrateDisplayName));
            }
        }
    }

    public string FormatDisplayName => SelectedFormat.GetDisplayName();
    public string BitrateDisplayName => SelectedFormat == DownloadFormat.MP4 ? "N/A (Wideo)" : SelectedBitrate.GetDisplayName();
    public bool IsAudioFormat => SelectedFormat != DownloadFormat.MP4;

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusDisplayName));
                OnPropertyChanged(nameof(StatusColorHex));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string StatusDisplayName => _status.GetDisplayName();

    public SolidColorBrush StatusBrush => _status switch
    {
        DownloadStatus.Pending => BrushPending,
        DownloadStatus.Searching => BrushSearching,
        DownloadStatus.Downloading => BrushDownloading,
        DownloadStatus.Converting => BrushConverting,
        DownloadStatus.Completed => BrushCompleted,
        DownloadStatus.Skipped => BrushSkipped,
        DownloadStatus.Error => BrushError,
        DownloadStatus.Stopped => BrushStopped,
        _ => BrushPending
    };

    public string StatusColorHex => _status switch
    {
        DownloadStatus.Pending => "#E5E7EB",
        DownloadStatus.Searching => "#67E8F9",
        DownloadStatus.Downloading => "#FDBA74",
        DownloadStatus.Converting => "#D8B4FE",
        DownloadStatus.Completed => "#4ADE80",
        DownloadStatus.Skipped => "#D1D5DB",
        DownloadStatus.Error => "#FCA5A5",
        DownloadStatus.Stopped => "#FDA4AF",
        _ => "#000000"
    };

    public double ProgressPercentage
    {
        get => _progressPercentage;
        set => SetField(ref _progressPercentage, value);
    }

    public string Speed
    {
        get => _speed;
        set => SetField(ref _speed, value);
    }

    public string Eta
    {
        get => _eta;
        set => SetField(ref _eta, value);
    }

    public string TotalSize
    {
        get => _totalSize;
        set => SetField(ref _totalSize, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetField(ref _outputPath, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

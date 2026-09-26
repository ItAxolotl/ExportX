using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace ExportX.Services;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Debug,
    Process
}

public class LogEntry
{
    private static readonly SolidColorBrush BrushInfo = new((Color)ColorConverter.ConvertFromString("#60A5FA"));
    private static readonly SolidColorBrush BrushSuccess = new((Color)ColorConverter.ConvertFromString("#4ADE80"));
    private static readonly SolidColorBrush BrushWarn = new((Color)ColorConverter.ConvertFromString("#FBBF24"));
    private static readonly SolidColorBrush BrushError = new((Color)ColorConverter.ConvertFromString("#F87171"));
    private static readonly SolidColorBrush BrushDebug = new((Color)ColorConverter.ConvertFromString("#9CA3AF"));
    private static readonly SolidColorBrush BrushProcess = new((Color)ColorConverter.ConvertFromString("#C084FC"));

    static LogEntry()
    {
        BrushInfo.Freeze();
        BrushSuccess.Freeze();
        BrushWarn.Freeze();
        BrushError.Freeze();
        BrushDebug.Freeze();
        BrushProcess.Freeze();
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    public string TimeString => Timestamp.ToString("HH:mm:ss.fff");

    public string LevelBadge => Level switch
    {
        LogLevel.Info => "[INFO]",
        LogLevel.Success => "[OK]",
        LogLevel.Warning => "[WARN]",
        LogLevel.Error => "[ERROR]",
        LogLevel.Debug => "[DEBUG]",
        LogLevel.Process => "[CORE]",
        _ => "[LOG]"
    };

    public SolidColorBrush LevelBrush => Level switch
    {
        LogLevel.Info => BrushInfo,
        LogLevel.Success => BrushSuccess,
        LogLevel.Warning => BrushWarn,
        LogLevel.Error => BrushError,
        LogLevel.Debug => BrushDebug,
        LogLevel.Process => BrushProcess,
        _ => BrushInfo
    };

    public string LevelColorHex => Level switch
    {
        LogLevel.Info => "#60A5FA",
        LogLevel.Success => "#4ADE80",
        LogLevel.Warning => "#FBBF24",
        LogLevel.Error => "#F87171",
        LogLevel.Debug => "#9CA3AF",
        LogLevel.Process => "#C084FC",
        _ => "#FFFFFF"
    };

    public string FormattedLine => $"{TimeString} {LevelBadge} {(!string.IsNullOrEmpty(Source) ? $"[{Source}] " : "")}{Message}";
}

public static class LogService
{
    private static readonly object _lock = new();
    public static ObservableCollection<LogEntry> LogEntries { get; } = new();
    public static event Action<LogEntry>? LogAdded;

    public static string LogFilePath => Path.Combine(ConfigService.GetLogsDirectory(), "exportx_app.log");

    static LogService()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch { }
    }

    public static void Log(string message, LogLevel level = LogLevel.Info, string source = "")
    {
        var entry = new LogEntry
        {
            Level = level,
            Message = message,
            Source = source,
            Timestamp = DateTime.Now
        };

        // Write to log file
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogFilePath, entry.FormattedLine + Environment.NewLine);
            }
        }
        catch { }

        // Dispatch to UI collection
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (LogEntries.Count > 2000)
                {
                    LogEntries.RemoveAt(0);
                }
                LogEntries.Add(entry);
                LogAdded?.Invoke(entry);
            });
        }
    }

    public static void Info(string message, string source = "") => Log(message, LogLevel.Info, source);
    public static void Success(string message, string source = "") => Log(message, LogLevel.Success, source);
    public static void Warn(string message, string source = "") => Log(message, LogLevel.Warning, source);
    public static void Error(string message, string source = "") => Log(message, LogLevel.Error, source);
    public static void Debug(string message, string source = "") => Log(message, LogLevel.Debug, source);
    public static void Process(string message, string source = "") => Log(message, LogLevel.Process, source);

    public static void Clear()
    {
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LogEntries.Clear();
            });
        }
    }
}

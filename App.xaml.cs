using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ExportX.Services;

namespace ExportX;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (object sender, DispatcherUnhandledExceptionEventArgs args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            MessageBox.Show($"Wystąpił nieoczekiwany błąd:\n{args.Exception.Message}\n\nSzczegóły:\n{GetFullExceptionDetails(args.Exception)}", "Błąd aplikacji", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private static string GetFullExceptionDetails(Exception? ex)
    {
        if (ex == null) return "Brak szczegółów wyjątku.";
        var sb = new StringBuilder();
        var cur = ex;
        int depth = 0;
        while (cur != null && depth < 5)
        {
            sb.AppendLine($"[Level {depth}] {cur.GetType().FullName}: {cur.Message}");
            sb.AppendLine(cur.StackTrace);
            cur = cur.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var details = GetFullExceptionDetails(ex);
            var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}]\n{details}\n\n";
            var crashPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExportX", "logs", "crash.log");
            var dir = Path.GetDirectoryName(crashPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(crashPath, msg);
            LogService.Error($"CRASH: {source} -> {details}", "CRASH");
        }
        catch { }
    }
}

using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ExportX.Services;

public static class AuthService
{
    private static CancellationTokenSource? _activeAuthCts;

    public static string DetectDefaultBrowser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var progId = key?.GetValue("ProgId")?.ToString() ?? "";
            if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
            if (progId.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "edge";
            if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
            if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
            if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";
            if (progId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "vivaldi";
        }
        catch { }
        return "chrome";
    }

    public static void OpenGoogleAccountChooser()
    {
        try
        {
            var url = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogService.Info("Otwarto stronę wyboru konta Google w domyślnej przeglądarce.", "AUTH");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Nie udało się otworzyć przeglądarki: {ex.Message}", "AUTH");
        }
    }

    public static void CancelOngoingAuth()
    {
        _activeAuthCts?.Cancel();
        _activeAuthCts = null;
    }

    public static async Task<bool> StartLiveBrowserAuthWatcherAsync(Action<string> onStatusUpdate, TimeSpan? timeout = null)
    {
        CancelOngoingAuth();
        _activeAuthCts = new CancellationTokenSource();
        var token = _activeAuthCts.Token;

        var defaultBrowser = DetectDefaultBrowser();
        var browserList = new[] { defaultBrowser, "chrome", "edge", "firefox", "brave", "opera", "vivaldi" }.Distinct().ToList();

        var ytDlpPath = ToolLocatorService.FindYtDlp();
        var targetCookiesPath = ConfigService.GetCookiesPath();
        var dir = Path.GetDirectoryName(targetCookiesPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempOutPath = Path.Combine(dir ?? AppDomain.CurrentDomain.BaseDirectory, "temp_live_auth.txt");
        var maxWait = timeout ?? TimeSpan.FromMinutes(3);
        var stopwatch = Stopwatch.StartNew();

        onStatusUpdate("Wybierz konto Google na otwartej stronie w przeglądarce...");

        while (!token.IsCancellationRequested && stopwatch.Elapsed < maxWait)
        {
            foreach (var browser in browserList)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    if (File.Exists(tempOutPath)) File.Delete(tempOutPath);

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
                    psi.ArgumentList.Add(tempOutPath);
                    psi.ArgumentList.Add("--skip-download");
                    psi.ArgumentList.Add("https://www.youtube.com");

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync(token);

                        if (File.Exists(tempOutPath) && new FileInfo(tempOutPath).Length > 100)
                        {
                            var content = await File.ReadAllTextAsync(tempOutPath, token);
                            if (content.Contains("LOGIN_INFO") || content.Contains("SID") || content.Contains("SAPISID") || content.Contains("__Secure"))
                            {
                                if (File.Exists(targetCookiesPath)) File.Delete(targetCookiesPath);
                                File.Move(tempOutPath, targetCookiesPath);

                                LogService.Success($"Pomyślnie wykryto zalogowane konto w przeglądarce ({browser})!", "AUTH");
                                onStatusUpdate("Zalogowano pomyślnie!");
                                return true;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch { }
            }

            try
            {
                await Task.Delay(2000, token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        if (File.Exists(tempOutPath)) try { File.Delete(tempOutPath); } catch { }
        return false;
    }
}

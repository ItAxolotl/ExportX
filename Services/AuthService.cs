using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ExportX.Services;

public static class AuthService
{
    public static string DetectActiveOrTargetBrowser()
    {
        // 1. Check if any popular browser is currently running
        var runningChecks = new (string procName, string browserKey)[]
        {
            ("brave", "brave"),
            ("chrome", "chrome"),
            ("opera", "opera"),
            ("msedge", "edge"),
            ("firefox", "firefox"),
            ("vivaldi", "vivaldi")
        };

        foreach (var (procName, browserKey) in runningChecks)
        {
            if (Process.GetProcessesByName(procName).Length > 0)
            {
                return browserKey;
            }
        }

        // 2. Otherwise detect system default from Windows Registry
        return DetectDefaultBrowser();
    }

    public static string DetectDefaultBrowser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var progId = key?.GetValue("ProgId")?.ToString() ?? "";
            if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";
            if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
            if (progId.Contains("Edge", StringComparison.OrdinalIgnoreCase) || progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) return "edge";
            if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
            if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
            if (progId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "vivaldi";
        }
        catch { }
        return "chrome";
    }

    public static string GetProcessNameForBrowser(string browser) => browser.ToLowerInvariant() switch
    {
        "edge" => "msedge",
        "chrome" => "chrome",
        "brave" => "brave",
        "firefox" => "firefox",
        "opera" => "opera",
        "operagx" => "opera",
        "vivaldi" => "vivaldi",
        _ => browser.ToLowerInvariant()
    };

    public static async Task<(bool success, string message)> ExtractCookiesWithQuickRestartAsync(
        string browser,
        Action<string>? progressCallback = null)
    {
        try
        {
            var ytDlpPath = ToolLocatorService.FindYtDlp();
            var targetCookiesPath = ConfigService.GetCookiesPath();
            var dir = Path.GetDirectoryName(targetCookiesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var procName = GetProcessNameForBrowser(browser);
            var runningProcs = Process.GetProcessesByName(procName);
            string? browserExePath = null;
            bool wasRunning = runningProcs.Length > 0;

            if (wasRunning)
            {
                try
                {
                    browserExePath = runningProcs[0].MainModule?.FileName;
                }
                catch { }

                if (string.IsNullOrEmpty(browserExePath) || !File.Exists(browserExePath))
                {
                    browserExePath = FindBrowserExePath(browser);
                }

                // Force kill all child processes of the browser to release SQLite database file locks
                if (browser != "firefox")
                {
                    progressCallback?.Invoke($"Zwalnianie bazy sesji z {browser.ToUpperInvariant()}...");
                    foreach (var p in runningProcs)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                    }

                    await Task.Delay(350);
                }
            }
            else
            {
                browserExePath = FindBrowserExePath(browser);
            }

            // Execute yt-dlp extraction with instant fast exit
            progressCallback?.Invoke($"Pobieranie sesji z {browser.ToUpperInvariant()}...");
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
            psi.ArgumentList.Add(targetCookiesPath);
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("--playlist-items");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--no-update");
            psi.ArgumentList.Add("--socket-timeout");
            psi.ArgumentList.Add("6");
            psi.ArgumentList.Add("https://www.youtube.com");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var process = Process.Start(psi);
                if (process != null)
                {
                    var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                    var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                    await Task.WhenAll(process.WaitForExitAsync(cts.Token), stdoutTask, stderrTask);
                }
            }
            catch { }

            // Immediately restart the browser if it was running before
            if (wasRunning && !string.IsNullOrEmpty(browserExePath) && File.Exists(browserExePath))
            {
                progressCallback?.Invoke($"Wznawianie przeglądarki {browser.ToUpperInvariant()} z otwartymi kartami...");
                try
                {
                    Process.Start(new ProcessStartInfo(browserExePath) { UseShellExecute = true });
                }
                catch { }
            }

            // Verify whether valid cookies were saved
            if (ConfigService.HasValidCookies())
            {
                // Also sync to Roaming if we are portable, or to portable if we are Roaming
                try
                {
                    var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExportX", "youtube_cookies.txt");
                    if (targetCookiesPath != appDataPath && File.Exists(targetCookiesPath))
                    {
                        var adDir = Path.GetDirectoryName(appDataPath);
                        if (!string.IsNullOrEmpty(adDir) && !Directory.Exists(adDir)) Directory.CreateDirectory(adDir);
                        File.Copy(targetCookiesPath, appDataPath, true);
                    }
                }
                catch { }

                LogService.Success($"Pomyślnie pobrano autentyczną sesję YouTube z przeglądarki {browser.ToUpperInvariant()}!", "AUTH");
                return (true, $"Pomyślnie pobrano sesję z przeglądarki {browser.ToUpperInvariant()} bez konieczności wpisywania haseł!");
            }

            return (false, $"Nie udało się pobrać aktywnej sesji YouTube z przeglądarki {browser.ToUpperInvariant()}.");
        }
        catch (Exception ex)
        {
            return (false, $"Błąd podczas pobierania sesji: {ex.Message}");
        }
    }

    private static string? FindBrowserExePath(string browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return browser.ToLowerInvariant() switch
        {
            "brave" => Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            "chrome" => Path.Combine(progFiles, @"Google\Chrome\Application\chrome.exe"),
            "edge" => Path.Combine(progFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
            "firefox" => Path.Combine(progFiles, @"Mozilla Firefox\firefox.exe"),
            "opera" => File.Exists(Path.Combine(local, @"Programs\Opera GX\launcher.exe")) 
                ? Path.Combine(local, @"Programs\Opera GX\launcher.exe")
                : Path.Combine(local, @"Programs\Opera\launcher.exe"),
            "vivaldi" => Path.Combine(local, @"Vivaldi\Application\vivaldi.exe"),
            _ => null
        };
    }
}

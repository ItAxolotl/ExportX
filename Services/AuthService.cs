using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ExportX.Services;

public static class AuthService
{
    public static string DetectDefaultBrowser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var progId = key?.GetValue("ProgId")?.ToString() ?? "";
            if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
            if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
            if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
            if (progId.Contains("Edge", StringComparison.OrdinalIgnoreCase) || progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) return "edge";
            if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";
            if (progId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "vivaldi";
        }
        catch { }
        return "brave";
    }

    public static string GetProcessNameForBrowser(string browser) => browser.ToLowerInvariant() switch
    {
        "edge" => "msedge",
        "chrome" => "chrome",
        "brave" => "brave",
        "firefox" => "firefox",
        "opera" => "opera",
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

            if (runningProcs.Length > 0)
            {
                try
                {
                    browserExePath = runningProcs[0].MainModule?.FileName;
                }
                catch { }

                // Fallback default paths if MainModule is inaccessible
                if (string.IsNullOrEmpty(browserExePath) || !File.Exists(browserExePath))
                {
                    browserExePath = FindBrowserExePath(browser);
                }

                // If it's a Chromium browser, close it gracefully to unlock the SQLite DB
                if (browser != "firefox")
                {
                    progressCallback?.Invoke($"Zamykanie {browser.ToUpperInvariant()} na 1 sekundę w celu zwolnienia bazy sesji...");
                    foreach (var p in runningProcs)
                    {
                        try { p.CloseMainWindow(); } catch { }
                    }

                    await Task.Delay(800);

                    // Re-check and force kill if still lingering in background
                    var lingering = Process.GetProcessesByName(procName);
                    foreach (var p in lingering)
                    {
                        try { p.Kill(); } catch { }
                    }

                    await Task.Delay(400);
                }
            }

            // Execute yt-dlp extraction
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
            psi.ArgumentList.Add("https://www.youtube.com");

            using (var process = Process.Start(psi))
            {
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }
            }

            // Immediately restart the browser if it was previously running
            if (!string.IsNullOrEmpty(browserExePath) && File.Exists(browserExePath))
            {
                progressCallback?.Invoke($"Wznawianie przeglądarki {browser.ToUpperInvariant()} z otwartymi kartami...");
                try
                {
                    var rPsi = new ProcessStartInfo
                    {
                        FileName = browserExePath,
                        UseShellExecute = true
                    };
                    rPsi.ArgumentList.Add("--restore-last-session");
                    Process.Start(rPsi);
                }
                catch { }
            }

            // Verify whether valid cookies were saved
            if (ConfigService.HasValidCookies())
            {
                LogService.Success($"Pomyślnie pobrano autentyczną sesję YouTube z przeglądarki {browser.ToUpperInvariant()}!", "AUTH");
                return (true, $"Pomyślnie pobrano sesję z przeglądarki {browser.ToUpperInvariant()} bez konieczności wpisywania haseł!");
            }

            return (false, $"Nie znaleziono aktywnego logowania do YouTube w przeglądarce {browser.ToUpperInvariant()}.");
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
            "opera" => Path.Combine(local, @"Programs\Opera\launcher.exe"),
            "vivaldi" => Path.Combine(local, @"Vivaldi\Application\vivaldi.exe"),
            _ => null
        };
    }
}

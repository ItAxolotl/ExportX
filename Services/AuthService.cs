using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using ExportX.Models;
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

    public static string GetSpotifyCookiesPath()
    {
        return Path.Combine(ConfigService.GetDataDirectory(), "spotify_cookies.txt");
    }

    public static async Task<(bool success, string message, SpotifyUserProfile? profile)> ExtractSpotifyCookiesWithQuickRestartAsync(
        string browser,
        Action<string>? progressCallback = null)
    {
        try
        {
            var ytDlpPath = ToolLocatorService.FindYtDlp();
            var targetCookiesPath = GetSpotifyCookiesPath();
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

            progressCallback?.Invoke($"Pobieranie ciasteczek Spotify z {browser.ToUpperInvariant()}...");
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
            psi.ArgumentList.Add("https://open.spotify.com");

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

            if (wasRunning && !string.IsNullOrEmpty(browserExePath) && File.Exists(browserExePath))
            {
                progressCallback?.Invoke($"Wznawianie przeglądarki {browser.ToUpperInvariant()} z otwartymi kartami...");
                try
                {
                    Process.Start(new ProcessStartInfo(browserExePath) { UseShellExecute = true });
                }
                catch { }
            }

            if (File.Exists(targetCookiesPath))
            {
                progressCallback?.Invoke("Weryfikowanie sesji Spotify i logowanie...");
                var (ok, msg, profile) = await GetSpotifyTokenFromCookiesFileAsync(targetCookiesPath);
                if (ok)
                {
                    LogService.Success($"Pomyślnie połączono konto Spotify z przeglądarki {browser.ToUpperInvariant()} ({profile?.DisplayName})!", "SPOTIFY");
                    return (true, $"Pomyślnie zalogowano do Spotify z przeglądarki {browser.ToUpperInvariant()}!", profile);
                }
                return (false, msg, null);
            }

            return (false, $"Nie znaleziono ciasteczek Spotify w przeglądarce {browser.ToUpperInvariant()}. Upewnij się, że jesteś zalogowany na open.spotify.com w tej przeglądarce.", null);
        }
        catch (Exception ex)
        {
            return (false, $"Błąd podczas pobierania sesji: {ex.Message}", null);
        }
    }

    public static async Task<(bool success, string message, SpotifyUserProfile? profile)> GetSpotifyTokenFromCookiesFileAsync(string cookiesFilePath)
    {
        if (!File.Exists(cookiesFilePath))
        {
            return (false, "Plik ciasteczek nie istnieje.", null);
        }

        try
        {
            var content = await File.ReadAllTextAsync(cookiesFilePath);
            var spDcMatch = System.Text.RegularExpressions.Regex.Match(content, @"(?:\t|^)sp_dc\t([^\r\n\t]+)");
            var spDc = spDcMatch.Success ? spDcMatch.Groups[1].Value.Trim() : "";

            if (string.IsNullOrEmpty(spDc))
            {
                // Also check key=value format
                var matchKv = System.Text.RegularExpressions.Regex.Match(content, @"sp_dc=([^;\r\n\t]+)");
                if (matchKv.Success) spDc = matchKv.Groups[1].Value.Trim();
            }

            if (string.IsNullOrEmpty(spDc))
            {
                return (false, "Plik ciasteczek nie zawiera klucza autoryzacyjnego 'sp_dc'. Upewnij się, że wyeksportowano ciasteczka z zalogowanego konta Spotify.", null);
            }

            return await GetSpotifyTokenFromSpDcAsync(spDc);
        }
        catch (Exception ex)
        {
            return (false, $"Błąd odczytu pliku ciasteczek: {ex.Message}", null);
        }
    }

    public static async Task<(bool success, string message, SpotifyUserProfile? profile)> GetSpotifyTokenFromSpDcAsync(string spDc)
    {
        if (string.IsNullOrWhiteSpace(spDc))
        {
            return (false, "Wartość ciasteczka sp_dc jest pusta.", null);
        }

        try
        {
            using var handler = new HttpClientHandler { UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://open.spotify.com/get_access_token");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            req.Headers.Add("Cookie", $"sp_dc={spDc.Trim()}");

            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"Błąd serwera Spotify podczas weryfikacji tokenu (HTTP {(int)resp.StatusCode}). Ciasteczko sp_dc może być wygasłe.", null);
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool isAnon = root.TryGetProperty("isAnonymous", out var aProp) && aProp.GetBoolean();
            if (isAnon)
            {
                return (false, "Spotify odrzuciło ciasteczko logowania (sesja anonimowa). Zaloguj się ponownie na open.spotify.com w przeglądarce.", null);
            }

            var accessToken = root.TryGetProperty("accessToken", out var tokProp) ? tokProp.GetString() : null;
            if (string.IsNullOrEmpty(accessToken))
            {
                return (false, "Nie udało się wyodrębnić tokenu dostępu ze Spotify.", null);
            }

            int expiresIn = 3600;
            if (root.TryGetProperty("accessTokenExpirationTimestampMs", out var expProp))
            {
                var expMs = expProp.GetInt64();
                var totalSec = (expMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000;
                if (totalSec > 60) expiresIn = (int)totalSec;
            }

            var userService = new SpotifyUserService();
            var profile = await userService.GetUserProfileAsync(accessToken);
            SpotifyAuthService.SaveSession(accessToken, expiresIn, profile, spDc.Trim());

            return (true, $"Zalogowano pomyślnie jako {profile?.DisplayName ?? "Użytkownik"}!", profile);
        }
        catch (Exception ex)
        {
            return (false, $"Błąd połączenia ze Spotify: {ex.Message}", null);
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

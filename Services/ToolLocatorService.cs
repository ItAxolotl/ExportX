using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace ExportX.Services;

public static class ToolLocatorService
{
    private static string? _cachedYtDlpPath;
    private static string? _cachedFfmpegPath;

    public static string GetBinDirectory()
    {
        if (ConfigService.IsPortableMode)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var localBin = Path.Combine(baseDir, "bin");
            if (!Directory.Exists(localBin))
            {
                try { Directory.CreateDirectory(localBin); } catch { }
            }
            return localBin;
        }

        var appDataBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ExportX",
            "bin"
        );
        if (!Directory.Exists(appDataBin))
        {
            try { Directory.CreateDirectory(appDataBin); } catch { }
        }
        return appDataBin;
    }

    public static string GetPreferredYtDlpPath()
    {
        return Path.Combine(GetBinDirectory(), "yt-dlp.exe");
    }

    public static string FindYtDlp()
    {
        if (!string.IsNullOrEmpty(_cachedYtDlpPath) && File.Exists(_cachedYtDlpPath))
        {
            return _cachedYtDlpPath;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Check local bundle bin/yt-dlp.exe
        var localBinPath = Path.Combine(baseDir, "bin", "yt-dlp.exe");
        if (File.Exists(localBinPath)) return _cachedYtDlpPath = localBinPath;

        // 2. Check root directory next to ExportX.exe
        var rootLocalPath = Path.Combine(baseDir, "yt-dlp.exe");
        if (File.Exists(rootLocalPath)) return _cachedYtDlpPath = rootLocalPath;

        // 3. Check AppData bin / Portable bin
        var preferredPath = GetPreferredYtDlpPath();
        if (File.Exists(preferredPath)) return _cachedYtDlpPath = preferredPath;

        // 4. Check standard AppData bin
        var appDataBinPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ExportX",
            "bin",
            "yt-dlp.exe"
        );
        if (File.Exists(appDataBinPath)) return _cachedYtDlpPath = appDataBinPath;

        // 5. Search in system PATH
        var pathFromEnv = FindInPath("yt-dlp.exe");
        if (!string.IsNullOrEmpty(pathFromEnv) && !pathFromEnv.Contains("System32", StringComparison.OrdinalIgnoreCase))
        {
            return _cachedYtDlpPath = pathFromEnv;
        }

        // 6. System32 fallback
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "yt-dlp.exe");
        if (File.Exists(sys32)) return _cachedYtDlpPath = sys32;

        return _cachedYtDlpPath = "yt-dlp";
    }

    public static string? FindFfmpegDirectory()
    {
        if (!string.IsNullOrEmpty(_cachedFfmpegPath) && Directory.Exists(_cachedFfmpegPath))
        {
            return _cachedFfmpegPath;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Check local bundle bin folder
        var localBin = Path.Combine(baseDir, "bin");
        if (File.Exists(Path.Combine(localBin, "ffmpeg.exe")))
        {
            return _cachedFfmpegPath = localBin;
        }

        // 2. Check root directory
        if (File.Exists(Path.Combine(baseDir, "ffmpeg.exe")))
        {
            return _cachedFfmpegPath = baseDir;
        }

        // 3. Check AppData bin
        var appDataBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ExportX",
            "bin"
        );
        if (File.Exists(Path.Combine(appDataBin, "ffmpeg.exe")))
        {
            return _cachedFfmpegPath = appDataBin;
        }

        // 4. Check WinGet Packages
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var winGetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winGetPackages))
        {
            try
            {
                var ffmpegExes = Directory.GetFiles(winGetPackages, "ffmpeg.exe", SearchOption.AllDirectories);
                if (ffmpegExes.Length > 0)
                {
                    return _cachedFfmpegPath = Path.GetDirectoryName(ffmpegExes[0]);
                }
            }
            catch { }
        }

        // 5. Check PATH
        var inPath = FindInPath("ffmpeg.exe");
        if (!string.IsNullOrEmpty(inPath))
        {
            return _cachedFfmpegPath = Path.GetDirectoryName(inPath);
        }

        return null;
    }

    private static string? FindInPath(string filename)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var path in paths)
        {
            try
            {
                var full = Path.Combine(path.Trim(), filename);
                if (File.Exists(full)) return full;
            }
            catch { }
        }

        return null;
    }

    public static async Task EnsureToolsAsync(Action<string>? statusCallback = null)
    {
        try
        {
            var ytDlp = FindYtDlp();
            if (string.IsNullOrEmpty(ytDlp) || !File.Exists(ytDlp) || ytDlp == "yt-dlp")
            {
                statusCallback?.Invoke("Pobieranie silnika yt-dlp...");
                LogService.Info("Brak lokalnego yt-dlp.exe. Rozpoczynanie automatycznego pobierania...", "ENGINE");
                await UpdateYtDlpAsync();
            }

            var ffmpegDir = FindFfmpegDirectory();
            if (string.IsNullOrEmpty(ffmpegDir) || !File.Exists(Path.Combine(ffmpegDir, "ffmpeg.exe")))
            {
                statusCallback?.Invoke("Pobieranie silnika FFmpeg...");
                LogService.Info("Brak lokalnego FFmpeg. Rozpoczynanie automatycznego pobierania...", "ENGINE");
                await DownloadFfmpegAsync();
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Automatyczne sprawdzanie narzędzi: {ex.Message}", "ENGINE");
        }
    }

    public static async Task<(bool success, string output)> UpdateYtDlpAsync()
    {
        try
        {
            var binDir = GetBinDirectory();
            var targetPath = GetPreferredYtDlpPath();
            var tempFile = Path.Combine(binDir, "yt-dlp.exe.tmp");

            const string downloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.Add("User-Agent", "ExportX-Downloader/1.2");

            using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);
            }

            // Replace existing binary
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            File.Move(tempFile, targetPath, overwrite: true);

            _cachedYtDlpPath = targetPath;

            // Get new version string
            var psi = new ProcessStartInfo
            {
                FileName = targetPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            var version = p != null ? (await p.StandardOutput.ReadToEndAsync()).Trim() : "latest";

            return (true, $"Pomyślnie zaktualizowano yt-dlp do najnowszej wersji: {version}\nLokalizacja: {targetPath}");
        }
        catch (Exception ex)
        {
            return (false, $"Błąd podczas pobierania aktualizacji: {ex.Message}");
        }
    }

    public static async Task<(bool success, string output)> DownloadFfmpegAsync()
    {
        try
        {
            var binDir = GetBinDirectory();
            var targetFfmpeg = Path.Combine(binDir, "ffmpeg.exe");
            if (File.Exists(targetFfmpeg)) return (true, "FFmpeg jest już pobrany.");

            const string zipUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
            var tempZip = Path.Combine(binDir, "ffmpeg_download.zip");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Add("User-Agent", "ExportX-Downloader/1.2");

            using (var response = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);
            }

            // Extract ffmpeg.exe and ffprobe.exe
            using (var zip = ZipFile.OpenRead(tempZip))
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                        entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var dest = Path.Combine(binDir, entry.Name);
                        if (File.Exists(dest)) File.Delete(dest);
                        entry.ExtractToFile(dest, overwrite: true);
                    }
                }
            }

            try { File.Delete(tempZip); } catch { }

            _cachedFfmpegPath = binDir;
            return (true, $"Pomyślnie pobrano FFmpeg do: {binDir}");
        }
        catch (Exception ex)
        {
            return (false, $"Błąd podczas pobierania FFmpeg: {ex.Message}");
        }
    }
}

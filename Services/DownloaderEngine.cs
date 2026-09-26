using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using ExportX.Models;

namespace ExportX.Services;

public class DownloaderEngine
{
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    private static readonly Regex DownloadProgressRegex = new(
        @"\[download\]\s+([0-9\.]+)%\s+of\s+~?([0-9\.]+[a-zA-Z]+)\s+at\s+([0-9\.]+[a-zA-Z]+/s)\s+ETA\s+([0-9:]+)",
        RegexOptions.Compiled);

    private static readonly Regex DestinationRegex = new(
        @"\[(?:download|ExtractAudio|Merger)\]\s+(?:Destination:\s+|Merging formats into\s+"")([^""\r\n]+)|\[download\]\s+([^""\r\n]+?)\s+has already been downloaded|\[(?:Metadata|EmbedThumbnail)\]\s+(?:Adding metadata to\s+""|ffmpeg:\s+Adding thumbnail to\s+"")([^""\r\n]+)",
        RegexOptions.Compiled);

    public bool IsRunning => _isRunning;

    public event Action<DownloadItem>? ItemStatusChanged;
    public event Action? QueueFinished;

    public void Stop()
    {
        _cts?.Cancel();
        _isRunning = false;
        LogService.Warn("Zatrzymano kolejkę pobierania.", "ENGINE");
    }

    public async Task StartQueueAsync(
        IEnumerable<DownloadItem> items,
        string outputDirectory,
        int maxDegreeOfParallelism,
        bool enableAnti403,
        bool autoSkipExisting,
        bool embedThumbnail,
        bool embedMetadata)
    {
        if (_isRunning) return;

        _isRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var ytDlpPath = ToolLocatorService.FindYtDlp();
        var ffmpegDir = ToolLocatorService.FindFfmpegDirectory();

        LogService.Info($"Rozpoczęto kolejkę pobierania. Wątki: {maxDegreeOfParallelism}, Folder: {outputDirectory}", "ENGINE");
        LogService.Info($"Wykryty silnik: yt-dlp={ytDlpPath}, ffmpeg={ffmpegDir ?? "(zmienna PATH)"}", "ENGINE");

        if (!Directory.Exists(outputDirectory))
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                LogService.Success($"Utworzono folder docelowy: {outputDirectory}", "ENGINE");
            }
            catch (Exception ex)
            {
                LogService.Error($"Nie można utworzyć folderu docelowego: {ex.Message}", "ENGINE");
            }
        }

        var pendingItems = items.Where(x => x.Status == DownloadStatus.Pending || x.Status == DownloadStatus.Error || x.Status == DownloadStatus.Stopped).ToList();
        LogService.Info($"Liczba pozycji do przetworzenia: {pendingItems.Count}", "ENGINE");

        using var semaphore = new SemaphoreSlim(Math.Clamp(maxDegreeOfParallelism, 1, 16));

        var tasks = pendingItems.Select(async item =>
        {
            await semaphore.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested)
                {
                    item.Status = DownloadStatus.Stopped;
                    item.StatusMessage = "Anulowano";
                    ItemStatusChanged?.Invoke(item);
                    return;
                }

                await ProcessItemWithFallbacksAsync(item, outputDirectory, enableAnti403, autoSkipExisting, embedThumbnail, embedMetadata, ytDlpPath, ffmpegDir, token);
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadStatus.Stopped;
                item.StatusMessage = "Zatrzymano";
                ItemStatusChanged?.Invoke(item);
            }
            catch (Exception ex)
            {
                item.Status = DownloadStatus.Error;
                item.ErrorMessage = ex.Message;
                item.StatusMessage = $"Błąd: {ex.Message}";
                LogService.Error($"Wyjątek podczas pobierania #{item.Index} ({item.Title}): {ex.Message}", "ENGINE");
                ItemStatusChanged?.Invoke(item);
            }
            finally
            {
                semaphore.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Ignored
        }
        finally
        {
            _isRunning = false;
            LogService.Success("Kolejka pobierania zakończyła pracę.", "ENGINE");
            QueueFinished?.Invoke();
        }
    }

    private async Task ProcessItemWithFallbacksAsync(
        DownloadItem item,
        string outputDirectory,
        bool enableAnti403,
        bool autoSkipExisting,
        bool embedThumbnail,
        bool embedMetadata,
        string ytDlpPath,
        string? ffmpegDir,
        CancellationToken ct)
    {
        // 1. Auto-skip check
        if (autoSkipExisting && CheckIfAlreadyExists(item, outputDirectory))
        {
            item.Status = DownloadStatus.Skipped;
            item.ProgressPercentage = 100;
            item.StatusMessage = "Pominięto (plik już istnieje)";
            LogService.Info($"#{item.Index} Pominięto: plik '{Path.GetFileName(item.OutputPath)}' już istnieje na dysku.", "SKIP");
            ItemStatusChanged?.Invoke(item);
            return;
        }

        // 2. Client fallback strategies for YouTube Anti-403 & Quality
        // When session cookies are present: skip android/ios (which don't accept cookies) and use web/desktop with JS runtime.
        // For video (MP4): start with default/web/tv which support 4K/1440p/1080p60.
        // For audio (MP3/FLAC/M4A): start with android/tv when cookies aren't present.
        var clientStrategies = enableAnti403
            ? (ConfigService.HasValidCookies()
                ? new[] { "", "web,mweb", "tv_embedded,web", "web", "mweb" }
                : (item.SelectedFormat.IsVideo()
                    ? new[] { "", "web,mweb", "tv_embedded,web", "ios,web", "android_vr,mweb", "android,web" }
                    : new[] { "android,web", "tv_embedded,android", "ios,web", "android_vr,mweb", "web,mweb", "" }))
            : new[] { "" };

        bool success = false;
        string lastError = string.Empty;

        for (int i = 0; i < clientStrategies.Length; i++)
        {
            if (ct.IsCancellationRequested) break;

            var clientArg = clientStrategies[i];
            var clientName = string.IsNullOrEmpty(clientArg) ? "Domyślny (Najwyższa jakość)" : clientArg;
            LogService.Process($"#{item.Index} [{item.SelectedFormat}] Start pobierania: \"{item.Title}\" (Klient: {clientName}, próba {i + 1}/{clientStrategies.Length})...", "DOWNLOADER");

            var (isOk, err) = await ExecuteYtDlpAsync(item, outputDirectory, clientArg, embedThumbnail, embedMetadata, ytDlpPath, ffmpegDir, ct);

            if (isOk)
            {
                success = true;
                LogService.Success($"#{item.Index} Pomyślnie pobrano: \"{item.Title}\" -> {item.OutputPath}", "SUCCESS");
                break;
            }

            lastError = err;
            LogService.Warn($"#{item.Index} Błąd próby {i + 1} ({clientName}): {err}", "FALLBACK");

            if (i < clientStrategies.Length - 1)
            {
                item.StatusMessage = $"Przełączanie klienta (próba {i + 2}/{clientStrategies.Length})...";
                ItemStatusChanged?.Invoke(item);
                await Task.Delay(1000, ct);
            }
        }

        if (!success && !ct.IsCancellationRequested)
        {
            item.Status = DownloadStatus.Error;
            item.ErrorMessage = lastError;
            item.StatusMessage = string.IsNullOrWhiteSpace(lastError) ? "Błąd pobierania" : $"Błąd: {Truncate(lastError, 60)}";
            LogService.Error($"#{item.Index} Ostateczne niepowodzenie pobierania: \"{item.Title}\". Szczegóły: {lastError}", "ERROR");
            ItemStatusChanged?.Invoke(item);
        }
    }

    private async Task<(bool success, string error)> ExecuteYtDlpAsync(
        DownloadItem item,
        string outputDirectory,
        string clientStrategy,
        bool embedThumbnail,
        bool embedMetadata,
        string ytDlpPath,
        string? ffmpegDir,
        CancellationToken ct)
    {
        item.Status = DownloadStatus.Downloading;
        item.StatusMessage = "Inicjalizacja...";
        item.ProgressPercentage = 0;
        ItemStatusChanged?.Invoke(item);

        var isUrl = PlaylistParserService.IsDirectUrl(item.QueryOrUrl);
        var targetInput = isUrl ? item.QueryOrUrl : $"ytsearch3:{SanitizeSearchQuery(item.QueryOrUrl)}";

        var ext = item.SelectedFormat.GetExtension();
        var bitrate = item.SelectedBitrate.ToKbpsString();
        var outTemplate = "%(title)s [%(id)s].%(ext)s";

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        // Populate ArgumentList safely without broken quote escaping!
        psi.ArgumentList.Add("--paths");
        psi.ArgumentList.Add(outputDirectory);

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outTemplate);

        if (!isUrl)
        {
            psi.ArgumentList.Add("--max-downloads");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("--ignore-errors");
            psi.ArgumentList.Add("--no-abort-on-error");
        }
        else
        {
            psi.ArgumentList.Add("--no-playlist");
        }

        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--no-colors");
        psi.ArgumentList.Add("--no-update");
        psi.ArgumentList.Add("--no-mtime");
        psi.ArgumentList.Add("--windows-filenames");
        psi.ArgumentList.Add("--socket-timeout");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("--retries");
        psi.ArgumentList.Add("10");
        psi.ArgumentList.Add("--fragment-retries");
        psi.ArgumentList.Add("10");

        if (!string.IsNullOrEmpty(ffmpegDir))
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(ffmpegDir);
        }

        psi.ArgumentList.Add("--js-runtimes");
        psi.ArgumentList.Add("node");

        if (ConfigService.HasValidCookies())
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(ConfigService.GetCookiesPath());
        }

        if (!string.IsNullOrWhiteSpace(clientStrategy))
        {
            psi.ArgumentList.Add("--extractor-args");
            psi.ArgumentList.Add($"youtube:player_client={clientStrategy}");
        }

        if (embedMetadata)
        {
            psi.ArgumentList.Add("--embed-metadata");
        }

        if (embedThumbnail)
        {
            psi.ArgumentList.Add("--embed-thumbnail");
        }

        if (item.SelectedFormat.IsVideo())
        {
            // Video format - Absolute Maximum Quality (4K/1440p/1080p60 AV1/VP9/H264 + Best Audio)
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("bestvideo*+bestaudio/best");
            psi.ArgumentList.Add("--format-sort");
            psi.ArgumentList.Add("res,fps,vbr,abr");
            psi.ArgumentList.Add("--merge-output-format");
            psi.ArgumentList.Add("mp4");
            psi.ArgumentList.Add("--no-keep-video");
        }
        else
        {
            // Audio format - Extract audio and convert to requested format & bitrate
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("--audio-format");
            psi.ArgumentList.Add(ext);

            if (item.SelectedFormat == DownloadFormat.MP3 || item.SelectedFormat == DownloadFormat.M4A || item.SelectedFormat == DownloadFormat.OPUS)
            {
                psi.ArgumentList.Add("--audio-quality");
                psi.ArgumentList.Add(bitrate);
            }
        }

        psi.ArgumentList.Add(targetInput);

        var errorOutput = new List<string>();

        try
        {
            using var process = new Process { StartInfo = psi };
            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                var line = e.Data.Trim();

                LogService.Debug($"[yt-dlp #{item.Index}] {line}", "PROCESS");

                // Progress line
                var progressMatch = DownloadProgressRegex.Match(line);
                if (progressMatch.Success)
                {
                    if (double.TryParse(progressMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    {
                        item.ProgressPercentage = pct;
                    }
                    item.TotalSize = progressMatch.Groups[2].Value;
                    item.Speed = progressMatch.Groups[3].Value;
                    item.Eta = progressMatch.Groups[4].Value;
                    item.Status = DownloadStatus.Downloading;
                    item.StatusMessage = $"Pobieranie: {item.ProgressPercentage:F1}% ({item.Speed}, ETA {item.Eta})";
                    ItemStatusChanged?.Invoke(item);
                }
                else if (line.Contains("[ExtractAudio]"))
                {
                    item.Status = DownloadStatus.Converting;
                    item.StatusMessage = "Konwersja audio FFmpeg...";
                    ItemStatusChanged?.Invoke(item);
                }
                else if (line.Contains("[Merger]"))
                {
                    item.Status = DownloadStatus.Converting;
                    item.StatusMessage = "Scalanie wideo HD i audio do MP4...";
                    ItemStatusChanged?.Invoke(item);
                }
                else if (line.Contains("[EmbedThumbnail]") || line.Contains("[Metadata]"))
                {
                    item.Status = DownloadStatus.Converting;
                    item.StatusMessage = "Zapisywanie okładki i tagów ID3...";
                    ItemStatusChanged?.Invoke(item);
                }

                // Parse destination path
                var destMatch = DestinationRegex.Match(line);
                if (destMatch.Success)
                {
                    var p1 = destMatch.Groups[1].Value;
                    var p2 = destMatch.Groups[2].Value;
                    var p3 = destMatch.Groups[3].Value;
                    var captured = !string.IsNullOrEmpty(p1) ? p1 : (!string.IsNullOrEmpty(p2) ? p2 : p3);
                    if (!string.IsNullOrWhiteSpace(captured))
                    {
                        item.OutputPath = captured.Trim().Trim('"');
                    }
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                var line = e.Data.Trim();

                if (!line.Contains("WARNING: Your yt-dlp version"))
                {
                    LogService.Warn($"[yt-dlp stderr #{item.Index}] {line}", "STDERR");
                }

                lock (errorOutput)
                {
                    errorOutput.Add(line);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
            });

            await process.WaitForExitAsync(ct);

            // ExitCode 0 = OK, ExitCode 101 = Max downloads reached (expected when using --max-downloads 1)
            bool isSuccessExit = process.ExitCode == 0 || process.ExitCode == 101;
            bool fileDownloaded = !string.IsNullOrEmpty(item.OutputPath) && File.Exists(item.OutputPath);

            if (isSuccessExit || fileDownloaded)
            {
                item.Status = DownloadStatus.Completed;
                item.ProgressPercentage = 100;
                item.Speed = "";
                item.Eta = "";
                item.StatusMessage = "Pobrano pomyślnie ✔";
                ItemStatusChanged?.Invoke(item);
                return (true, string.Empty);
            }
            else
            {
                // Clean up orphaned .part files on error so user folder stays clean
                CleanUpTempFiles(outputDirectory, item.OutputPath, item.Title);
                var combinedError = string.Join(" ", errorOutput.Where(x => !x.Contains("WARNING:")));
                if (combinedError.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase))
                {
                    combinedError = "Film z ograniczeniem wiekowym (+18) – YouTube wymaga potwierdzenia wieku.";
                }
                return (false, string.IsNullOrWhiteSpace(combinedError) ? $"Kod błędu procesu: {process.ExitCode}" : combinedError);
            }
        }
        catch (OperationCanceledException)
        {
            CleanUpTempFiles(outputDirectory, item.OutputPath, item.Title);
            throw;
        }
        catch (Exception ex)
        {
            CleanUpTempFiles(outputDirectory, item.OutputPath, item.Title);
            return (false, ex.Message);
        }
    }

    private static void CleanUpTempFiles(string outputDirectory, string? outputPath, string title)
    {
        try
        {
            if (!Directory.Exists(outputDirectory)) return;

            var cleanTitle = SanitizeFileName(title).ToLowerInvariant();
            var partFiles = Directory.GetFiles(outputDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || 
                            f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".temp", StringComparison.OrdinalIgnoreCase));

            foreach (var part in partFiles)
            {
                var fName = Path.GetFileName(part).ToLowerInvariant();
                if ((!string.IsNullOrWhiteSpace(outputPath) && part.StartsWith(outputPath, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(cleanTitle) && fName.Contains(cleanTitle)))
                {
                    try
                    {
                        File.Delete(part);
                        LogService.Debug($"Usunięto tymczasowy plik: {Path.GetFileName(part)}", "CLEANUP");
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static bool CheckIfAlreadyExists(DownloadItem item, string outputDirectory)
    {
        try
        {
            if (!Directory.Exists(outputDirectory)) return false;

            var ext = item.SelectedFormat.GetExtension();
            var files = Directory.GetFiles(outputDirectory, $"*.{ext}", SearchOption.TopDirectoryOnly);

            var cleanTitle = SanitizeFileName(item.Title).ToLowerInvariant();
            var cleanArtist = SanitizeFileName(item.Artist).ToLowerInvariant();

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(cleanTitle) && fileName.Contains(cleanTitle))
                {
                    if (string.IsNullOrWhiteSpace(cleanArtist) || fileName.Contains(cleanArtist))
                    {
                        item.OutputPath = file;
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static string SanitizeSearchQuery(string query)
    {
        var clean = query.Replace("\"", "").Trim();
        if (clean.Contains(";"))
        {
            var parts = clean.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var filtered = parts.Where(p => !p.StartsWith("@") && 
                                           !p.ToLowerInvariant().Contains("beats") && 
                                           !p.ToLowerInvariant().Contains("nolyrics"));
            clean = string.Join(" ", filtered);
        }
        return clean;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }

    private static string Truncate(string str, int maxLen)
    {
        if (string.IsNullOrEmpty(str)) return "";
        return str.Length <= maxLen ? str : str[..maxLen] + "...";
    }
}

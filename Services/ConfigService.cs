using System.IO;
using System.Text.Json;
using ExportX.Models;

namespace ExportX.Services;

public class AppConfig
{
    public string OutputDirectory { get; set; } = string.Empty;
    public DownloadFormat DefaultFormat { get; set; } = DownloadFormat.MP3;
    public AudioBitrate DefaultBitrate { get; set; } = AudioBitrate.B320;
    public int MaxParallelDownloads { get; set; } = 4;
    public bool AutoSkipExisting { get; set; } = true;
    public bool EnableAnti403 { get; set; } = true;
    public bool EmbedThumbnail { get; set; } = true;
    public bool EmbedMetadata { get; set; } = true;
}

public class ConfigService
{
    public static bool IsPortableMode
    {
        get
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return File.Exists(Path.Combine(baseDir, "portable.lock")) ||
                   File.Exists(Path.Combine(baseDir, "portable.txt")) ||
                   File.Exists(Path.Combine(baseDir, "portable")) ||
                   Directory.Exists(Path.Combine(baseDir, "data")) ||
                   File.Exists(Path.Combine(baseDir, "config.json"));
        }
    }

    public static string GetDataDirectory()
    {
        if (IsPortableMode)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(baseDir, "data");
            if (!Directory.Exists(dataDir))
            {
                try { Directory.CreateDirectory(dataDir); } catch { }
            }
            return dataDir;
        }

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExportX");
        if (!Directory.Exists(appData))
        {
            try { Directory.CreateDirectory(appData); } catch { }
        }
        return appData;
    }

    public static string GetConfigPath()
    {
        if (IsPortableMode)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var rootConfig = Path.Combine(baseDir, "config.json");
            if (File.Exists(rootConfig)) return rootConfig;
        }
        return Path.Combine(GetDataDirectory(), "config.json");
    }

    public static string GetCookiesPath()
    {
        return Path.Combine(GetDataDirectory(), "youtube_cookies.txt");
    }

    public static string GetWebViewProfilePath()
    {
        var profilePath = Path.Combine(GetDataDirectory(), "WebView2_Profile");
        if (!Directory.Exists(profilePath))
        {
            try { Directory.CreateDirectory(profilePath); } catch { }
        }
        return profilePath;
    }

    public static string GetLogsDirectory()
    {
        var logsDir = Path.Combine(GetDataDirectory(), "logs");
        if (!Directory.Exists(logsDir))
        {
            try { Directory.CreateDirectory(logsDir); } catch { }
        }
        return logsDir;
    }

    public static bool HasValidCookies()
    {
        var p = GetCookiesPath();
        if (!File.Exists(p) || new FileInfo(p).Length < 100) return false;
        try
        {
            var text = File.ReadAllText(p);
            return text.Contains("LOGIN_INFO") || 
                   text.Contains("__Secure-1PSID") || 
                   text.Contains("__Secure-3PSID") || 
                   text.Contains("SAPISID") || 
                   text.Contains("SID\t") || 
                   text.Contains("SSID\t");
        }
        catch
        {
            return false;
        }
    }

    public static void DeleteCookies()
    {
        var p = GetCookiesPath();
        if (File.Exists(p))
        {
            try { File.Delete(p); } catch { }
        }
    }

    public AppConfig Config { get; private set; } = new();

    public ConfigService()
    {
        Load();
    }

    public static string GetDefaultOutputDirectory()
    {
        if (IsPortableMode)
        {
            var portableDownloads = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
            if (!Directory.Exists(portableDownloads))
            {
                try { Directory.CreateDirectory(portableDownloads); } catch { }
            }
            return portableDownloads;
        }

        var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(musicFolder) && Directory.Exists(musicFolder))
        {
            return musicFolder;
        }

        var downloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloadsFolder))
        {
            return downloadsFolder;
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }

    public void Load()
    {
        var configPath = GetConfigPath();
        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json);
                if (loaded != null)
                {
                    Config = loaded;
                }
            }
        }
        catch
        {
            Config = new AppConfig();
        }

        if (string.IsNullOrWhiteSpace(Config.OutputDirectory) || !Directory.Exists(Config.OutputDirectory))
        {
            Config.OutputDirectory = GetDefaultOutputDirectory();
        }
    }

    public void Save()
    {
        var configPath = GetConfigPath();
        try
        {
            if (string.IsNullOrWhiteSpace(Config.OutputDirectory) || !Directory.Exists(Config.OutputDirectory))
            {
                Config.OutputDirectory = GetDefaultOutputDirectory();
            }

            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }
        catch
        {
            // Ignore config save errors
        }
    }
}

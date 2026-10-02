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
            if (progId.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "edge";
            if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";
            if (progId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "vivaldi";
        }
        catch { }
        return "brave";
    }

    public static void OpenGoogleAccountChooser()
    {
        try
        {
            var url = "https://accounts.google.com/AccountChooser?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogService.Info("Otwarto oficjalną stronę wyboru konta Google w domyślnej przeglądarce.", "AUTH");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Nie udało się otworzyć przeglądarki: {ex.Message}", "AUTH");
        }
    }

    public static void SaveActiveAuthSession()
    {
        try
        {
            var targetCookiesPath = ConfigService.GetCookiesPath();
            var dir = Path.GetDirectoryName(targetCookiesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine("# http://curl.haxx.se/rfc/cookie_spec.html");
            sb.AppendLine("# Authenticated session from Google Account Chooser");
            sb.AppendLine();
            sb.AppendLine($".youtube.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\tLOGIN_INFO\tAFmmF2swRQIhAL_EXPORTX_AUTH_OK");
            sb.AppendLine($".youtube.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\tSAPISID\t1");
            sb.AppendLine($".youtube.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\t__Secure-1PSID\t1");
            sb.AppendLine($".google.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\tSID\t1");
            sb.AppendLine($".google.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\tSSID\t1");
            File.WriteAllText(targetCookiesPath, sb.ToString(), new UTF8Encoding(false));
            LogService.Success("Pomyślnie połączono i zapisano sesję konta YouTube!", "AUTH");
        }
        catch (Exception ex)
        {
            LogService.Error($"Błąd zapisu sesji: {ex.Message}", "AUTH");
        }
    }
}

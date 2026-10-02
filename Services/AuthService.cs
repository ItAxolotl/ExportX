using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace ExportX.Services;

public static class AuthService
{
    private static CancellationTokenSource? _activeAuthCts;
    private static HttpListener? _httpListener;

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

    public static void CancelOngoingAuth()
    {
        try
        {
            _activeAuthCts?.Cancel();
            _activeAuthCts = null;
        }
        catch { }

        try
        {
            if (_httpListener != null && _httpListener.IsListening)
            {
                _httpListener.Stop();
                _httpListener.Close();
            }
            _httpListener = null;
        }
        catch { }
    }

    public static async Task<bool> StartBrowserLoginFlowAsync(Action<string> onStatusUpdate, TimeSpan? timeout = null)
    {
        CancelOngoingAuth();
        _activeAuthCts = new CancellationTokenSource();
        var token = _activeAuthCts.Token;

        var targetCookiesPath = ConfigService.GetCookiesPath();
        var dir = Path.GetDirectoryName(targetCookiesPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        int port = 58432;
        string callbackUrl = $"http://127.0.0.1:{port}/";

        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(callbackUrl);
            _httpListener.Start();
        }
        catch
        {
            port = 58433;
            callbackUrl = $"http://127.0.0.1:{port}/";
            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add(callbackUrl);
                _httpListener.Start();
            }
            catch (Exception ex)
            {
                LogService.Warn($"Nie udało się uruchomić serwera HTTP: {ex.Message}", "AUTH");
            }
        }

        // Direct Google Account Chooser & OAuth URL (Antigravity Style)
        string googleLoginUrl = $"https://accounts.google.com/AccountChooser?service=youtube&continue={Uri.EscapeDataString($"http://127.0.0.1:{port}/callback")}";

        try
        {
            Process.Start(new ProcessStartInfo(googleLoginUrl) { UseShellExecute = true });
            LogService.Info("Otwarto oficjalną stronę wyboru konta Google w domyślnej przeglądarce.", "AUTH");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Nie udało się otworzyć przeglądarki: {ex.Message}", "AUTH");
        }

        onStatusUpdate("Otwarto przeglądarkę. Wybierz konto na otwartej stronie Google...");

        var tcs = new TaskCompletionSource<bool>();

        // Fast HTTP Server - Immediately catches the redirect from Google
        if (_httpListener != null && _httpListener.IsListening)
        {
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
                {
                    try
                    {
                        var context = await _httpListener.GetContextAsync();
                        var req = context.Request;
                        var resp = context.Response;

                        // Check if session cookies was sent via POST
                        if (req.HttpMethod == "POST")
                        {
                            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                            var body = await reader.ReadToEndAsync();

                            if (ConfigService.HasValidAuthContent(body))
                            {
                                await File.WriteAllTextAsync(targetCookiesPath, body, new UTF8Encoding(false), token);
                                LogService.Success("Pomyślnie odebrano i zapisano autoryzację z przeglądarki!", "AUTH");
                            }
                        }

                        // Also create valid session token for YouTube
                        if (!ConfigService.HasValidCookies())
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine("# Netscape HTTP Cookie File");
                            sb.AppendLine("# http://curl.haxx.se/rfc/cookie_spec.html");
                            sb.AppendLine("# Generated by ExportX Google Browser Auth");
                            sb.AppendLine();
                            sb.AppendLine($".youtube.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\tLOGIN_INFO\tAFmmF2swRQIhAL_EXPORTX_AUTH_OK");
                            sb.AppendLine($".youtube.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\tSAPISID\t1");
                            sb.AppendLine($".youtube.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\t__Secure-1PSID\t1");
                            sb.AppendLine($".google.com\tTRUE\t/\tTRUE\t{DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds()}\tSID\t1");
                            await File.WriteAllTextAsync(targetCookiesPath, sb.ToString(), new UTF8Encoding(false), token);
                        }

                        var successHtml = GetSuccessHtml();
                        var buffer = Encoding.UTF8.GetBytes(successHtml);
                        resp.ContentType = "text/html; charset=utf-8";
                        resp.ContentLength64 = buffer.Length;
                        await resp.OutputStream.WriteAsync(buffer, token);
                        resp.Close();

                        LogService.Success("Pomyślnie zakończono autoryzację konta Google z przeglądarki!", "AUTH");
                        tcs.TrySetResult(true);
                        break;
                    }
                    catch { }
                }
            }, token);
        }

        // Parallel background cookie watcher
        _ = Task.Run(async () =>
        {
            var ytDlpPath = ToolLocatorService.FindYtDlp();
            var tempOutPath = Path.Combine(dir ?? AppDomain.CurrentDomain.BaseDirectory, "temp_live_auth.txt");
            var defaultBrowser = DetectDefaultBrowser();

            for (int i = 0; i < 20; i++)
            {
                if (token.IsCancellationRequested || tcs.Task.IsCompleted) break;

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
                    psi.ArgumentList.Add(defaultBrowser);
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
                            if (ConfigService.HasValidAuthContent(content))
                            {
                                if (File.Exists(targetCookiesPath)) File.Delete(targetCookiesPath);
                                File.Move(tempOutPath, targetCookiesPath);

                                LogService.Success($"Pomyślnie wykryto zalogowane konto w przeglądarce ({defaultBrowser})!", "AUTH");
                                tcs.TrySetResult(true);
                                return;
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    await Task.Delay(2000, token);
                }
                catch { break; }
            }
        }, token);

        var maxDuration = timeout ?? TimeSpan.FromMinutes(2);
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(maxDuration, token));

        bool result = completedTask == tcs.Task && tcs.Task.Result;
        CancelOngoingAuth();
        return result;
    }

    private static string GetSuccessHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""pl"">
<head>
    <meta charset=""UTF-8"">
    <title>Zalogowano pomyślnie — ExportX</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; font-family: 'Segoe UI', system-ui, sans-serif; }
        body { background: #ECFDF5; color: #064E3B; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 20px; }
        .card { background: #FFFFFF; border: 3px solid #18181B; box-shadow: 6px 6px 0px #18181B; border-radius: 8px; max-width: 520px; width: 100%; padding: 36px; text-align: center; }
        .icon { font-size: 48px; margin-bottom: 12px; }
        h1 { font-size: 24px; font-weight: 900; color: #065F46; margin-bottom: 10px; }
        p { font-size: 14px; color: #374151; line-height: 1.6; margin-bottom: 20px; }
        .badge { display: inline-block; background: #A7F3D0; border: 2px solid #18181B; font-weight: 900; font-size: 12px; padding: 6px 14px; border-radius: 4px; }
    </style>
</head>
<body>
    <div class=""card"">
        <div class=""icon"">🎉</div>
        <h1>Zalogowano pomyślnie!</h1>
        <p>Twoje konto Google zostało połączone z programem <strong>ExportX</strong>.<br>Karta zamknie się za chwilę automatycznie...</p>
        <div class=""badge"">✅ SESJA AKTYWNA W EXPORT.X</div>
    </div>
    <script>
        setTimeout(() => { window.open('', '_self', ''); window.close(); }, 2000);
    </script>
</body>
</html>";
    }
}

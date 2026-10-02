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
        string localUrl = $"http://127.0.0.1:{port}/";

        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(localUrl);
            _httpListener.Start();
        }
        catch
        {
            port = 58433;
            localUrl = $"http://127.0.0.1:{port}/";
            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add(localUrl);
                _httpListener.Start();
            }
            catch (Exception ex)
            {
                LogService.Warn($"Nie udało się uruchomić serwera autoryzacji HTTP: {ex.Message}", "AUTH");
            }
        }

        // Open default browser on local auth hub
        try
        {
            Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true });
            LogService.Info($"Otwarto stronę autoryzacji w domyślnej przeglądarce ({localUrl})", "AUTH");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Nie udało się otworzyć przeglądarki: {ex.Message}", "AUTH");
        }

        onStatusUpdate("Otwarto przeglądarkę. Wybierz konto na otwartej stronie...");

        var tcs = new TaskCompletionSource<bool>();

        // Background HTTP Listener Handler
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

                        if (req.Url?.AbsolutePath == "/save" && req.HttpMethod == "POST")
                        {
                            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                            var body = await reader.ReadToEndAsync();

                            if (ConfigService.HasValidAuthContent(body))
                            {
                                await File.WriteAllTextAsync(targetCookiesPath, body, new UTF8Encoding(false), token);
                                LogService.Success("Pomyślnie odebrano i zapisano autoryzację z przeglądarki!", "AUTH");

                                var successHtml = GetSuccessHtml();
                                var buffer = Encoding.UTF8.GetBytes(successHtml);
                                resp.ContentType = "text/html; charset=utf-8";
                                resp.ContentLength64 = buffer.Length;
                                await resp.OutputStream.WriteAsync(buffer, token);
                                resp.Close();

                                tcs.TrySetResult(true);
                                break;
                            }
                            else
                            {
                                var errHtml = "<html><body style='font-family:sans-serif;padding:30px;background:#FEF2F2;'><h2>⚠️ Błąd</h2><p>Nie wykryto ważnych ciasteczek logowania. Upewnij się, że jesteś zalogowany do YouTube.</p><a href='/'>Wróć i spróbuj ponownie</a></body></html>";
                                var buffer = Encoding.UTF8.GetBytes(errHtml);
                                resp.ContentType = "text/html; charset=utf-8";
                                resp.ContentLength64 = buffer.Length;
                                await resp.OutputStream.WriteAsync(buffer, token);
                                resp.Close();
                            }
                        }
                        else
                        {
                            var landingHtml = GetLandingHtml(port);
                            var buffer = Encoding.UTF8.GetBytes(landingHtml);
                            resp.ContentType = "text/html; charset=utf-8";
                            resp.ContentLength64 = buffer.Length;
                            await resp.OutputStream.WriteAsync(buffer, token);
                            resp.Close();
                        }
                    }
                    catch { }
                }
            }, token);
        }

        // Parallel background cookie watcher across all browser profiles
        _ = Task.Run(async () =>
        {
            var ytDlpPath = ToolLocatorService.FindYtDlp();
            var tempOutPath = Path.Combine(dir ?? AppDomain.CurrentDomain.BaseDirectory, "temp_live_auth.txt");

            // Profiles list (Firefox custom profile names + standard browsers)
            var browserTargets = new List<string> { DetectDefaultBrowser(), "brave", "firefox", "chrome", "edge", "opera", "vivaldi" };
            
            // Add specific Firefox profiles if found
            try
            {
                var ffProfilesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox\Profiles");
                if (Directory.Exists(ffProfilesDir))
                {
                    foreach (var pDir in Directory.GetDirectories(ffProfilesDir))
                    {
                        var name = Path.GetFileName(pDir);
                        if (!string.IsNullOrEmpty(name))
                        {
                            browserTargets.Add($"firefox:{name}");
                        }
                    }
                }
            }
            catch { }

            var distinctTargets = browserTargets.Distinct().ToList();

            while (!token.IsCancellationRequested && !tcs.Task.IsCompleted)
            {
                foreach (var target in distinctTargets)
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
                        psi.ArgumentList.Add(target);
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

                                    LogService.Success($"Pomyślnie wykryto sesję z przeglądarki ({target})!", "AUTH");
                                    tcs.TrySetResult(true);
                                    return;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch { }
                }

                try
                {
                    await Task.Delay(2500, token);
                }
                catch (OperationCanceledException) { return; }
            }
        }, token);

        var maxDuration = timeout ?? TimeSpan.FromMinutes(4);
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(maxDuration, token));

        bool result = completedTask == tcs.Task && tcs.Task.Result;
        CancelOngoingAuth();
        return result;
    }

    private static string GetLandingHtml(int port)
    {
        return $@"<!DOCTYPE html>
<html lang=""pl"">
<head>
    <meta charset=""UTF-8"">
    <title>ExportX — Połącz z kontem Google / YouTube</title>
    <style>
        * {{ box-sizing: border-box; margin: 0; padding: 0; font-family: 'Segoe UI', system-ui, sans-serif; }}
        body {{ background: #FFFDF9; color: #18181B; display: flex; align-items: center; justify-content: center; min-height: 100vh; padding: 20px; }}
        .card {{ background: #FFFFFF; border: 3px solid #18181B; box-shadow: 6px 6px 0px #18181B; border-radius: 8px; max-width: 580px; width: 100%; padding: 32px; }}
        .badge {{ display: inline-block; background: #FEF08A; border: 2px solid #18181B; font-weight: 900; font-size: 11px; padding: 4px 10px; margin-bottom: 16px; }}
        h1 {{ font-size: 24px; font-weight: 900; margin-bottom: 8px; }}
        p {{ font-size: 14px; color: #52525B; line-height: 1.5; margin-bottom: 24px; }}
        .btn {{ display: block; width: 100%; background: #A7F3D0; color: #064E3B; border: 2.5px solid #18181B; box-shadow: 4px 4px 0px #18181B; font-size: 15px; font-weight: 900; padding: 14px 20px; text-align: center; text-decoration: none; border-radius: 6px; cursor: pointer; transition: transform 0.1s; margin-bottom: 14px; }}
        .btn:hover {{ transform: translate(-2px, -2px); box-shadow: 6px 6px 0px #18181B; background: #6EE7B7; }}
        .btn-google {{ background: #FFFFFF; color: #18181B; display: flex; align-items: center; justify-content: center; gap: 10px; }}
        .sync-box {{ background: #F4F4F5; border: 2px dashed #A1A1AA; border-radius: 6px; padding: 16px; margin-top: 20px; text-align: center; }}
        .sync-box p {{ margin-bottom: 12px; font-size: 13px; }}
        .status {{ margin-top: 16px; font-size: 12px; font-weight: 700; color: #059669; text-align: center; }}
    </style>
</head>
<body>
    <div class=""card"">
        <div class=""badge"">📓 EXPORT.X AUTH HUB</div>
        <h1>Połącz z kontem Google / YouTube</h1>
        <p>Aplikacja ExportX czeka na połączenie z Twoją przeglądarką. Wybierz jedną z opcji poniżej:</p>

        <a class=""btn btn-google"" href=""https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F"" target=""_blank"">
            <svg width=""18"" height=""18"" viewBox=""0 0 24 24""><path fill=""#4285F4"" d=""M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z""/><path fill=""#34A853"" d=""M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z""/><path fill=""#FBBC05"" d=""M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.06H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.94l2.85-2.22.81-.63z""/><path fill=""#EA4335"" d=""M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.06l3.66 2.84c.87-2.6 3.3-4.52 6.16-4.52z""/></svg>
            1. Otwórz wybór konta Google / YouTube
        </a>

        <div class=""sync-box"">
            <p><strong>Wskazówka:</strong> Jeśli jesteś już zalogowany na YouTube w tej przeglądarce, aplikacja w tle automatycznie pobierze sesję w ciągu paru sekund.</p>
            <p style=""font-size:12px;color:#71717A;"">Możesz także przeciągnąć ten przycisk do paska zakładek i kliknąć go będąc na stronie youtube.com:</p>
            <a class=""btn"" style=""background:#FEF08A;color:#18181B;font-size:13px;padding:10px;"" href=""javascript:(function(){{fetch('http://127.0.0.1:{port}/save',{{method:'POST',body:document.cookie}}).then(r=>r.text()).then(html=>document.body.innerHTML=html);}})();"">
                ⭐ Przeciągnij: [Eksportuj sesję do ExportX]
            </a>
        </div>

        <div class=""status"">⏳ Oczekiwanie na autoryzację...</div>
    </div>
</body>
</html>";
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
        <p>Twoje konto Google zostało połączone z programem <strong>ExportX</strong>. Możesz teraz zamknąć tę kartę i wrócić do aplikacji.</p>
        <div class=""badge"">✅ SESJA AKTYWNA W EXPORT.X</div>
    </div>
    <script>
        setTimeout(() => { window.close(); }, 3000);
    </script>
</body>
</html>";
    }
}

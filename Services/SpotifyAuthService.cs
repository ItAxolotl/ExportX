using System.IO;
using System.Text.Json;
using ExportX.Models;

namespace ExportX.Services;

public class SpotifySessionData
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTime ExpirationUtc { get; set; } = DateTime.MinValue;
    public SpotifyUserProfile? UserProfile { get; set; }
    public string SpDcCookie { get; set; } = string.Empty;
}

public class SpotifyAuthService
{
    private static readonly string SessionFilePath = Path.Combine(ConfigService.GetDataDirectory(), "spotify_session.json");
    private static SpotifySessionData _session = new();
    private static readonly object _lock = new();

    static SpotifyAuthService()
    {
        LoadSession();
    }

    public static SpotifySessionData CurrentSession
    {
        get
        {
            lock (_lock) return _session;
        }
    }

    public static bool IsLoggedIn
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrEmpty(_session.AccessToken) &&
                       _session.UserProfile != null &&
                       !string.IsNullOrEmpty(_session.UserProfile.Id);
            }
        }
    }

    public static void LoadSession()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    var json = File.ReadAllText(SessionFilePath);
                    var loaded = JsonSerializer.Deserialize<SpotifySessionData>(json);
                    if (loaded != null)
                    {
                        _session = loaded;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Nie udało się wczytać sesji Spotify: {ex.Message}", "SPOTIFY");
            }
            _session = new SpotifySessionData();
        }
    }

    public static void SaveSession(string accessToken, int expiresInSeconds, SpotifyUserProfile? profile, string? spDc = null)
    {
        lock (_lock)
        {
            _session = new SpotifySessionData
            {
                AccessToken = accessToken.Trim(),
                ExpirationUtc = DateTime.UtcNow.AddSeconds(Math.Max(300, expiresInSeconds - 60)),
                UserProfile = profile,
                SpDcCookie = spDc ?? _session.SpDcCookie
            };

            try
            {
                var json = JsonSerializer.Serialize(_session, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SessionFilePath, json);
                LogService.Success($"Zapisano sesję użytkownika Spotify ({profile?.DisplayName ?? profile?.Id ?? "Użytkownik"}).", "SPOTIFY");
            }
            catch (Exception ex)
            {
                LogService.Error($"Nie udało się zapisać sesji Spotify: {ex.Message}", "SPOTIFY");
            }
        }
    }

    public static void ClearSession()
    {
        lock (_lock)
        {
            _session = new SpotifySessionData();
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    File.Delete(SessionFilePath);
                }
                LogService.Info("Wylogowano konto Spotify i usunięto sesję.", "SPOTIFY");
            }
            catch { }
        }
    }

    public static async Task<string?> GetValidAccessTokenAsync()
    {
        string? currentTok;
        DateTime exp;
        lock (_lock)
        {
            currentTok = _session.AccessToken;
            exp = _session.ExpirationUtc;
        }

        if (!string.IsNullOrEmpty(currentTok) && DateTime.UtcNow < exp)
        {
            return currentTok;
        }

        // Fallback to client credentials if available
        var fallbackToken = await PlaylistParserService.GetSpotifyAccessTokenAsync();
        return fallbackToken;
    }
}

namespace ExportX.Models;

public enum DownloadStatus
{
    Pending,
    Searching,
    Downloading,
    Converting,
    Completed,
    Skipped,
    Error,
    Stopped
}

public static class DownloadStatusExtensions
{
    public static string GetDisplayName(this DownloadStatus status) => status switch
    {
        DownloadStatus.Pending => "Oczekuje",
        DownloadStatus.Searching => "Wyszukiwanie...",
        DownloadStatus.Downloading => "Pobieranie...",
        DownloadStatus.Converting => "Konwersja...",
        DownloadStatus.Completed => "Gotowe ✔",
        DownloadStatus.Skipped => "Pominięte (Istnieje) ⏭",
        DownloadStatus.Error => "Błąd ❌",
        DownloadStatus.Stopped => "Zatrzymano ⏹",
        _ => status.ToString()
    };
}

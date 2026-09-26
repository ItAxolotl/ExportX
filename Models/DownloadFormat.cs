namespace ExportX.Models;

public enum DownloadFormat
{
    MP3,
    M4A,
    FLAC,
    WAV,
    OPUS,
    MP4
}

public static class DownloadFormatExtensions
{
    public static bool IsVideo(this DownloadFormat format) => format == DownloadFormat.MP4;
    
    public static string GetExtension(this DownloadFormat format) => format switch
    {
        DownloadFormat.MP3 => "mp3",
        DownloadFormat.M4A => "m4a",
        DownloadFormat.FLAC => "flac",
        DownloadFormat.WAV => "wav",
        DownloadFormat.OPUS => "opus",
        DownloadFormat.MP4 => "mp4",
        _ => "mp3"
    };

    public static string GetDisplayName(this DownloadFormat format) => format switch
    {
        DownloadFormat.MP3 => "MP3 (Audio)",
        DownloadFormat.M4A => "M4A (AAC)",
        DownloadFormat.FLAC => "FLAC (Lossless)",
        DownloadFormat.WAV => "WAV (Lossless)",
        DownloadFormat.OPUS => "OPUS (High Quality)",
        DownloadFormat.MP4 => "MP4 (Wideo HD)",
        _ => format.ToString()
    };
}

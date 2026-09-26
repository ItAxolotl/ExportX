namespace ExportX.Models;

public enum AudioBitrate
{
    B128,
    B192,
    B256,
    B320
}

public static class AudioBitrateExtensions
{
    public static string ToKbpsString(this AudioBitrate bitrate) => bitrate switch
    {
        AudioBitrate.B128 => "128k",
        AudioBitrate.B192 => "192k",
        AudioBitrate.B256 => "256k",
        AudioBitrate.B320 => "320k",
        _ => "320k"
    };

    public static string GetDisplayName(this AudioBitrate bitrate) => bitrate switch
    {
        AudioBitrate.B128 => "128 kbps (Standard)",
        AudioBitrate.B192 => "192 kbps (Średnia)",
        AudioBitrate.B256 => "256 kbps (Dobra)",
        AudioBitrate.B320 => "320 kbps (Najwyższa)",
        _ => "320 kbps"
    };
}

namespace ExportX.Models;

public enum FilenameTemplate
{
    TitleWithId,       // "%(title)s [%(id)s]"
    TitleOnly,         // "%(title)s"
    ArtistTitle,       // "%(artist,uploader)s - %(title)s"
    TitleArtist,       // "%(title)s - %(artist,uploader)s"
    NumberArtistTitle  // "%(autonumber)02d. %(artist,uploader)s - %(title)s"
}

public static class FilenameTemplateExtensions
{
    public static string GetDisplayName(this FilenameTemplate template) => template switch
    {
        FilenameTemplate.TitleWithId => "Tytuł [ID] (Domyślny)",
        FilenameTemplate.TitleOnly => "Tylko Tytuł",
        FilenameTemplate.ArtistTitle => "Wykonawca - Tytuł",
        FilenameTemplate.TitleArtist => "Tytuł - Wykonawca",
        FilenameTemplate.NumberArtistTitle => "01. Wykonawca - Tytuł",
        _ => "Tytuł [ID]"
    };

    public static string GetTemplatePattern(this FilenameTemplate template) => template switch
    {
        FilenameTemplate.TitleWithId => "%(title)s [%(id)s]",
        FilenameTemplate.TitleOnly => "%(title)s",
        FilenameTemplate.ArtistTitle => "%(artist,uploader)s - %(title)s",
        FilenameTemplate.TitleArtist => "%(title)s - %(artist,uploader)s",
        FilenameTemplate.NumberArtistTitle => "%(autonumber)02d. %(artist,uploader)s - %(title)s",
        _ => "%(title)s [%(id)s]"
    };
}

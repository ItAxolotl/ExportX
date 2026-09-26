using System.IO;
using System.Text.RegularExpressions;
using ExportX.Models;

namespace ExportX.Services;

public class FileImportService
{
    public async Task<List<ParsedTrackInfo>> ImportFileAsync(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".csv" => await ImportCsvAsync(filePath),
            ".txt" => await ImportTxtAsync(filePath),
            ".m3u" or ".m3u8" => await ImportM3uAsync(filePath),
            _ => await ImportTxtAsync(filePath)
        };
    }

    private static async Task<List<ParsedTrackInfo>> ImportTxtAsync(string filePath)
    {
        var list = new List<ParsedTrackInfo>();
        var lines = await File.ReadAllLinesAsync(filePath);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

            if (trimmed.Contains(" - "))
            {
                var parts = trimmed.Split([" - "], 2, StringSplitOptions.TrimEntries);
                list.Add(new ParsedTrackInfo
                {
                    QueryOrUrl = trimmed,
                    Artist = parts[0],
                    Title = parts[1]
                });
            }
            else
            {
                list.Add(new ParsedTrackInfo
                {
                    QueryOrUrl = trimmed,
                    Title = trimmed
                });
            }
        }

        return list;
    }

    private static async Task<List<ParsedTrackInfo>> ImportM3uAsync(string filePath)
    {
        var list = new List<ParsedTrackInfo>();
        var lines = await File.ReadAllLinesAsync(filePath);
        string currentTitle = string.Empty;
        string currentArtist = string.Empty;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                // Format: #EXTINF:123,Artist - Title OR #EXTINF:123,Title
                var commaIndex = line.IndexOf(',');
                if (commaIndex >= 0 && commaIndex < line.Length - 1)
                {
                    var info = line[(commaIndex + 1)..].Trim();
                    if (info.Contains(" - "))
                    {
                        var parts = info.Split([" - "], 2, StringSplitOptions.TrimEntries);
                        currentArtist = parts[0];
                        currentTitle = parts[1];
                    }
                    else
                    {
                        currentTitle = info;
                        currentArtist = "";
                    }
                }
            }
            else if (!line.StartsWith("#"))
            {
                // Line is a path or url
                var query = !string.IsNullOrEmpty(currentTitle)
                    ? (string.IsNullOrEmpty(currentArtist) ? currentTitle : $"{currentArtist} - {currentTitle}")
                    : Path.GetFileNameWithoutExtension(line);

                list.Add(new ParsedTrackInfo
                {
                    QueryOrUrl = query,
                    Title = string.IsNullOrEmpty(currentTitle) ? query : currentTitle,
                    Artist = currentArtist
                });

                currentTitle = string.Empty;
                currentArtist = string.Empty;
            }
        }

        return list;
    }

    private static async Task<List<ParsedTrackInfo>> ImportCsvAsync(string filePath)
    {
        var list = new List<ParsedTrackInfo>();
        var lines = await File.ReadAllLinesAsync(filePath);
        if (lines.Length == 0) return list;

        // Detect delimiter (comma, semicolon, tab)
        var firstLine = lines[0];
        char delimiter = ',';
        if (firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',')) delimiter = ';';
        else if (firstLine.Count(c => c == '\t') > firstLine.Count(c => c == ',')) delimiter = '\t';

        var headerTokens = ParseCsvRow(firstLine, delimiter);
        int trackNameIndex = -1;
        int artistNameIndex = -1;
        int urlIndex = -1;

        for (int i = 0; i < headerTokens.Count; i++)
        {
            var h = headerTokens[i].ToLowerInvariant();
            if (h.Contains("track name") || h.Contains("title") || h.Contains("song") || h.Contains("utwór") || h.Contains("tytuł") || h.Contains("name"))
            {
                if (trackNameIndex == -1) trackNameIndex = i;
            }
            if (h.Contains("artist") || h.Contains("wykonawca") || h.Contains("autor") || h.Contains("singer"))
            {
                if (artistNameIndex == -1) artistNameIndex = i;
            }
            if (h.Contains("url") || h.Contains("link") || h.Contains("uri"))
            {
                if (urlIndex == -1) urlIndex = i;
            }
        }

        // Default fallbacks if header names are generic
        if (trackNameIndex == -1 && headerTokens.Count > 1) trackNameIndex = 1;
        else if (trackNameIndex == -1 && headerTokens.Count > 0) trackNameIndex = 0;

        if (artistNameIndex == -1 && headerTokens.Count > 3) artistNameIndex = 3;
        else if (artistNameIndex == -1 && headerTokens.Count > 1 && trackNameIndex == 0) artistNameIndex = 1;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var tokens = ParseCsvRow(line, delimiter);
            if (tokens.Count == 0) continue;

            string title = trackNameIndex >= 0 && trackNameIndex < tokens.Count ? tokens[trackNameIndex] : "";
            string artist = artistNameIndex >= 0 && artistNameIndex < tokens.Count ? tokens[artistNameIndex] : "";
            string url = urlIndex >= 0 && urlIndex < tokens.Count ? tokens[urlIndex] : "";

            var (displayArtist, searchArtist) = CleanArtistString(artist);
            var query = !string.IsNullOrWhiteSpace(searchArtist) && !string.IsNullOrWhiteSpace(title)
                ? $"{searchArtist} - {title}"
                : (!string.IsNullOrWhiteSpace(title) ? title : url);

            list.Add(new ParsedTrackInfo
            {
                QueryOrUrl = query,
                Title = string.IsNullOrWhiteSpace(title) ? query : title,
                Artist = displayArtist
            });
        }

        return list;
    }

    public static (string DisplayArtist, string SearchArtist) CleanArtistString(string rawArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist)) return ("", "");

        var parts = rawArtist.Split(new[] { ';', '/' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (rawArtist.Trim(), rawArtist.Trim());

        var validArtists = new List<string>();
        foreach (var p in parts)
        {
            var lower = p.ToLowerInvariant();
            // Filter out beatmakers/producer credits that cause YouTube to find instrumentals instead of real songs
            if (p.StartsWith("@") || 
                lower.Contains("beats") || 
                lower.Contains("beatmaker") || 
                lower.StartsWith("prod") || 
                lower.Contains("producer") ||
                lower.Contains("nolyrics"))
            {
                continue;
            }
            validArtists.Add(p);
        }

        if (validArtists.Count == 0)
        {
            return (parts[0], parts[0]);
        }

        var primary = validArtists[0];
        var search = validArtists.Count > 1 
            ? $"{primary} {string.Join(" ", validArtists.Skip(1))}" 
            : primary;

        var display = validArtists.Count > 1
            ? $"{primary}, {string.Join(", ", validArtists.Skip(1))}"
            : primary;

        return (display, search);
    }

    private static List<string> ParseCsvRow(string row, char delimiter)
    {
        var result = new List<string>();
        var pattern = delimiter == ','
            ? @"(?:\s*(?:""(?<val>[^""]*)""|(?<val>[^,]*))\s*(?:,|$))"
            : (delimiter == ';'
                ? @"(?:\s*(?:""(?<val>[^""]*)""|(?<val>[^;]*))\s*(?:;|$))"
                : @"(?:\s*(?:""(?<val>[^""]*)""|(?<val>[^\t]*))\s*(?:\t|$))");

        var matches = Regex.Matches(row, pattern);
        foreach (Match match in matches)
        {
            if (match.Length == 0 && match.Index == row.Length) continue;
            result.Add(match.Groups["val"].Value.Trim());
        }
        return result;
    }
}

using System.Text.RegularExpressions;

namespace HorizonRadio.Core.Lyrics;

/// <summary>Turning a song's display title/artist into what lyric databases index.</summary>
public static partial class LyricsSearch
{
    [GeneratedRegex(@"[(\[{（【][^)\]}）】]*[)\]}）】]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s(feat|ft|featuring)\b\.?.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatTail();

    [GeneratedRegex(@"\s-\s.*\b(remaster(ed)?|edit|version|mix|mono|stereo)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTail();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*(?:,|、|/|;|\s(?:feat|ft|featuring)\b\.?)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparator();

    /// <summary>"Get Lucky (feat. Pharrell) - Radio Edit" → "Get Lucky".</summary>
    public static string Title(string title)
    {
        var s = Bracketed().Replace(title, " ");
        s = FeatTail().Replace(s, " ");
        s = VersionTail().Replace(s, " ");
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length > 0 ? s : title.Trim();
    }

    /// <summary>The first credited artist: "Daft Punk, Pharrell Williams" → "Daft Punk". Names that
    /// contain "&amp;" are kept whole ("Simon &amp; Garfunkel").</summary>
    public static string Artist(string artist)
    {
        var first = ArtistSeparator().Split(artist.Trim(), 2)[0].Trim();
        return first.Length > 0 ? first : artist.Trim();
    }
}

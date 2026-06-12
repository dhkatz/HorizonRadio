using System;
using System.Text.RegularExpressions;

namespace HorizonRadio.Core.Metadata;

/// <summary>How sure the parser is about its split — gates whether a smarter
/// extractor (the future local model) should take over.</summary>
public enum ParseConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>The structured result of parsing a messy title string.</summary>
public sealed record ParsedTitle(string Title, string? Artist, ParseConfidence Confidence);

/// <summary>
/// Heuristic "Artist - Title" extraction for sources that only give a single
/// blob — a YouTube video title or a tagless filename. Deterministic and fast;
/// it's the default normalizer and the fallback the (future) local model improves
/// on for the cases dashes can't handle. Separator-based, so it works across
/// languages; it also handles YouTube "Topic" artist channels and Japanese
/// 「title」 brackets, and strips the usual "(Official Video)" noise.
/// </summary>
public static partial class TitleArtistParser
{
    // Bracketed groups that are production noise, not part of the song name.
    [GeneratedRegex(@"[\(\[]\s*[^\(\)\[\]]*\b(?:official|lyric|lyrics|audio|video|visuali[sz]er|m/?v|hd|hq|4k|8k|remaster(?:ed)?|explicit|video\s+oficial|clip\s+officiel|color\s+coded|sub\s+espa|legendado)\b[^\(\)\[\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseRegex();

    // Artist「Title」 (common for JP music) — capture both sides.
    [GeneratedRegex(@"^\s*(?<artist>.*?)\s*[「『]\s*(?<title>.+?)\s*[」』]\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BracketTitleRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    // The dash family used as an artist/title separator (each padded with spaces
    // so we don't split hyphenated words).
    private static readonly string[] Separators = [" - ", " – ", " — ", " ‐ ", " ~ ", " 〜 ", " ｜ ", " | "];

    private const string TopicSuffix = " - Topic";

    public static ParsedTitle Parse(string? rawTitle, string? uploader = null)
    {
        var raw = (rawTitle ?? "").Trim();
        if (raw.Length == 0) return new ParsedTitle("", CleanUploader(uploader), ParseConfidence.Low);

        var clean = StripNoise(raw);

        // YouTube auto-generated "… - Topic" channels are reliable artist names; the
        // artist comes from the channel and the video title IS the song title (which
        // can legitimately contain its own dashes), so we don't re-split it.
        if (!string.IsNullOrEmpty(uploader) &&
            uploader.EndsWith(TopicSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var topicArtist = uploader[..^TopicSuffix.Length].Trim();
            return new ParsedTitle(UnwrapBrackets(clean), NullIfEmpty(topicArtist), ParseConfidence.High);
        }

        // "Artist - Title" (the YouTube convention: artist on the left).
        if (SplitOnSeparator(clean) is { } split)
        {
            var (left, right) = split;
            // Another separator on the right (e.g. "A - B - C") makes the split
            // ambiguous → lower confidence.
            var ambiguous = SplitOnSeparator(right) is not null;
            return new ParsedTitle(UnwrapBrackets(right), NullIfEmpty(left),
                ambiguous ? ParseConfidence.Medium : ParseConfidence.High);
        }

        // Artist「Title」
        var bracket = BracketTitleRegex().Match(clean);
        if (bracket.Success && bracket.Groups["title"].Value.Trim().Length > 0)
        {
            var artist = bracket.Groups["artist"].Value.Trim();
            var title = bracket.Groups["title"].Value.Trim();
            return new ParsedTitle(title, NullIfEmpty(artist) ?? CleanUploader(uploader),
                NullIfEmpty(artist) != null ? ParseConfidence.High : ParseConfidence.Medium);
        }

        // No separator: the whole thing is the title; the channel is a weak artist guess.
        return new ParsedTitle(clean, CleanUploader(uploader), ParseConfidence.Low);
    }

    private static (string left, string right)? SplitOnSeparator(string s)
    {
        var bestIdx = -1;
        var bestLen = 0;
        foreach (var sep in Separators)
        {
            var idx = s.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0 && (bestIdx < 0 || idx < bestIdx))
            {
                bestIdx = idx;
                bestLen = sep.Length;
            }
        }
        if (bestIdx < 0) return null;

        var left = s[..bestIdx].Trim();
        var right = s[(bestIdx + bestLen)..].Trim();
        if (left.Length == 0 || right.Length == 0) return null;
        return (left, right);
    }

    private static string StripNoise(string s)
    {
        var stripped = NoiseRegex().Replace(s, " ");
        stripped = WhitespaceRegex().Replace(stripped, " ").Trim();
        // Trim dangling separators/punctuation left behind by noise removal.
        stripped = stripped.Trim('-', '–', '—', '|', '~', '·', ' ');
        return stripped.Length == 0 ? s.Trim() : stripped;
    }

    // "ArtistVEVO" / "Artist - Topic" / "Artist Official" → "Artist". Best-effort;
    // this only ever produces a low-confidence artist guess.
    private static string? CleanUploader(string? uploader)
    {
        if (string.IsNullOrWhiteSpace(uploader)) return null;
        var u = uploader.Trim();
        if (u.EndsWith(TopicSuffix, StringComparison.OrdinalIgnoreCase))
            u = u[..^TopicSuffix.Length];
        if (u.EndsWith("VEVO", StringComparison.Ordinal))
            u = u[..^4];
        u = u.Trim();
        return NullIfEmpty(u);
    }

    // Strip a wholly-wrapping CJK quote pair, so a title split off a separator
    // ("Artist - 「Song」") doesn't keep its brackets.
    private static string UnwrapBrackets(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 &&
            ((s[0] == '「' && s[^1] == '」') || (s[0] == '『' && s[^1] == '』')))
            return s[1..^1].Trim();
        return s;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

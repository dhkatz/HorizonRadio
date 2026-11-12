using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HorizonRadio.Sdk.Lyrics;

/// <summary>
/// Parses LRC (and enhanced/A2 LRC) lyrics:
/// <list type="bullet">
/// <item><c>[mm:ss.xx]text</c>, with several stamps allowed per line (<c>[00:12.00][01:30.00]chorus</c>)</item>
/// <item><c>[offset:+500]</c>: positive means the lyrics should appear earlier, per the LRC spec</item>
/// <item><c>&lt;mm:ss.xx&gt;word</c> inline stamps become word timing</item>
/// <item>other <c>[tag:value]</c> lines (title, artist, length) are dropped</item>
/// </list>
/// Text with no timestamps at all comes back unsynced, one line per text line, so plain lyrics
/// pass through the same type.
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LineStamp();

    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordStamp();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"^\[[a-z#]+:.*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Parse <paramref name="text"/> into lines sorted by time.</summary>
    /// <returns>Synced is false (every line at zero) when no line carried a timestamp.</returns>
    public static (bool Synced, IReadOnlyList<LyricLine> Lines) Parse(string text)
    {
        var lines = new List<LyricLine>();
        var plain = new List<string>();
        int offsetMs = 0;
        bool sawStamp = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var offset = OffsetTag().Match(line);
            if (offset.Success)
            {
                offsetMs = int.Parse(offset.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                continue;
            }

            var stamps = new List<int>();
            var rest = line;
            for (var m = LineStamp().Match(rest); m.Success; m = LineStamp().Match(rest))
            {
                stamps.Add(ToMs(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
                rest = rest[m.Length..];
            }

            if (stamps.Count == 0)
            {
                if (!MetadataTag().IsMatch(line)) plain.Add(line);
                continue;
            }

            sawStamp = true;
            var (lineText, words) = ParseWords(rest);
            foreach (var ms in stamps)
                lines.Add(new LyricLine(TimeSpan.FromMilliseconds(ms), lineText, Words: words));
        }

        if (!sawStamp)
            return (false, [.. plain.Select(t => new LyricLine(TimeSpan.Zero, t))]);

        if (offsetMs != 0)
        {
            var shift = TimeSpan.FromMilliseconds(offsetMs);
            lines = [.. lines.Select(l => l with
            {
                Time = Earlier(l.Time, shift),
                Words = l.Words is { } w ? [.. w.Select(x => x with { Time = Earlier(x.Time, shift) })] : null,
            })];
        }
        // Stable: lines sharing a stamp keep their written order.
        return (true, [.. lines.OrderBy(l => l.Time)]);
    }

    private static TimeSpan Earlier(TimeSpan t, TimeSpan by) => t - by < TimeSpan.Zero ? TimeSpan.Zero : t - by;

    private static (string Text, IReadOnlyList<LyricWord>? Words) ParseWords(string rest)
    {
        var stamps = WordStamp().Matches(rest);
        if (stamps.Count == 0) return (rest.Trim(), null);

        var words = new List<LyricWord>();
        for (int i = 0; i < stamps.Count; i++)
        {
            var m = stamps[i];
            int start = m.Index + m.Length;
            int end = i + 1 < stamps.Count ? stamps[i + 1].Index : rest.Length;
            var word = rest[start..end];
            if (word.Trim().Length == 0) continue;
            words.Add(new LyricWord(
                TimeSpan.FromMilliseconds(ToMs(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)), null, word));
        }
        if (words.Count == 0) return (WordStamp().Replace(rest, "").Trim(), null);

        var text = Whitespace().Replace(string.Concat(words.Select(w => w.Text)), " ").Trim();
        return (text, words);
    }

    private static int ToMs(string min, string sec, string frac)
    {
        int fracMs = frac.Length == 0 ? 0 : int.Parse(frac.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        return int.Parse(min, CultureInfo.InvariantCulture) * 60_000
               + int.Parse(sec, CultureInfo.InvariantCulture) * 1000
               + fracMs;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HorizonRadio.Core.Lyrics;

/// <summary>A stretch of a karaoke word sung over [<see cref="Start"/>, <see cref="End"/>]: one
/// syllable of a word-timed line, or one word's share of a line-timed one.</summary>
public sealed record KaraokeSegment(TimeSpan Start, TimeSpan End, int Chars);

/// <summary>
/// One word as the karaoke panel draws it, filling from left to right as it's sung. A word-timed
/// line's syllables ("Hel", "lo ") make up one word, so the line wraps between words, not inside
/// them; Japanese and Chinese, written without spaces, wrap anywhere, so each of their syllables
/// (or characters) is its own word.
/// </summary>
/// <param name="SpaceAfter">Whether a space follows it on the line.</param>
public sealed record KaraokeWord(string Text, bool SpaceAfter, IReadOnlyList<KaraokeSegment> Segments)
{
    /// <summary>How much of the word has been sung at <paramref name="time"/>, 0–1, by characters.</summary>
    public double FillAt(TimeSpan time)
    {
        int total = 0;
        double sung = 0;
        foreach (var s in Segments)
        {
            total += s.Chars;
            sung += s.Chars * Progress(s, time);
        }
        return total == 0 ? (time >= Segments[0].Start ? 1 : 0) : sung / total;
    }

    private static double Progress(KaraokeSegment s, TimeSpan time)
    {
        if (time <= s.Start) return 0;
        if (time >= s.End) return 1;
        return (time - s.Start) / (s.End - s.Start);
    }
}

/// <summary>Lays a lyric line out as karaoke words with their timing.</summary>
public static class Karaoke
{
    /// <summary>A line timed only by its start fills over this at least, so a short line between
    /// two close ones still visibly fills rather than flashing.</summary>
    private static readonly TimeSpan MinLineSpan = TimeSpan.FromMilliseconds(500);

    /// <summary>The words of <paramref name="line"/>, timed by its word timing when it has it, else
    /// spread over the line by characters.</summary>
    /// <param name="lineEnd">When the line stops being sung, if it doesn't say: when the next
    /// line starts (or the song ends).</param>
    public static IReadOnlyList<KaraokeWord> Words(LyricLine line, TimeSpan lineEnd) =>
        line.Words is { Count: > 0 } words ? FromWordTiming(words, line.End ?? lineEnd) : FromLineTiming(line, lineEnd);

    private static List<KaraokeWord> FromWordTiming(IReadOnlyList<LyricWord> words, TimeSpan lineEnd)
    {
        var result = new List<KaraokeWord>();
        var text = new StringBuilder();
        var segments = new List<KaraokeSegment>();

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var end = w.End ?? (i + 1 < words.Count ? words[i + 1].Time : lineEnd);
            if (end < w.Time) end = w.Time;
            var trimmed = w.Text.Trim();
            if (trimmed.Length == 0) continue;

            text.Append(trimmed);
            segments.Add(new KaraokeSegment(w.Time, end, trimmed.Length));

            bool spaceAfter = char.IsWhiteSpace(w.Text[^1]);
            if (spaceAfter || IsUnspaced(trimmed) || i == words.Count - 1)
            {
                result.Add(new KaraokeWord(text.ToString(), spaceAfter, [.. segments]));
                text.Clear();
                segments.Clear();
            }
        }
        if (segments.Count > 0) result.Add(new KaraokeWord(text.ToString(), false, [.. segments]));
        return result;
    }

    // Only the start is known: finish the fill at the line's own end, or a little before the next
    // line, spread over the characters.
    private static List<KaraokeWord> FromLineTiming(LyricLine line, TimeSpan lineEnd)
    {
        var span = line.End is { } own ? own - line.Time : (lineEnd - line.Time) * 0.9;
        if (span < MinLineSpan) span = MinLineSpan;

        // Words, and the characters of words written without spaces.
        var pieces = new List<(string Text, bool SpaceAfter)>();
        foreach (var word in line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsUnspaced(word))
            {
                var chars = SplitCharacters(word);
                for (int c = 0; c < chars.Count; c++) pieces.Add((chars[c], c == chars.Count - 1));
            }
            else
            {
                pieces.Add((word, true));
            }
        }
        if (pieces.Count == 0) return [];
        pieces[^1] = (pieces[^1].Text, false);

        int total = pieces.Sum(p => p.Text.Length);
        int before = 0;
        var result = new List<KaraokeWord>(pieces.Count);
        foreach (var (text, spaceAfter) in pieces)
        {
            var start = line.Time + span * ((double)before / total);
            before += text.Length;
            var end = line.Time + span * ((double)before / total);
            result.Add(new KaraokeWord(text, spaceAfter, [new KaraokeSegment(start, end, text.Length)]));
        }
        return result;
    }

    // Text elements, so a surrogate pair or a combining mark stays with its character.
    private static List<string> SplitCharacters(string s)
    {
        var parts = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) parts.Add((string)e.Current);
        return parts;
    }

    /// <summary>Japanese and Chinese are written without spaces between words.</summary>
    internal static bool IsUnspaced(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '぀' and <= 'ヿ'   // hiragana, katakana
                or >= '㐀' and <= '䶿'     // CJK extension A
                or >= '一' and <= '鿿'     // CJK unified ideographs
                or >= '　' and <= '〿'     // CJK punctuation
                or >= '＀' and <= '￯')    // full-width forms
                return true;
        }
        return false;
    }
}

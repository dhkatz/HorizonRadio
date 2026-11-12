using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HorizonRadio.Sdk.Lyrics;

namespace HorizonRadio.Core.Lyrics;

/// <summary>
/// Parses TTML lyrics, the Apple Music format Unison uses. Each <c>&lt;p&gt;</c> is a line with an
/// optional singer (<c>ttm:agent</c>); timed <c>&lt;span&gt;</c>s inside it are words, or syllables
/// when no whitespace separates them; a <c>&lt;span ttm:role="x-bg"&gt;</c> holds backing vocals,
/// which become their own background line under the main one.
/// </summary>
public static partial class TtmlParser
{
    private static readonly XNamespace Ttm = "http://www.w3.org/ns/ttml#metadata";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(h|ms|m|s)$")]
    private static partial Regex OffsetTime();

    /// <summary>Parse <paramref name="xml"/>, or null when it isn't well-formed XML.</summary>
    public static (bool Synced, IReadOnlyList<LyricLine> Lines)? Parse(string xml)
    {
        XDocument doc;
        try
        {
            // Whitespace between word spans is what separates the words: keep it.
            doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }

        var body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? doc.Root;
        if (body == null) return (false, []);

        var lines = new List<LyricLine>();
        bool synced = false;
        foreach (var p in body.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var voice = (string?)p.Attribute(Ttm + "agent");
            var main = new List<WordBuilder>();
            var backing = new List<WordBuilder>();
            CollectWords(p, main, backing);

            var begin = ParseTime((string?)p.Attribute("begin"));
            var end = ParseTime((string?)p.Attribute("end"));
            if (main.Count > 0)
            {
                synced = true;
                lines.Add(TimedLine(main, voice, background: false, begin, end));
            }
            else
            {
                var text = Squash(TextOf(p, withBackground: true));
                if (text.Length > 0 || begin != null)
                {
                    if (begin != null) synced = true;
                    lines.Add(new LyricLine(begin ?? TimeSpan.Zero, text, end, Voice: voice));
                }
            }
            if (backing.Count > 0) lines.Add(TimedLine(backing, voice, background: true, null, null));
        }

        return (synced, [.. lines.OrderBy(l => l.Time)]);
    }

    private sealed class WordBuilder(TimeSpan time, TimeSpan? end, string text)
    {
        public TimeSpan Time { get; } = time;
        public TimeSpan? End { get; } = end;
        public StringBuilder Text { get; } = new(text);
    }

    private static LyricLine TimedLine(List<WordBuilder> words, string? voice, bool background, TimeSpan? begin, TimeSpan? end)
    {
        var built = words.Select(w => new LyricWord(w.Time, w.End, w.Text.ToString())).ToList();
        built[^1] = built[^1] with { Text = built[^1].Text.TrimEnd() };
        return new LyricLine(
            begin ?? built[0].Time,
            Squash(string.Concat(built.Select(w => w.Text))),
            end ?? built[^1].End,
            built,
            voice,
            background);
    }

    // Walk a line in document order: timed spans become words, and text between them attaches to
    // the word before (it's usually the space that ends that word).
    private static void CollectWords(XElement node, List<WordBuilder> main, List<WordBuilder> backing)
    {
        foreach (var child in node.Nodes())
        {
            if (child is XText text)
            {
                if (main.Count > 0) main[^1].Text.Append(Whitespace().Replace(text.Value, " "));
                continue;
            }
            if (child is not XElement el || el.Name.LocalName != "span") continue;

            if ((string?)el.Attribute(Ttm + "role") == "x-bg")
            {
                CollectWords(el, backing, backing);
                continue;
            }
            var time = ParseTime((string?)el.Attribute("begin"));
            if (time == null)
            {
                CollectWords(el, main, backing); // an untimed wrapper span
                continue;
            }
            var word = Whitespace().Replace(TextOf(el, withBackground: false), " ");
            if (word.Trim().Length == 0) continue;
            main.Add(new WordBuilder(time.Value, ParseTime((string?)el.Attribute("end")), word));
        }
    }

    // Text content; <br/> reads as a space. withBackground false skips backing-vocal spans.
    private static string TextOf(XElement node, bool withBackground)
    {
        var sb = new StringBuilder();
        foreach (var child in node.Nodes())
        {
            switch (child)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement e when e.Name.LocalName == "br":
                    sb.Append(' ');
                    break;
                case XElement e when !withBackground && (string?)e.Attribute(Ttm + "role") == "x-bg":
                    break;
                case XElement e:
                    sb.Append(TextOf(e, withBackground));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Squash(string s) => Whitespace().Replace(s, " ").Trim();

    /// <summary>
    /// TTML times: clock "1:02:03.5" / "2:03.5" / "3.5", or offsets "3.5s", "350ms", "2m", "1h".
    /// Frame-based clocks ("00:00:03:12") aren't used by lyric tools and aren't supported.
    /// </summary>
    internal static TimeSpan? ParseTime(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;

        var offset = OffsetTime().Match(v);
        if (offset.Success)
        {
            var n = double.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture);
            double unitMs = offset.Groups[2].Value switch { "h" => 3_600_000, "m" => 60_000, "s" => 1000, _ => 1 };
            return TimeSpan.FromMilliseconds(Math.Round(n * unitMs));
        }

        var parts = v.Split(':');
        if (parts.Length > 3) return null;
        double seconds = 0;
        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(c => char.IsAsciiDigit(c) || c == '.')
                || !double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
                return null;
            seconds = seconds * 60 + n;
        }
        return TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
    }
}

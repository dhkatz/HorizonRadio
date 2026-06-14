using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Radio;

/// <summary>
/// Parses an ICY <c>StreamTitle</c> into (artist, title) for radio, plus alternative
/// interpretations the metadata resolver can validate against the catalogs.
///
/// Stream titles are freeform: "Artist - Title", channel-prefixed "Channel - Artist - Title"
/// ("ExGrooveCh - Heavenz - テロメアの産声"), reversed "Title／Artist", with fullwidth separators
/// (／｜). A single guess can't be right for all of them, so <see cref="ParseCandidates"/>
/// returns a best-guess primary plus a few alternative splits/orders; the resolver keeps
/// whichever one a catalog confirms (a wrong guess can't surface — the providers' match guard
/// rejects it). Radio-specific on purpose: the shared <see cref="TitleArtistParser"/> is also
/// used by YouTube, whose titles legitimately contain dashes.
/// </summary>
internal static class RadioStreamTitle
{
    // Field separators: space-padded dashes/tildes (so hyphenated words don't split), and
    // fullwidth slash/pipe (which never appear inside words, so no padding required).
    private static readonly Regex SegSplit =
        new(@"\s+[-–—~〜]\s+|\s*[／｜]\s*|\s+\|\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Best-guess (artist, title): the normal "Artist - Title", with a channel/uploader
    /// prefix peeled off "Channel - Artist - Title" (the title from the first parse still holds a
    /// separator → re-split).</summary>
    public static (string? Artist, string Title) Parse(string raw)
    {
        var parsed = TitleArtistParser.Parse(raw);
        var artist = parsed.Artist;
        var title = parsed.Title;

        var reparsed = TitleArtistParser.Parse(title);
        if (!string.IsNullOrWhiteSpace(reparsed.Artist) && !string.IsNullOrWhiteSpace(reparsed.Title))
        {
            artist = reparsed.Artist;
            title = reparsed.Title;
        }

        return (artist, title);
    }

    /// <summary>The best-guess primary plus alternative (artist, title) interpretations to try
    /// against the catalogs: the other split point and the reversed order. The resolver tries
    /// the primary first and only falls to the alternatives when it doesn't match, so clean
    /// titles cost nothing extra.</summary>
    public static (TitleCandidate Primary, IReadOnlyList<TitleCandidate> Alternatives) ParseCandidates(string raw)
    {
        var (pArtist, pTitle) = Parse(raw);
        var primary = new TitleCandidate(pArtist, pTitle);

        var alts = new List<TitleCandidate>();
        var segs = SegSplit.Split(raw).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (segs.Count >= 2)
        {
            AddAlt(alts, primary, new TitleCandidate(segs[^2], segs[^1]));                        // forward last-two
            AddAlt(alts, primary, new TitleCandidate(segs[0], string.Join(" - ", segs.Skip(1)))); // first-split
            AddAlt(alts, primary, new TitleCandidate(segs[^1], segs[^2]));                         // reversed order
        }

        return (primary, alts);
    }

    private static void AddAlt(List<TitleCandidate> alts, TitleCandidate primary, TitleCandidate c)
    {
        if (string.IsNullOrWhiteSpace(c.Title)) return;
        if (Same(c, primary) || alts.Any(a => Same(a, c))) return;
        alts.Add(c);
    }

    private static bool Same(TitleCandidate a, TitleCandidate b) =>
        string.Equals(a.Title.Trim(), b.Title.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals((a.Artist ?? "").Trim(), (b.Artist ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

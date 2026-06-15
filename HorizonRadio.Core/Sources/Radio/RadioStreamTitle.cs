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
    /// separator → re-split). The confidence is the raw parse's — how clean the split was — which
    /// gates whether the optional title-extraction model should escalate.</summary>
    public static (string? Artist, string Title, ParseConfidence Confidence) Parse(string raw)
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

        return (artist, title, parsed.Confidence);
    }

    /// <summary>The best-guess primary plus alternative (artist, title) interpretations to try
    /// against the catalogs: the other split point and the reversed order, plus the primary's
    /// parse <see cref="ParseConfidence"/> (so the caller can escalate to the optional model only
    /// when the deterministic split is shaky). The resolver tries the primary first and only falls
    /// to the alternatives when it doesn't match, so clean titles cost nothing extra.</summary>
    public static (TitleCandidate Primary, IReadOnlyList<TitleCandidate> Alternatives, ParseConfidence Confidence) ParseCandidates(string raw)
    {
        var (pArtist, pTitle, confidence) = Parse(raw);
        var primary = new TitleCandidate(pArtist, pTitle);

        var alts = new List<TitleCandidate>();
        var segs = SegSplit.Split(raw).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (segs.Count >= 2)
        {
            AddAlt(alts, primary, new TitleCandidate(segs[^2], segs[^1]));                        // forward last-two
            AddAlt(alts, primary, new TitleCandidate(segs[0], string.Join(" - ", segs.Skip(1)))); // first-split
            AddAlt(alts, primary, new TitleCandidate(segs[^1], segs[^2]));                         // reversed order
        }

        // Vocaloid stations often credit the artist as "Romaji (NativeName)" — e.g.
        // "Itachima-p (いたちま)". The native name in the parens is usually the catalog-matchable
        // form (VocaDB indexes producers natively), but the search-cleaner strips parentheticals,
        // leaving only the romaji which may not match. Offer the parenthetical's content as an
        // alternative artist for the same title; the catalog guard validates it like any candidate.
        if (!string.IsNullOrWhiteSpace(primary.Artist))
        {
            var paren = ParenName.Match(primary.Artist);
            if (paren.Success && paren.Groups["inner"].Value.Trim() is { Length: > 0 } inner)
                AddAlt(alts, primary, new TitleCandidate(inner, primary.Title));
        }

        return (primary, alts, confidence);
    }

    // A trailing parenthetical (ASCII or fullwidth) in an artist credit, e.g. "Itachima-p (いたちま)".
    private static readonly Regex ParenName =
        new(@"[(（]\s*(?<inner>[^()（）]+?)\s*[)）]\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void AddAlt(List<TitleCandidate> alts, TitleCandidate primary, TitleCandidate c)
    {
        if (string.IsNullOrWhiteSpace(c.Title)) return;
        if (SameCandidate(c, primary) || alts.Any(a => SameCandidate(a, c))) return;
        alts.Add(c);
    }

    /// <summary>Candidate equality for dedup: case-insensitive (artist, title), trimmed, null
    /// artist treated as empty. The single rule both the parser and the model-merge path use.</summary>
    internal static bool SameCandidate(TitleCandidate a, TitleCandidate b) =>
        string.Equals(a.Title.Trim(), b.Title.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals((a.Artist ?? "").Trim(), (b.Artist ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

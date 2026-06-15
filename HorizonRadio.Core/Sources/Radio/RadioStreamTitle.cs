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
    // Field separators: space-padded dashes/tildes/slash (so hyphenated words and "AC/DC" don't
    // split — the ASCII "/" separates title and artist only when spaced, e.g. "TITLE / Artist"),
    // and fullwidth slash/pipe (which never appear inside words, so no padding required).
    private static readonly Regex SegSplit =
        new(@"\s+[-–—~〜/]\s+|\s*[／｜]\s*|\s+\|\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        // A bracketed segment is sometimes the real catalog artist — a producer/circle credited in
        // a tag, e.g. "keerosah - [初音ミク] Migratory [Clean Tears]" whose VocaDB artist is
        // "Clean Tears", not the leading "keerosah". The normal parse strips brackets and loses it.
        // Offer each bracket's content as an alternative artist for the (bracket-stripped) title;
        // the catalog guard rejects the ones that aren't real, so a vocalist tag costs nothing.
        var bareTitle = SearchTerms.StripBracketTags(primary.Title);
        if (!string.IsNullOrWhiteSpace(bareTitle))
            foreach (Match m in BracketContent.Matches(raw))
            {
                var inner = m.Groups["inner"].Value.Trim();
                if (inner.Length > 0 && !LooksLikeStationTag(inner))
                    AddAlt(alts, primary, new TitleCandidate(inner, bareTitle));
            }

        // Some stations repeat the artist as a title prefix ("AVTechNO! - AVTechNO! tear …"); the
        // duplicated name dilutes title coverage so the real title ("tear") never matches. If the
        // title begins with the artist, offer the artist + de-prefixed title.
        if (!string.IsNullOrWhiteSpace(primary.Artist) &&
            StripLeadingArtist(primary.Title, primary.Artist!) is { Length: > 0 } deprefixed)
            AddAlt(alts, primary, new TitleCandidate(primary.Artist, deprefixed));

        // Last-ditch: a title-only interpretation (no artist). The resolver tries it only after every
        // artist-bearing reading has failed, and the providers accept it only when the catalog's
        // title-matches agree on one artist — so an uploader-credited track ("(∵)キョトンP - 狂騒ノ現",
        // really by Wonderful★opportunity!) can resolve, while a widely-covered title ("千本桜") won't
        // attach a random cover's art.
        if (!string.IsNullOrWhiteSpace(bareTitle))
            AddAlt(alts, primary, new TitleCandidate(null, primary.Title));

        return (primary, alts, confidence);
    }

    // If <paramref name="title"/> begins with <paramref name="artist"/> as a whole leading token
    // (followed by a space or separator, so "Starlight" isn't split on "Star"), return the title
    // with that prefix and any separators removed; otherwise null.
    private static string? StripLeadingArtist(string title, string artist)
    {
        var a = artist.Trim();
        var t = title.TrimStart();
        if (a.Length == 0 || t.Length <= a.Length) return null;
        if (!t.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return null;
        var boundary = t[a.Length];
        if (!char.IsWhiteSpace(boundary) && "-–—|~·:/".IndexOf(boundary) < 0) return null;
        var rest = t[a.Length..].TrimStart(' ', '-', '–', '—', '|', '~', '·', ':', '/');
        return rest.Length > 0 ? rest : null;
    }

    // Content of a tag bracket ([…], 【…】, 〔…〕, 〖…〗) — the same set StripBracketTags removes.
    // Parentheses are deliberately excluded (handled separately, and often part of the real title).
    private static readonly Regex BracketContent =
        new(@"[\[【〔〖]\s*(?<inner>[^\[\]【】〔〕〖〗]+?)\s*[\]】〕〗]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Per-song station tags like "[1MRl]" / "[vJ]" / "[SEV]" — short, space-less ASCII alnum codes,
    // not artist names. Genuine short names (GUMI, IA) may slip through, but offering one as an
    // artist is harmless: the catalog guard just rejects a wrong candidate.
    private static bool LooksLikeStationTag(string s) =>
        s.Length <= 5 && s.All(c => c < 128 && char.IsLetterOrDigit(c));

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

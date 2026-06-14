using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Normalization helpers for metadata lookups. Radio (and YouTube) titles carry noise a
/// catalog search can't match against — bracketed vocalist/circle tags ("[Megurine Luka]
/// … [SEV]"), parenthetical "(Official Video)", "feat." credits. Cleaning these before
/// searching is what turns a no-match into a hit; the matched canonical fields then flow
/// back through the normal pipeline.
/// </summary>
public static class SearchTerms
{
    private static readonly Regex Brackets = new(@"[\(\[\{][^\(\)\[\]\{\}]*[\)\]\}]", RegexOptions.Compiled);
    private static readonly Regex Feat = new(@"\b(feat\.?|ft\.?|featuring)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);

    /// <summary>Clean a title/artist for a catalog search: drop bracketed tags and "feat."
    /// credits, collapse whitespace. Falls back to the trimmed input if cleaning would
    /// empty it (e.g. a title that is entirely one bracketed phrase).</summary>
    public static string CleanForSearch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Brackets.Replace(s, " ");
        t = Feat.Replace(t, " ");
        t = Whitespace.Replace(t, " ").Trim().Trim('-', '–', '—', '|', '~', '·', ' ');
        return t.Length == 0 ? s.Trim() : t;
    }

    /// <summary>Strip only square-bracket tags, used for the radio now-playing title where
    /// "[Vocalist]Song [Circle]" should display as "Song" but parentheses (often part of
    /// the real title, e.g. "(Remix)") are kept.</summary>
    public static string StripBracketTags(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        var t = Regex.Replace(s, @"\[[^\]]*\]", " ");
        t = Whitespace.Replace(t, " ").Trim();
        return t.Length == 0 ? s.Trim() : t;
    }

    /// <summary>Lower-cased alphanumeric tokens, for loose match comparison.</summary>
    public static IReadOnlyList<string> Tokens(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        var t = NonAlnum.Replace(s, " ").ToLowerInvariant();
        return [.. t.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// Score how well a catalog result matches the query, or null to reject it.
    ///
    /// Title is the primary signal, measured DIRECTIONALLY as how much of the query title the
    /// result covers — so a result whose title is a strict subset of the query ("Sky" for a
    /// "Beyond the Sky" query) does not score a full match, while the legitimate reverse (the
    /// catalog has a longer "… (Remaster)" title) still does. Spacing/camelCase differences
    /// ("BitterSweet" vs "Bitter Sweet") and token reordering count as equal titles.
    ///
    /// When both artists are known they must share a token — a same-title track by an
    /// unrelated act (a metal band's "Beyond the Sky" vs. the broadcast Vocaloid producer) is
    /// rejected; a wrong cover is worse than none. Beyond that floor the artist is a ranking
    /// bonus (broadcast vs. store credits differ in formatting/romanization). When NO artist
    /// is known, a title-only match can latch onto a cover/different song, so we require the
    /// titles to actually be the same (not a subset). A 1-token title is too generic to accept
    /// on the title alone.
    ///
    /// <paramref name="artistConfirmed"/> = the artist was established out-of-band (e.g. a
    /// VocaDB <c>artistId</c>-scoped search): the result's artist string may carry a canonical
    /// name not the broadcast alias ("AIKA" vs. "NGC 3.14"), so the artist gate is skipped.
    /// </summary>
    public static double? MatchScore(string queryTitle, string? queryArtist, string? resultTitle, string? resultArtist,
                                     bool artistConfirmed = false)
    {
        var qt = Tokens(CleanForSearch(queryTitle));
        var rt = Tokens(resultTitle);
        if (qt.Count == 0 || rt.Count == 0) return null;

        bool titlesEqual = SquashEqual(queryTitle, resultTitle) || SameSet(qt, rt);
        double titleCover = titlesEqual ? 1.0 : Coverage(qt, rt);

        var qa = Tokens(CleanForSearch(queryArtist));
        var ra = Tokens(resultArtist);

        if (artistConfirmed) return titleCover >= 0.6 ? titleCover + 0.5 : null;

        // No artist to corroborate: a loose/subset title can be a cover or a different song,
        // so require the titles to genuinely match.
        if (qa.Count == 0) return titlesEqual ? 1.0 : null;

        if (titleCover < 0.6) return null;

        var artistCover = Coverage(qa, ra);
        if (ra.Count > 0 && artistCover == 0) return null;   // known but disjoint artist → reject
        if (qt.Count < 2 && artistCover < 0.5) return null;   // generic 1-word title needs the artist

        return titleCover + 0.5 * artistCover;
    }

    // Bare alphanumerics, lower-cased — collapses spacing/punctuation/case for title compare.
    private static string Squash(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool SquashEqual(string? a, string? b)
    {
        var sa = Squash(a);
        return sa.Length > 0 && sa == Squash(b);
    }

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count > 0 && new HashSet<string>(a).SetEquals(b);

    // Fraction of the QUERY tokens present in the result (directional). 0 when either is empty.
    private static double Coverage(IReadOnlyList<string> query, IReadOnlyList<string> result)
    {
        if (query.Count == 0 || result.Count == 0) return 0;
        var rs = result.ToHashSet();
        return (double)query.Count(rs.Contains) / query.Count;
    }
}

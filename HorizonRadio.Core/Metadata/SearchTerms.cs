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
    /// Score how well a catalog result matches the query, or null to reject it. The title
    /// must overlap strongly (the primary signal) and, when both artists are known, they
    /// must share at least one token — a same-title track by an unrelated act (e.g.
    /// "Beyond the Sky" by a metal band vs. the broadcast Vocaloid producer) is a wrong
    /// match, and a wrong cover is worse than none (we fall back to the station logo).
    /// Beyond that floor the artist is a ranking bonus, so a partial/looser credit still
    /// scores — the broadcast artist and a store's credit often differ in formatting
    /// ("feat." names, a circle, romanization). A 1-token title is too generic to accept
    /// on the title alone, so those also require artist agreement.
    ///
    /// <paramref name="artistConfirmed"/> = the artist was already established out-of-band
    /// (e.g. a VocaDB <c>artistId</c>-scoped search): the result's artist string may carry a
    /// canonical name rather than the broadcast alias ("AIKA" vs. "NGC 3.14"), so the artist
    /// gate is skipped — a strong title match alone is enough.
    /// </summary>
    public static double? MatchScore(string queryTitle, string? queryArtist, string? resultTitle, string? resultArtist,
                                     bool artistConfirmed = false)
    {
        var qt = Tokens(CleanForSearch(queryTitle));
        var rt = Tokens(resultTitle);
        if (qt.Count == 0 || rt.Count == 0) return null;

        var titleOverlap = Overlap(qt, rt);
        // Spacing / camelCase differences ("BitterSweet" vs "Bitter Sweet") tokenize apart;
        // if the titles are equal once squashed to bare alphanumerics, it's a full match.
        if (titleOverlap < 1.0 && Squash(queryTitle) is { Length: > 0 } qs && qs == Squash(resultTitle))
            titleOverlap = 1.0;
        if (titleOverlap < 0.6) return null;

        if (artistConfirmed) return titleOverlap + 0.5;

        var qa = Tokens(CleanForSearch(queryArtist));
        var ra = Tokens(resultArtist);
        var artistOverlap = Overlap(qa, ra);

        // Known-but-disjoint artists → different act, reject (the title-only false match).
        if (qa.Count > 0 && ra.Count > 0 && artistOverlap == 0) return null;

        // Single-word titles ("Secret", "Heart") match too easily; demand the artist back them up.
        if (qt.Count < 2 && artistOverlap < 0.5) return null;

        return titleOverlap + 0.5 * artistOverlap;
    }

    // Bare alphanumerics, lower-cased — collapses spacing/punctuation/case for title compare.
    private static string Squash(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Fraction of the smaller token set that appears in the other (order-insensitive).
    // 0 when either side is empty.
    private static double Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var sb = b.ToHashSet();
        int shared = a.Count(sb.Contains);
        return (double)shared / Math.Min(a.Count, b.Count);
    }
}

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
    // ASCII ()[]{}  plus the CJK tag brackets Vocaloid/doujin stations use: 【…】 (lenticular,
    // the common vocalist tag like 【IA】), 〔…〕 / 〖…〗 (tortoise-shell), and fullwidth （…）.
    // Without the CJK pairs a title like "【IA】 Azure Lines" reaches the catalog with the
    // vocalist tag attached and never matches.
    private static readonly Regex Brackets = new(
        @"[\(\[\{][^\(\)\[\]\{\}]*[\)\]\}]|【[^【】]*】|〔[^〔〕]*〕|〖[^〖〗]*〗|（[^（）]*）", RegexOptions.Compiled);
    // Display-side tag strip: square brackets and the CJK lenticular/tortoise tags, but NOT
    // parentheses (often part of the real title, e.g. "(Remix)").
    private static readonly Regex TagBrackets = new(
        @"\[[^\]]*\]|【[^【】]*】|〔[^〔〕]*〕|〖[^〖〗]*〗", RegexOptions.Compiled);
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
        // Fall back to the trimmed original when cleaning leaves nothing matchable — e.g. "+(Plus)"
        // would otherwise reduce to "+" (no letters/digits), an empty query that can never match.
        return t.Any(char.IsLetterOrDigit) ? t : s.Trim();
    }

    /// <summary>Strip bracketed tags, used for the radio now-playing title where
    /// "[Vocalist]Song [Circle]" or "【IA】Song" should display as "Song" but parentheses (often
    /// part of the real title, e.g. "(Remix)") are kept. Handles both ASCII square brackets and
    /// the CJK lenticular/tortoise tags Vocaloid stations use as vocalist/circle markers.</summary>
    public static string StripBracketTags(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        var t = TagBrackets.Replace(s, " ");
        t = Whitespace.Replace(t, " ").Trim();
        return t.Length == 0 ? s.Trim() : t;
    }

    /// <summary>A grouping key for an artist credit: the producer name (before "feat."), squashed to
    /// bare lowercase alphanumerics. "kiichi" and "kiichi feat. GUMI" share a key; "EZFG" and
    /// "Kerosene" don't. Used to decide whether a set of title-only matches agree on one artist.</summary>
    public static string ArtistKey(string? artist) => Squash(Feat.Replace(artist ?? "", ""));

    /// <summary>True when an artist string carries no search tokens — the "title-only" case, where
    /// <see cref="MatchScore"/> can only match on title equality. This is the exact predicate
    /// MatchScore's no-artist branch uses (<c>Tokens(CleanForSearch(artist)).Count == 0</c>), shared
    /// so the providers' <see cref="TitleOnlyGuard"/> can't drift from the scorer.</summary>
    public static bool IsArtistless(string? artist) => Tokens(CleanForSearch(artist)).Count == 0;

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

        // Spacing/punctuation-only artist differences ("Kairiki Bear" vs "Kairikibear", "DECO*27" vs
        // "DECO 27") read as zero token overlap, yet they're the same act — bridge them via squash,
        // exactly as the title compare already does. A squash match is a full-strength corroboration,
        // so it both clears the zero-overlap gate below and scores like an agreeing artist.
        if (SquashArtistMatch(queryArtist, resultArtist)) artistCover = 1.0;

        if (ra.Count > 0 && artistCover == 0)
        {
            // Zero overlap usually means a genuinely different act (reject — a wrong cover is worse
            // than none). But it can also just be different scripts the token compare can't bridge:
            // a romaji broadcast name ("Bunmyaku") against a kanji catalog name ("文脈"). When the
            // query artist is romaji-only and the result's PRODUCER name is non-Latin, the artist is
            // unverifiable, not contradicted — fall back to the no-artist rule (a genuinely equal,
            // multi-word title only; never a loose/subset or 1-word title that could be a cover).
            //
            // Crucially we test the producer (the credit before "feat"), not the whole string: a
            // result like "Other Band feat. 初音ミク" carries CJK only in the vocalist credit, and
            // "Other Band" is a Latin producer the romaji query COULD have matched — its zero
            // overlap is a real mismatch, so that must still reject (the "metal band" guard).
            var producer = Feat.Replace(resultArtist ?? "", "");
            bool crossScript = HasLatin(queryArtist) && !HasCjk(queryArtist)
                && HasCjk(producer) && !HasLatin(producer);
            if (crossScript) return titlesEqual && qt.Count >= 2 ? titleCover : null;
            return null;
        }
        if (qt.Count < 2 && artistCover < 0.5) return null;   // generic 1-word title needs the artist

        return titleCover + 0.5 * artistCover;
    }

    private static bool HasLatin(string? s) =>
        s != null && s.Any(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));

    private static bool HasCjk(string? s) => s != null && s.Any(c =>
        c is (>= '぀' and <= 'ヿ')   // hiragana + katakana
          or (>= '㐀' and <= '鿿')   // CJK unified ideographs
          or (>= '豈' and <= '﫿')); // CJK compatibility ideographs

    // Bare alphanumerics, lower-cased — collapses spacing/punctuation/case for title compare.
    private static string Squash(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool SquashEqual(string? a, string? b)
    {
        var sa = Squash(a);
        return sa.Length > 0 && sa == Squash(b);
    }

    // Two artist strings that differ only by spacing/punctuation/case ("Kairiki Bear" vs
    // "Kairikibear") squash to the same thing — a match token Coverage can't see. The result is
    // also compared against its producer credit (before "feat."), so a "Band feat. <query>"
    // vocalist credit can't fabricate a match (the metal-band guard).
    private static bool SquashArtistMatch(string? queryArtist, string? resultArtist)
    {
        var qs = Squash(queryArtist);
        if (qs.Length == 0) return false;
        if (qs == Squash(resultArtist)) return true;
        return qs == Squash(Feat.Replace(resultArtist ?? "", ""));
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

/// <summary>
/// Decides whether a title-only catalog lookup (a query with no usable artist) is safe to accept.
/// A title match with no artist to corroborate can latch onto a cover or an unrelated same-titled
/// song, so a provider should only accept one when every title-match points to a single, known
/// artist. Feed it each result whose title matched (its <see cref="SearchTerms.MatchScore"/> was
/// non-null); <see cref="IsAmbiguous"/> is then true for a widely-covered title (several distinct
/// artists) or an unverifiable one (a blank artist credit). The guard is inert — never ambiguous —
/// for an artist-bearing query.
///
/// One implementation, used by every provider, keyed via <see cref="SearchTerms.IsArtistless"/> so
/// the gate engages on exactly the queries MatchScore scores as title-only (avoiding a raw-vs-
/// cleaned-artist mismatch).
/// </summary>
public sealed class TitleOnlyGuard
{
    // Distinct producer-credit keys of the title-matches seen; null when the query has an artist
    // (gate inert). The empty-string key marks a result whose artist is blank/unverifiable.
    private readonly HashSet<string>? _artists;

    public TitleOnlyGuard(string? queryArtist)
        => _artists = SearchTerms.IsArtistless(queryArtist) ? new HashSet<string>(StringComparer.Ordinal) : null;

    /// <summary>Record a result whose title matched the query.</summary>
    public void Observe(string? resultArtist) => _artists?.Add(SearchTerms.ArtistKey(resultArtist));

    /// <summary>True when the title-only matches are too ambiguous to attach art: more than one
    /// distinct artist, or a lone match with a blank/unverifiable artist credit.</summary>
    public bool IsAmbiguous => _artists is { Count: > 1 } || (_artists is { Count: 1 } && _artists.Contains(""));
}

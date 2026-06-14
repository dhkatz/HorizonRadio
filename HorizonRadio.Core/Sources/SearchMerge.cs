using System.Text.RegularExpressions;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// One display row over one-or-more <see cref="SearchResult"/>s the merge judged to be
/// the same track from different sources (e.g. the same song on Spotify and YouTube).
/// <see cref="Sources"/> keeps every underlying result in encounter order, so the UI can
/// label each source and let the user pick which one actually plays.
/// </summary>
/// <param name="Title">Display title (from the first source in the group).</param>
/// <param name="Subtitle">Display subtitle (from the first source in the group).</param>
/// <param name="ArtUrl">Display artwork URL (from the first source in the group).</param>
/// <param name="Sources">The merged results, in the order they were seen.</param>
public sealed record MergedResult(
    string Title,
    string Subtitle,
    string? ArtUrl,
    IReadOnlyList<SearchResult> Sources);

/// <summary>
/// Folds a flat, source-ordered result list into one row per track, merging hits that
/// are confidently the same song across sources. Policy is deliberately CONSERVATIVE —
/// when in doubt it leaves results as separate rows rather than risk folding a cover,
/// remix, or different song together:
///
///   • Match on a normalized token set of title + first artist (lowercased,
///     parentheticals/brackets and "feat." runs stripped, punctuation dropped). Two
///     results match when one token set is a subset of the other — this bridges the
///     cross-source title shape gap (Spotify's bare "Get Lucky" vs. YouTube's
///     "Daft Punk - Get Lucky (Official Audio) ft. …"), since the artist tokens one
///     source carries in its subtitle the other carries in its title.
///   • Require at least two tokens on the smaller side, so single-word titles don't
///     collapse unrelated songs.
///   • Where both sides report a duration, they must be within ~5s — a guard that keeps
///     a radio edit, extended mix, or short snippet from folding into the album cut.
///
/// First-seen order is preserved, so the merged list keeps the sources' own ordering.
/// </summary>
public static class SearchMerge
{
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(5);

    // Bracketed segments ("(Official Video)", "[Remix]") and trailing "feat."/"ft."
    // runs are noise for identity; strip them before tokenizing.
    private static readonly Regex Brackets = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);
    private static readonly Regex FeatRun = new(@"\b(feat|ft|featuring)\b\.?.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NonAlnum = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    public static IReadOnlyList<MergedResult> Merge(IReadOnlyList<SearchResult> results)
    {
        // Each group: the merged results plus the token set of its first member (the
        // key we test new results against). Linear scan — result lists are small.
        var groups = new List<(List<SearchResult> Items, HashSet<string> Tokens)>();

        foreach (var r in results)
        {
            var tokens = TokenSet(r);
            var merged = false;

            foreach (var g in groups)
            {
                if (IsSameTrack(g.Tokens, tokens, g.Items[0].Duration, r.Duration))
                {
                    g.Items.Add(r);
                    merged = true;
                    break;
                }
            }

            if (!merged) groups.Add(([r], tokens));
        }

        return groups.Select(g => new MergedResult(
            Title: g.Items[0].Title,
            Subtitle: g.Items[0].Subtitle,
            ArtUrl: g.Items[0].ArtUrl,
            Sources: g.Items)).ToList();
    }

    private static bool IsSameTrack(HashSet<string> a, HashSet<string> b, TimeSpan? da, TimeSpan? db)
    {
        var smaller = a.Count <= b.Count ? a : b;
        var larger = a.Count <= b.Count ? b : a;
        if (smaller.Count < 2) return false;          // too little signal to be confident
        if (!smaller.IsSubsetOf(larger)) return false;
        return DurationCompatible(da, db);
    }

    // Unknown on either side → don't block the merge (the token match already passed);
    // both known → require them close, so different-length versions stay apart.
    private static bool DurationCompatible(TimeSpan? a, TimeSpan? b)
        => a is null || b is null || (a.Value - b.Value).Duration() <= DurationTolerance;

    private static HashSet<string> TokenSet(SearchResult r)
    {
        var set = new HashSet<string>();
        AddTokens(set, r.Title);
        AddTokens(set, FirstArtist(r.Subtitle));
        return set;
    }

    private static void AddTokens(HashSet<string> set, string? text)
    {
        foreach (var token in Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            set.Add(token);
    }

    /// <summary>Lowercase, strip bracketed segments + "feat." runs + punctuation, collapse
    /// whitespace. Exposed for the merge tests.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.ToLowerInvariant();
        t = Brackets.Replace(t, " ");
        t = FeatRun.Replace(t, " ");
        t = NonAlnum.Replace(t, " ");
        return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Subtitles list multiple credits ("A, B, C"); the first is the primary artist.
    private static string FirstArtist(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle)) return "";
        var comma = subtitle.IndexOf(',');
        return comma < 0 ? subtitle : subtitle[..comma];
    }
}

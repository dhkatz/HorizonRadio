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
    // A word that marks a bracketed group as a credit for the version or who made it ("(… Remix)",
    // "(feat. …)", "(Piano ver.)", "【カバー】"), rather than part of the song's name.
    private static readonly Regex CreditWord = new(
        @"\b(?:remix|rmx|mix|feat|ft|featuring|cover|covered|arrange|arranged|arr|edit|ver|version|prod|produced|remaster(?:ed)?|bootleg)\b|リミックス|カバー|アレンジ|バージョン",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

    /// <summary>True when an artist string can't confirm a match — the "title-only" case, where
    /// <see cref="MatchScore"/> can only match on title equality: it carries no search tokens, or it
    /// names only a synthesizer voice ("Hatsune Miku"), which sings thousands of songs. This is the
    /// exact predicate MatchScore's no-artist branch uses, shared so the providers'
    /// <see cref="TitleOnlyGuard"/> can't drift from the scorer.</summary>
    public static bool IsArtistless(string? artist) =>
        Tokens(CleanForSearch(artist)).Count == 0 || Voicebanks.IsVoicebankCredit(artist);

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
        var score = MatchScoreCore(queryTitle, queryArtist, resultTitle, resultArtist, artistConfirmed);
        // A remix the artists disagree on can still be identified by its credit (see CreditIdentifiesRemix).
        if (score == null && resultTitle != null && CreditIdentifiesRemix(queryTitle, queryArtist, resultTitle))
            score = 1.0;
        if (score == null) return null;

        // Among results that all match, prefer the one whose name IS the query over a longer title
        // that contains it: "ヒバナ" over the mashup "ゴーストルール + ヒバナ + マシュマロ" in the
        // same artist's songs, which otherwise score the same. Small enough to only break ties.
        var name = Tokens(StripCreditGroups(resultTitle!));
        var exactness = name.Count == 0 ? 1.0 : Coverage(name, Tokens(CleanForSearch(queryTitle)));
        score += 0.05 * exactness;

        // Prefer the release the query names: the original over a live, remixed, or re-recorded
        // version ("Lost One's Weeping (2025 New Recording)", "Melt - CPK! Remix") when the query
        // didn't ask for one. Catalogs often list the other versions first.
        if (VersionWord.IsMatch(resultTitle!) && !VersionWord.IsMatch(queryTitle)) score -= 0.1;
        return score;
    }

    // A word that marks a release as another version of the song than the original recording.
    private static readonly Regex VersionWord = new(
        @"\b(?:remix|rmx|mix|edit|bootleg|remodel|rework|flip|cover|covered|arrange|arranged|ver|version|live|instrumental|karaoke|off vocal|acoustic|recording|reloaded|re-recorded)\b|リミックス|カバー|アレンジ|バージョン|ライブ",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Whether a title or a catalog's note on a recording names another version than the
    /// original recording: live, remixed, re-recorded, covered ("MIKU EXPO 2014 in INDONESIA Live").</summary>
    public static bool NamesAnotherVersion(string? text) => !string.IsNullOrEmpty(text) && VersionWord.IsMatch(text);

    // A trailing " - <version>" the stores put after the name ("Melt - CPK! Remix", "Song - Live"),
    // or a version set off by dashes ("Leia -八王子P Remix-").
    private static readonly Regex DashVersion = new(
        @"\s+[-–—]\s+(?<v>[^-–—]+)$|\s+[-–—~〜](?<v>[^-–—~〜]+)[-–—~〜]$", RegexOptions.Compiled);

    /// <summary>Whether two titles name the same song once their credits are set aside: brackets
    /// that credit a version or who made it ("千本桜 (feat. 初音ミク)"), and a store's trailing
    /// " - Remix"-style suffix.</summary>
    private static bool SameName(string queryTitle, string? resultTitle)
    {
        if (resultTitle is null) return false;
        var q = BareName(queryTitle);
        var r = BareName(resultTitle);
        return SquashEqual(q, r) || SameSet(Tokens(q), Tokens(r));
    }

    private static string BareName(string title)
    {
        var name = StripCreditGroups(title);
        var dash = DashVersion.Match(name);
        return dash.Success && VersionWord.IsMatch(dash.Groups["v"].Value) ? name[..dash.Index] : name;
    }

    private static double? MatchScoreCore(string queryTitle, string? queryArtist, string? resultTitle, string? resultArtist,
                                          bool artistConfirmed)
    {
        var qt = Tokens(CleanForSearch(queryTitle));
        var rt = Tokens(resultTitle);
        if (qt.Count == 0 || rt.Count == 0) return null;

        bool titlesEqual = SquashEqual(queryTitle, resultTitle) || SameSet(qt, rt);
        double titleCover = titlesEqual ? 1.0 : Coverage(qt, rt);

        // The query has to match the song's name, not only its credits: "KurageP" must not
        // match "Perfect Seimei (Wada Takeaki (KurageP) Remix)" through the remixer's name. A
        // query that also matches part of the name ("Endless World Wahiko Remix" against
        // "Endless World (Wahiko Remix)") is unaffected.
        if (!titlesEqual)
        {
            var name = Tokens(StripCreditGroups(resultTitle!));
            if (name.Count > 0 && name.Count < rt.Count && Coverage(qt, name) == 0) return null;

            // A one-word title has to be the song's whole name: "air" is not "The Fresh Prince of
            // Bel-Air", whatever the artists share. ("Leia (Hachioji P Remix)" is more than a word.)
            if (qt.Count == 1 && Tokens(queryTitle).Count == 1 && !SameName(queryTitle, resultTitle)) return null;
        }

        var qa = Tokens(CleanForSearch(queryArtist));
        var ra = Tokens(resultArtist);

        if (artistConfirmed) return titleCover >= 0.6 ? titleCover + 0.5 : null;

        // No artist to corroborate: a loose/subset title can be a cover or a different song,
        // so require the titles to genuinely match (credits aside: "千本桜 (feat. 初音ミク)" is
        // 千本桜, so the title-only guard sees it). A credit that names only the singing voice
        // ("Hatsune Miku") corroborates nothing either: the voice sings thousands of songs.
        if (IsArtistless(queryArtist))
        {
            if (!titlesEqual && !SameName(queryTitle, resultTitle)) return null;
            // With nothing else known, a lone Latin word ("ARiA") is anyone's song. A voice
            // credit narrows it to that voice's songs, where the catalog can rank "Melt".
            if (qa.Count == 0 && qt.Count == 1 && !HasCjk(queryTitle)) return null;
            return 1.0;
        }

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
            if (!crossScript || !titlesEqual || qt.Count < 2) return null;

            // Cross-script + exact multi-word title is necessary but NOT sufficient: a same-titled
            // cover by an unrelated Japanese-named artist looks identical to a real romaji↔CJK
            // match. When the catalog producer is purely KANA we can romanize it and confirm it
            // actually sounds like the broadcast name — rejecting "くろくも" for a "HachiojiP" query
            // while still bridging "まふまふ"/"Mafumafu". A KANJI name can't be cheaply romanized,
            // so it stays on the (unverified) title-only acceptance rather than being wrongly rejected.
            var producerRomaji = Romaji.TryRomanize(producer);
            if (producerRomaji != null && !Romaji.SoundsLike(Feat.Replace(queryArtist ?? "", ""), producerRomaji))
                return null;
            return titleCover;
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
    /// <summary>The title without its credit groups: bracketed groups (nesting allowed) that name
    /// the version or who made it ("(Wada Takeaki (KurageP) Remix)", "(feat. GUMI)"). Other groups,
    /// often part of the name ("(Transparent Meteor)"), are kept.</summary>
    internal static string StripCreditGroups(string title) => SplitCredits(title).Name;

    /// <summary>Whether a bracketed phrase credits a version or who made it ("feat. GUMI",
    /// "Piano ver.", "X Remix") rather than naming the song.</summary>
    public static bool IsCredit(string? text) => !string.IsNullOrEmpty(text) && CreditWord.IsMatch(text);

    private static readonly Regex RemixWord = new(
        @"\b(?:remix|rmx|mix|edit|bootleg|remodel|rework|flip)\b|リミックス",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Whether the title credits a remix ("ササメク(… inner world mix)", "Song (X Remix)").
    /// A catalog search for the bare name can bury it under same-named originals and covers, so
    /// a provider may look among remixes too.</summary>
    public static bool HasRemixCredit(string? title) =>
        !string.IsNullOrEmpty(title) && RemixWord.IsMatch(SplitCredits(title).Credits);

    /// <summary>
    /// Whether a remix's own credit identifies it although the artists share nothing: stations
    /// credit the remixer where catalogs credit the original artist, or the other way round, and
    /// some stations credit someone else entirely. "ササメク(sasameku can't be the word found in
    /// reayfla's inner world mix)" by "キョトンP" is the Sakuzyo remix VocaDB lists, and the track
    /// iTunes credits to Shiina Mota. Either is enough, as long as the song's own name is in the
    /// query too:
    /// <list type="bullet">
    /// <item>the credit names the query's artist as the remixer ("Wahiko" for "(Wahiko Remix)"), or</item>
    /// <item>the credit is long enough to name only this remix (four or more words besides "mix",
    /// "remix", …) and the query carries nearly all of it (one misspelled word is fine:
    /// "reayfla" for "reayfia").</item>
    /// </list>
    /// The query is read as written, brackets and all: the credit is usually in them.
    /// </summary>
    internal static bool CreditIdentifiesRemix(string queryTitle, string? queryArtist, string resultTitle)
    {
        var (nameText, creditText) = SplitCredits(resultTitle);
        var credit = Tokens(creditText).Where(t => !CreditWord.IsMatch(t)).ToList();
        if (credit.Count == 0) return false;

        var query = Tokens(queryTitle);
        var name = Tokens(nameText);
        if (name.Count == 0 || Coverage(name, query) < 1.0) return false;

        var artist = Tokens(CleanForSearch(queryArtist));
        if (artist.Count > 0 && Coverage(artist, credit) == 1.0) return true;
        return credit.Count >= 4 && Coverage(credit, query) >= 0.8;
    }

    // A title split into its name and its credit groups (see StripCreditGroups).
    private static (string Name, string Credits) SplitCredits(string title)
    {
        const string open = "([（【〔〖";
        const string close = ")]）】〕〗";
        var sb = new System.Text.StringBuilder(title.Length);
        var credits = new System.Text.StringBuilder();
        int depth = 0, start = 0;
        for (int i = 0; i < title.Length; i++)
        {
            char c = title[i];
            if (open.Contains(c))
            {
                if (depth++ == 0) start = i;
                continue;
            }
            if (close.Contains(c) && depth > 0)
            {
                if (--depth == 0)
                {
                    var group = title[start..(i + 1)];
                    if (!CreditWord.IsMatch(group)) sb.Append(group);
                    else
                    {
                        sb.Append(' ');
                        credits.Append(group).Append(' ');
                    }
                }
                continue;
            }
            if (depth == 0) sb.Append(c);
        }
        if (depth > 0) sb.Append(title[start..]); // unbalanced: keep the rest as written
        return (sb.ToString(), credits.ToString());
    }

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

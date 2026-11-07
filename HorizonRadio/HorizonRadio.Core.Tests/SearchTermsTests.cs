using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Query normalization that turns tag-laden radio titles into something a catalog can
/// match, plus the guard that stops a fuzzy near-miss from attaching the wrong art.
/// </summary>
public class SearchTermsTests
{
    [Fact]
    public void A_title_found_only_in_a_remix_credit_does_not_match()
    {
        // VocaDB, artist-scoped to 和田たけあき, offered this remix for the "title" KurageP (really
        // the producer's alias, from a three-part stream title).
        Assert.Null(SearchTerms.MatchScore("KurageP", "和田たけあき",
            "Perfect Seimei (Wada Takeaki (KurageP) Remix)", "和田たけあき", artistConfirmed: true));
    }

    [Fact]
    public void The_exact_title_outranks_a_longer_title_containing_it()
    {
        var exact = SearchTerms.MatchScore("ヒバナ", "DECO*27", "ヒバナ", "DECO*27 feat. 初音ミク", artistConfirmed: true);
        var mashup = SearchTerms.MatchScore("ヒバナ", "DECO*27", "ゴーストルール + ヒバナ + マシュマロ = ???",
            "DECO*27 feat. 初音ミク", artistConfirmed: true);

        // A one-word title has to be the whole name, so the mashup doesn't even match.
        Assert.NotNull(exact);
        Assert.True(mashup is null || exact > mashup);
    }

    // Vocaloid Radio's title for the Sakuzyo remix of 椎名もた's ササメク, credited to someone else.
    private const string SasamekuQuery = "ササメク(sasameku can't be the word found in reayfla's inner world mix)";

    [Theory]
    [InlineData("Sasameku (Sasameku Can't Be the Word Found in Reayfia's Inner World Mix)", "Shiina Mota")]    // iTunes
    [InlineData("ササメク(sasameku can’t be the word found in reayfia's inner world mix)", "Sakuzyo feat. 初音ミク")] // VocaDB
    public void A_long_remix_credit_identifies_the_remix_when_the_artists_disagree(string title, string artist) =>
        Assert.NotNull(SearchTerms.MatchScore(SasamekuQuery, "(∵)キョトンP (kyotn)", title, artist));

    [Theory]
    [InlineData(SasamekuQuery, true)]
    [InlineData("Endless World (Wahiko Remix)", true)]
    [InlineData("Song (feat. GUMI)", false)]
    [InlineData("玲瓏メテオ (Transparent Meteor)", false)]
    [InlineData("Remix", false)] // a title that is the word, not a credit
    public void Recognizes_a_remix_credit(string title, bool remix) =>
        Assert.Equal(remix, SearchTerms.HasRemixCredit(title));

    [Fact]
    public void The_remixer_named_in_the_credit_is_the_stations_artist()
    {
        Assert.NotNull(SearchTerms.MatchScore("Endless World", "Wahiko", "Endless World (Wahiko Remix)", "Aerial Flow"));
    }

    [Fact]
    public void A_short_credit_does_not_excuse_a_different_artist()
    {
        // "(Original Mix)" names no one remix: a same-titled track by another act is still rejected.
        Assert.Null(SearchTerms.MatchScore("Song (Original Mix)", "Band", "Song (Original Mix)", "Other Act"));
        // The remix credit must be for this song: the name has to be in the query too.
        Assert.Null(SearchTerms.MatchScore(SasamekuQuery, "kyotn",
            "Other Song (Sasameku Can't Be the Word Found in Reayfia's Inner World Mix)", "Shiina Mota"));
    }

    [Fact]
    public void A_remix_query_still_matches_its_remix()
    {
        Assert.NotNull(SearchTerms.MatchScore("Endless World Wahiko Remix", "Aerial Flow",
            "Endless World (Wahiko Remix)", "Aerial Flow"));
        Assert.NotNull(SearchTerms.MatchScore("Endless World", "Aerial Flow",
            "Endless World (Wahiko Remix)", "Aerial Flow"));
    }

    [Fact]
    public void A_name_in_parentheses_is_not_a_credit()
    {
        // "(Transparent Meteor)" is the English name, not a credit; matching it is fine.
        Assert.NotNull(SearchTerms.MatchScore("Transparent Meteor", "Mochizuki Reku",
            "玲瓏メテオ (Transparent Meteor)", "Mochizuki Reku"));
    }

    [Theory]
    [InlineData("Perfect Seimei (Wada Takeaki (KurageP) Remix)", "Perfect Seimei")]
    [InlineData("Song (feat. GUMI) [Piano ver.]", "Song")]
    [InlineData("玲瓏メテオ (Transparent Meteor)", "玲瓏メテオ (Transparent Meteor)")]
    [InlineData("Unbalanced (Remix", "Unbalanced (Remix")]
    public void StripCreditGroups_removes_only_credits(string title, string expected) =>
        Assert.Equal(expected, string.Join(' ', SearchTerms.StripCreditGroups(title).Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Theory]
    [InlineData("[Megurine Luka]Sacred Secret [SEV]", "Sacred Secret")]
    [InlineData("Blinding Lights (Official Video)", "Blinding Lights")]
    [InlineData("Song Title feat. Someone", "Song Title")]
    [InlineData("Tune ft. X & Y", "Tune")]
    [InlineData("Plain Title", "Plain Title")]
    // Fullwidth lenticular vocalist tag (Vocaloid stations): 【IA】 must be stripped too.
    [InlineData("【IA】 Azure Lines", "Azure Lines")]
    [InlineData("【初音ミク】メルト", "メルト")]
    public void CleanForSearch_strips_tags_and_credits(string input, string expected)
        => Assert.Equal(expected, SearchTerms.CleanForSearch(input));

    [Fact]
    public void CleanForSearch_falls_back_when_cleaning_would_empty()
        => Assert.Equal("[only tags]", SearchTerms.CleanForSearch("[only tags]"));

    [Fact]
    public void CleanForSearch_keeps_a_title_that_would_reduce_to_punctuation()
        // "+(Plus)" must not collapse to "+" (no letters/digits → empty query that can never match);
        // keep the original so "Plus" survives for searching/scoring.
        => Assert.Equal("+(Plus)", SearchTerms.CleanForSearch("+(Plus)"));

    [Fact]
    public void ArtistKey_groups_by_producer_credit()
    {
        // Same producer with/without a vocalist credit share a key; genuinely different acts don't.
        Assert.Equal(SearchTerms.ArtistKey("kiichi"), SearchTerms.ArtistKey("kiichi feat. GUMI"));
        Assert.NotEqual(SearchTerms.ArtistKey("EZFG"), SearchTerms.ArtistKey("Kerosene"));
    }

    [Fact]
    public void IsArtistless_matches_MatchScores_no_artist_branch()
    {
        Assert.True(SearchTerms.IsArtistless(null));
        Assert.True(SearchTerms.IsArtistless(""));
        // Non-whitespace but token-empty — MatchScore scores this title-only, so IsArtistless must
        // agree (the bug a raw IsNullOrWhiteSpace gate would miss).
        Assert.True(SearchTerms.IsArtistless("--"));
        Assert.True(SearchTerms.IsArtistless("（）"));
        Assert.False(SearchTerms.IsArtistless("MuryokuP"));
    }

    [Fact]
    public void TitleOnlyGuard_is_inert_for_an_artist_bearing_query()
    {
        var g = new TitleOnlyGuard("MuryokuP");
        g.Observe("Cover A");
        g.Observe("Cover B");                 // two distinct artists, but the query had an artist
        Assert.False(g.IsAmbiguous);
    }

    [Fact]
    public void TitleOnlyGuard_engages_for_a_punctuation_only_artist()
    {
        // "--" cleans to no tokens, so MatchScore treats it title-only; the guard must engage too.
        var g = new TitleOnlyGuard("--");
        g.Observe("Cover A");
        g.Observe("Cover B");
        Assert.True(g.IsAmbiguous);
    }

    [Fact]
    public void TitleOnlyGuard_flags_distinct_or_blank_artists_but_accepts_one_producer()
    {
        var distinct = new TitleOnlyGuard("");
        distinct.Observe("EZFG");
        distinct.Observe("Kerosene");
        Assert.True(distinct.IsAmbiguous);            // several distinct artists → ambiguous

        var blank = new TitleOnlyGuard("");
        blank.Observe(null);
        Assert.True(blank.IsAmbiguous);               // lone unverifiable credit → ambiguous

        var single = new TitleOnlyGuard("");
        single.Observe("Wonderful★opportunity!");
        single.Observe("Wonderful★opportunity! feat. 初音ミク");
        Assert.False(single.IsAmbiguous);             // one producer (with/without vocalist) → safe
    }

    [Theory]
    [InlineData("[Megurine Luka]Sacred Secret [SEV]", "Sacred Secret")]
    [InlineData("Song (Remix)", "Song (Remix)")] // parentheses kept for display
    [InlineData("【IA】 Azure Lines [SEG]", "Azure Lines")] // fullwidth vocalist tag + ASCII circle tag
    [InlineData("〔cover〕 Senbonzakura", "Senbonzakura")] // tortoise-shell tag
    public void StripBracketTags_removes_bracket_tags(string input, string expected)
        => Assert.Equal(expected, SearchTerms.StripBracketTags(input));

    [Fact]
    public void MatchScore_accepts_a_real_match_through_the_tags()
        => Assert.NotNull(SearchTerms.MatchScore(
            "[Megurine Luka]Sacred Secret [SEV]", "MuryokuP", "Sacred Secret", "MuryokuP"));

    [Fact]
    public void MatchScore_rejects_an_unrelated_title()
        => Assert.Null(SearchTerms.MatchScore(
            "Sacred Secret", "MuryokuP", "Completely Different Song", "Other Artist"));

    [Fact]
    public void MatchScore_rejects_same_title_by_a_different_artist()
        // The "Beyond the Sky" false positive: exact title, but the candidate is a totally
        // unrelated act — reject rather than attach the wrong cover.
        => Assert.Null(SearchTerms.MatchScore(
            "Beyond the Sky", "hano", "Beyond the Sky", "Dreams of Gray"));

    [Fact]
    public void MatchScore_accepts_a_cross_script_artist_when_the_title_is_an_exact_multiword_match()
    {
        // Romaji broadcast name "Bunmyaku" vs kanji catalog name "文脈" share no token, but that's
        // a script difference, not a contradiction. An exact multi-word title carries the match.
        Assert.NotNull(SearchTerms.MatchScore("Innocent Favor", "Bunmyaku", "Innocent Favor", "文脈 feat. GUMI"));
        // …but a 1-word title is too generic to accept without a verifiable artist.
        Assert.Null(SearchTerms.MatchScore("Favor", "Bunmyaku", "Favor", "文脈"));
        // …and a subset/loose title is not enough either.
        Assert.Null(SearchTerms.MatchScore("Innocent Favor", "Bunmyaku", "Favor", "文脈"));
    }

    [Fact]
    public void MatchScore_still_rejects_a_same_script_disjoint_artist()
        // Both romaji, genuinely different act — the cross-script relaxation must NOT apply here
        // (this is the "metal band Beyond the Sky" guard).
        => Assert.Null(SearchTerms.MatchScore("Innocent Favor", "Bunmyaku", "Innocent Favor", "Some Other Band"));

    [Fact]
    public void MatchScore_cross_script_looks_at_the_producer_not_a_cjk_vocalist_credit()
        // The result's CJK is only in the vocalist credit; the producer "Other Band" is a Latin
        // name the romaji query could have matched, so zero overlap is a real mismatch → reject.
        // (Without the producer-portion check this would false-match on the exact multi-word title.)
        => Assert.Null(SearchTerms.MatchScore("Innocent Favor", "Bunmyaku", "Innocent Favor", "Other Band feat. 初音ミク"));

    [Fact]
    public void MatchScore_rejects_a_kana_cover_by_a_different_artist()
        // The real bug: iTunes returns "KiLLER LADY" by くろくも (kurokumo) for a HachiojiP query.
        // Exact title + cross-script used to accept it; romanizing the kana producer shows
        // "kurokumo" ≠ "hachiojip", so the wrong cover's art is now rejected.
        => Assert.Null(SearchTerms.MatchScore("KiLLER LADY", "HachiojiP feat. GUMI", "KiLLER LADY", "くろくも"));

    [Fact]
    public void MatchScore_accepts_a_kana_artist_that_romanizes_to_the_query()
        // The legit cross-script case the romanizer should still bridge: the same act, broadcast in
        // romaji and listed in kana, sounds alike → accept on the exact multi-word title.
        => Assert.NotNull(SearchTerms.MatchScore("Sweet Devil", "Mafumafu", "Sweet Devil", "まふまふ"));

    [Fact]
    public void MatchScore_allows_a_partial_or_unknown_result_artist()
    {
        // Shares a token (looser store credit) → still matches.
        Assert.NotNull(SearchTerms.MatchScore(
            "Long Distinct Title", "MuryokuP", "Long Distinct Title", "MuryokuP feat. Miku"));
        // Result artist unknown → can't contradict; a strong multi-word title still counts.
        Assert.NotNull(SearchTerms.MatchScore(
            "Long Distinct Title", "MuryokuP", "Long Distinct Title", null));
    }

    [Fact]
    public void MatchScore_agreeing_artist_outscores_an_unknown_one()
    {
        var agrees = SearchTerms.MatchScore("Long Distinct Title", "RealArtist", "Long Distinct Title", "RealArtist");
        var unknown = SearchTerms.MatchScore("Long Distinct Title", "RealArtist", "Long Distinct Title", null);
        Assert.True(agrees > unknown); // artist agreement is a ranking bonus above the floor
    }

    [Fact]
    public void MatchScore_requires_artist_for_single_word_titles()
    {
        Assert.Null(SearchTerms.MatchScore("Secret", "MuryokuP", "Secret", "Unrelated Band"));
        Assert.NotNull(SearchTerms.MatchScore("Secret", "MuryokuP", "Secret", "MuryokuP"));
    }

    [Fact]
    public void MatchScore_matches_across_spacing_and_camelcase()
    {
        // "BitterSweet" (broadcast) vs "Bitter Sweet" (catalog) — same once squashed.
        Assert.NotNull(SearchTerms.MatchScore("BitterSweet", "AIKA", "Bitter Sweet", "AIKA"));
        // Still gated on artist when not confirmed — a different act is rejected.
        Assert.Null(SearchTerms.MatchScore("BitterSweet", "NGC 3.14", "Bitter Sweet", "AIKA feat. Hatsune Miku"));
    }

    [Fact]
    public void MatchScore_bridges_an_artist_spacing_difference()
    {
        // "Kairiki Bear" (broadcast) vs "Kairikibear" (catalog) — same act, spacing only. Token
        // overlap is zero, so without the squash bridge the correct title match was rejected.
        Assert.NotNull(SearchTerms.MatchScore(
            "Imitation Psychotropic", "Kairiki Bear", "Imitation Gallery (Gate of Psychotropic)", "Kairikibear"));
        // The producer credit (before "feat.") is what's compared, so a vocalist credit is fine.
        Assert.NotNull(SearchTerms.MatchScore(
            "Imitation Gallery", "Kairiki Bear", "Imitation Gallery", "Kairikibear feat. GUMI"));
        // A genuinely different act that merely squashes differently is still rejected.
        Assert.Null(SearchTerms.MatchScore(
            "Imitation Psychotropic", "Kairiki Bear", "Imitation Gallery (Gate of Psychotropic)", "Some Other Band"));
    }

    [Fact]
    public void MatchScore_rejects_a_result_title_that_is_a_subset_of_the_query()
        // "Sky" is not "Beyond the Sky" — directional coverage rejects the shorter result.
        => Assert.Null(SearchTerms.MatchScore("Beyond the Sky", "hano", "Sky", "hano"));

    [Fact]
    public void MatchScore_still_matches_a_longer_catalog_title()
        // The legitimate reverse: catalog has extra trailing words.
        => Assert.NotNull(SearchTerms.MatchScore("Sacred Secret", "MuryokuP", "Sacred Secret (Remaster)", "MuryokuP"));

    [Fact]
    public void MatchScore_title_only_rejects_a_loose_match_but_accepts_an_exact_one()
    {
        // No artist to corroborate → a subset/loose title must not match (cover/other-song risk)…
        Assert.Null(SearchTerms.MatchScore("Beyond the Sky", "", "A Place beyond the starry sky", "MIJIPIN"));
        // …but an exact title is the best we can do without an artist.
        Assert.NotNull(SearchTerms.MatchScore("Beyond the Sky", "", "Beyond the Sky", "Whoever"));
    }

    [Fact]
    public void MatchScore_artist_confirmed_skips_the_artist_gate()
    {
        // The artistId-scoped VocaDB case: artist already confirmed, the credit shows the
        // canonical name not the alias — a strong (squash) title match alone qualifies.
        Assert.NotNull(SearchTerms.MatchScore(
            "BitterSweet", "NGC 3.14", "Bitter Sweet", "AIKA feat. Hatsune Miku", artistConfirmed: true));
        // But a genuinely different title is still rejected even when artist-confirmed.
        Assert.Null(SearchTerms.MatchScore(
            "BitterSweet", "NGC 3.14", "Totally Other Song", "AIKA", artistConfirmed: true));
    }
}

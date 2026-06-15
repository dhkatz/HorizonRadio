using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Query normalization that turns tag-laden radio titles into something a catalog can
/// match, plus the guard that stops a fuzzy near-miss from attaching the wrong art.
/// </summary>
public class SearchTermsTests
{
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

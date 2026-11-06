using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources.Radio;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// ICY StreamTitle parsing for radio: plain "Artist - Title", and the "Channel - Artist -
/// Title" prefix form some stations broadcast (which must not leave the channel as the artist).
/// </summary>
public class RadioStreamTitleTests
{
    [Theory]
    // Channel-prefixed → peel the channel, recover real artist + title.
    [InlineData("ExGrooveCh - Heavenz - テロメアの産声", "Heavenz", "テロメアの産声")]
    // Plain "Artist - Title" → unchanged.
    [InlineData("MuryokuP - Sacred Secret", "MuryokuP", "Sacred Secret")]
    // Bracket tags in the title are NOT a separator → not re-split (stripped later, at display).
    [InlineData("hano - [Hatsune Miku] Beyond the Sky", "hano", "[Hatsune Miku] Beyond the Sky")]
    public void Parse_recovers_artist_and_title(string raw, string expectedArtist, string expectedTitle)
    {
        var (artist, title, _) = RadioStreamTitle.Parse(raw);
        Assert.Equal(expectedArtist, artist);
        Assert.Equal(expectedTitle, title);
    }

    [Fact]
    public void Parse_title_only_keeps_the_title()
    {
        var (artist, title, confidence) = RadioStreamTitle.Parse("Just A Title");
        Assert.Null(artist);
        Assert.Equal("Just A Title", title);
        // No separator → Low confidence, the signal the model escalates on.
        Assert.Equal(ParseConfidence.Low, confidence);
    }

    [Fact]
    public void Parse_surfaces_confidence_for_model_escalation()
    {
        // Clean two-part split → High (the model is skipped under Escalate).
        Assert.Equal(ParseConfidence.High, RadioStreamTitle.Parse("MuryokuP - Sacred Secret").Confidence);
        // Channel-prefixed three-part → ambiguous → Medium (the model escalates).
        Assert.Equal(ParseConfidence.Medium,
            RadioStreamTitle.Parse("ExGrooveCh - Heavenz - テロメアの産声").Confidence);
    }

    private static bool Has(IReadOnlyList<HorizonRadio.Core.Models.TitleCandidate> alts, string? artist, string title) =>
        alts.Any(c => c.Artist == artist && c.Title == title);

    [Fact]
    public void ParseCandidates_channel_prefix_primary_and_alternatives()
    {
        var (primary, alts, _) = RadioStreamTitle.ParseCandidates("ExGrooveCh - Heavenz - テロメアの産声");

        Assert.Equal("Heavenz", primary.Artist);          // channel peeled
        Assert.Equal("テロメアの産声", primary.Title);
        Assert.True(Has(alts, "ExGrooveCh", "Heavenz - テロメアの産声")); // the other split
        Assert.True(Has(alts, "テロメアの産声", "Heavenz"));            // reversed order
    }

    [Fact]
    public void ParseCandidates_offers_parenthetical_native_name_as_an_alt_artist()
    {
        // "Romaji (NativeName)" — the native name in the parens is the catalog-matchable form,
        // so it must be offered as an alternative artist for the same title.
        var (primary, alts, _) = RadioStreamTitle.ParseCandidates("Itachima-p (いたちま) - サクリファイス");
        Assert.Equal("サクリファイス", primary.Title);
        Assert.True(Has(alts, "いたちま", "サクリファイス"));
    }

    [Fact]
    public void ParseCandidates_fullwidth_reversed_order_offers_the_right_candidate()
    {
        // "Title／Artist" with a fullwidth slash — the plain parse can't split it, but the
        // reversed candidate recovers (Heavenz, テロメアの産声) for the catalog to confirm.
        var (_, alts, _) = RadioStreamTitle.ParseCandidates("テロメアの産声／Heavenz");
        Assert.True(Has(alts, "Heavenz", "テロメアの産声"));
    }

    [Fact]
    public void ParseCandidates_clean_two_part_primary_with_reversed_alt()
    {
        var (primary, alts, _) = RadioStreamTitle.ParseCandidates("MuryokuP - Sacred Secret");
        Assert.Equal("MuryokuP", primary.Artist);
        Assert.Equal("Sacred Secret", primary.Title);
        Assert.True(Has(alts, "Sacred Secret", "MuryokuP")); // reversed, for catalog validation
    }

    [Fact]
    public void ParseCandidates_splits_a_spaced_ascii_slash_into_title_and_artist()
    {
        // "<uploader> - <TITLE> / <Artist> ft.<vocalist>" — the spaced ASCII "/" separates title and
        // artist (like the already-handled fullwidth ／). The reversed candidate must recover
        // (HarryP…, TODAY THE FUTURE) for the catalog to confirm; "(∵)キョトンP" is just the uploader.
        var (_, alts, _) = RadioStreamTitle.ParseCandidates(
            "(∵)キョトンP (kyotn) - TODAY THE FUTURE / HarryP ft.初音ミク [1MR8]");

        Assert.True(Has(alts, "HarryP ft.初音ミク [1MR8]", "TODAY THE FUTURE"));
    }

    [Fact]
    public void ParseCandidates_does_not_split_an_unspaced_slash()
    {
        // "AC/DC" must stay intact — only a space-padded "/" is a separator.
        var (primary, _, _) = RadioStreamTitle.ParseCandidates("AC/DC - Thunderstruck");
        Assert.Equal("AC/DC", primary.Artist);
        Assert.Equal("Thunderstruck", primary.Title);
    }

    [Fact]
    public void ParseCandidates_strips_a_duplicated_artist_prefix_from_the_title()
    {
        // "AVTechNO! - AVTechNO! tear feat.Hatsune Miku" — the artist is repeated as a title prefix,
        // which dilutes title coverage. Offer the de-prefixed title so the real "tear" can match.
        var (primary, alts, _) = RadioStreamTitle.ParseCandidates("AVTechNO! - AVTechNO! tear feat.Hatsune Miku");

        Assert.Equal("AVTechNO!", primary.Artist);
        Assert.True(Has(alts, "AVTechNO!", "tear feat.Hatsune Miku"));
    }

    [Fact]
    public void ParseCandidates_offers_a_title_only_last_resort()
    {
        // Uploader-credited track ("(∵)キョトンP" is a channel, not the producer): a title-only
        // candidate lets the catalog resolve it by title when the results agree on one artist.
        var (_, alts, _) = RadioStreamTitle.ParseCandidates("(∵)キョトンP (kyotn) - 狂騒ノ現");

        Assert.Contains(alts, c => c.Artist is null && c.Title == "狂騒ノ現");
    }

    [Fact]
    public void ParseCandidates_offers_a_bracketed_producer_as_an_alt_artist()
    {
        // The "Migratory" case: the real catalog artist ("Clean Tears") is in a bracket the parse
        // strips, while the leading "keerosah" (uploader) becomes the primary artist. The bracket's
        // content must be offered as an alt artist for the bare title so the catalog can confirm it.
        var (primary, alts, _) = RadioStreamTitle.ParseCandidates(
            "keerosah - [初音ミク] Migratory [Clean Tears] [1MRl]");

        Assert.Equal("keerosah", primary.Artist);
        Assert.True(Has(alts, "Clean Tears", "Migratory"));   // the producer-in-a-tag candidate
        Assert.True(Has(alts, "初音ミク", "Migratory"));        // the vocalist tag too (harmless)
        Assert.False(Has(alts, "1MRl", "Migratory"));         // the station code is filtered out
    }
}

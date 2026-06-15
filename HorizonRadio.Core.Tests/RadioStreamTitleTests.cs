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
}

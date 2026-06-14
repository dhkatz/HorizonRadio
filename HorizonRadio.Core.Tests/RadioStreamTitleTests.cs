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
        var (artist, title) = RadioStreamTitle.Parse(raw);
        Assert.Equal(expectedArtist, artist);
        Assert.Equal(expectedTitle, title);
    }

    [Fact]
    public void Parse_title_only_keeps_the_title()
    {
        var (artist, title) = RadioStreamTitle.Parse("Just A Title");
        Assert.Null(artist);
        Assert.Equal("Just A Title", title);
    }

    private static bool Has(IReadOnlyList<HorizonRadio.Core.Models.TitleCandidate> alts, string? artist, string title) =>
        alts.Any(c => c.Artist == artist && c.Title == title);

    [Fact]
    public void ParseCandidates_channel_prefix_primary_and_alternatives()
    {
        var (primary, alts) = RadioStreamTitle.ParseCandidates("ExGrooveCh - Heavenz - テロメアの産声");

        Assert.Equal("Heavenz", primary.Artist);          // channel peeled
        Assert.Equal("テロメアの産声", primary.Title);
        Assert.True(Has(alts, "ExGrooveCh", "Heavenz - テロメアの産声")); // the other split
        Assert.True(Has(alts, "テロメアの産声", "Heavenz"));            // reversed order
    }

    [Fact]
    public void ParseCandidates_fullwidth_reversed_order_offers_the_right_candidate()
    {
        // "Title／Artist" with a fullwidth slash — the plain parse can't split it, but the
        // reversed candidate recovers (Heavenz, テロメアの産声) for the catalog to confirm.
        var (_, alts) = RadioStreamTitle.ParseCandidates("テロメアの産声／Heavenz");
        Assert.True(Has(alts, "Heavenz", "テロメアの産声"));
    }

    [Fact]
    public void ParseCandidates_clean_two_part_primary_with_reversed_alt()
    {
        var (primary, alts) = RadioStreamTitle.ParseCandidates("MuryokuP - Sacred Secret");
        Assert.Equal("MuryokuP", primary.Artist);
        Assert.Equal("Sacred Secret", primary.Title);
        Assert.True(Has(alts, "Sacred Secret", "MuryokuP")); // reversed, for catalog validation
    }
}

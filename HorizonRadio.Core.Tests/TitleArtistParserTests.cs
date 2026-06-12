using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The heuristic title/artist splitter — the deterministic normalizer that the
/// (future) local model only has to improve on for the hard cases.
/// </summary>
public class TitleArtistParserTests
{
    [Theory]
    [InlineData("Rick Astley - Never Gonna Give You Up (Official Music Video)", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Daft Punk - Get Lucky [Official Audio]", "Daft Punk", "Get Lucky")]
    [InlineData("Adele – Hello", "Adele", "Hello")]            // en dash
    [InlineData("deadmau5 — Strobe", "deadmau5", "Strobe")]    // em dash
    public void Splits_artist_dash_title_and_strips_noise(string raw, string artist, string title)
    {
        var p = TitleArtistParser.Parse(raw);
        Assert.Equal(artist, p.Artist);
        Assert.Equal(title, p.Title);
        Assert.Equal(ParseConfidence.High, p.Confidence);
    }

    [Fact]
    public void Topic_channel_is_the_artist()
    {
        var p = TitleArtistParser.Parse("Strobe", uploader: "deadmau5 - Topic");
        Assert.Equal("deadmau5", p.Artist);
        Assert.Equal("Strobe", p.Title);
        Assert.Equal(ParseConfidence.High, p.Confidence);
    }

    [Fact]
    public void Japanese_bracket_title()
    {
        var p = TitleArtistParser.Parse("YOASOBI「アイドル」");
        Assert.Equal("YOASOBI", p.Artist);
        Assert.Equal("アイドル", p.Title);
    }

    [Fact]
    public void No_separator_uses_channel_as_low_confidence_artist()
    {
        var p = TitleArtistParser.Parse("Some Cool Track", uploader: "CoolChannel");
        Assert.Equal("Some Cool Track", p.Title);
        Assert.Equal("CoolChannel", p.Artist);
        Assert.Equal(ParseConfidence.Low, p.Confidence);
    }

    [Fact]
    public void Ambiguous_multi_separator_is_medium_confidence()
    {
        var p = TitleArtistParser.Parse("Artist - Song - Remix");
        Assert.Equal("Artist", p.Artist);
        Assert.Equal("Song - Remix", p.Title);
        Assert.Equal(ParseConfidence.Medium, p.Confidence);
    }

    [Fact]
    public void Vevo_suffix_stripped_from_channel_fallback()
    {
        var p = TitleArtistParser.Parse("Untitled", uploader: "TaylorSwiftVEVO");
        Assert.Equal("TaylorSwift", p.Artist);
    }

    [Fact]
    public void Empty_title_is_low_confidence_blank()
    {
        var p = TitleArtistParser.Parse("");
        Assert.Equal("", p.Title);
        Assert.Null(p.Artist);
        Assert.Equal(ParseConfidence.Low, p.Confidence);
    }
}

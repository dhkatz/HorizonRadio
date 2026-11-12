using HorizonRadio.Metadata.VocaDb;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Lyrics from the Vocaloid Lyrics Wiki, for songs whose VocaDB entry has none ("Blurry Star"):
/// a page found by the VocaDB song it links, its lyrics table read column by column.
/// </summary>
public class VocaloidLyricsWikiTests
{
    private const string Page = """
        {{Infobox Song
        |songtitle = "'''KiLLER LADY'''"
        |producer = [[HachiojiP]]
        }}

        ==Lyrics==
        {{lyrics toggle|jp:Japanese|rom:Romaji|eng:English}}
        {| {{lyrics table class}}
        |- class="lyrics-table-header"
        ! {{lyrics header}}
        |-
        |くだらない{{ruby|戯言|たわごと}}　付き合いきれないわ
        |kudaranai tawagoto tsukiai kirenai wa
        |I've had enough of your ''absurd'' nonsense.
        |-
        |<br />
        |-
        |あたしが言いたいこと
        |atashi ga iitai koto
        |Understand what I [[want]] to tell you,
        |-
        | colspan="3" |Ah!
        |}
        {{Translator|RandomAir BGM}}

        ==External Links==
        *{{VDB|S/1399|VocaDB}}
        """;

    [Fact]
    public void Reads_each_column_as_a_version()
    {
        var lyrics = VocaloidLyricsWiki.Parse(Page)!;

        Assert.Equal(["くだらない戯言　付き合いきれないわ", "", "あたしが言いたいこと", "Ah!"], lyrics.Lines.Select(l => l.Text));
        Assert.Equal(["Romaji", "English"], lyrics.Variants!.Select(v => v.Label));
        Assert.Equal(["I've had enough of your absurd nonsense.", "", "Understand what I want to tell you,", "Ah!"],
            lyrics.Variants![1].Lines.Select(l => l.Text));
        Assert.Equal("Lyrics from Vocaloid Lyrics Wiki (CC BY-SA 4.0) · translated by RandomAir BGM", lyrics.Credit);
        Assert.False(lyrics.Synced);
    }

    [Fact]
    public void Two_columns_without_a_translator()
    {
        var lyrics = VocaloidLyricsWiki.Parse("""
            ==Lyrics==
            {{lyrics toggle|jp:Japanese|rom:Romaji}}
            {| {{lyrics table class}}
            |-
            |光散らばる夜
            |hikari chirabaru yoru
            |}
            """)!;

        Assert.Equal("光散らばる夜", lyrics.Lines[0].Text);
        Assert.Equal("Romaji", Assert.Single(lyrics.Variants!).Label);
        Assert.Equal("Lyrics from Vocaloid Lyrics Wiki (CC BY-SA 4.0)", lyrics.Credit);
    }

    [Fact]
    public void A_page_without_a_lyrics_table_has_none() =>
        Assert.Null(VocaloidLyricsWiki.Parse("{{Infobox Song}}\n==Discography==\n*An album"));

    [Theory]
    [InlineData(1399, true)]
    [InlineData(139, false)]   // "S/1399" isn't a link to song 139
    [InlineData(13990, false)]
    public void A_page_is_this_songs_only_by_its_exact_vocadb_link(int id, bool links) =>
        Assert.Equal(links, VocaloidLyricsWiki.LinksSong(Page, id));
}

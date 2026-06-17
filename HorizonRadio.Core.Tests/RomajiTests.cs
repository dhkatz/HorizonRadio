using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The kana romanizer used to verify the cross-script artist bridge: it romanizes hiragana/katakana
/// (returning null on kanji), and judges two romanizations "the same artist" tolerant of
/// Hepburn/Nippon and long-vowel spelling differences.
/// </summary>
public class RomajiTests
{
    [Theory]
    [InlineData("くろくも", "kurokumo")]
    [InlineData("まふまふ", "mafumafu")]
    [InlineData("きょう", "kyou")]          // digraph + long vowel
    [InlineData("カタカナ", "katakana")]     // katakana folds to the same readings
    [InlineData("ミク", "miku")]
    [InlineData("がっこう", "gakkou")]       // sokuon doubles the next consonant
    [InlineData("ラーメン", "raamen")]       // chōonpu repeats the vowel
    public void TryRomanize_reads_kana(string kana, string expected)
        => Assert.Equal(expected, Romaji.TryRomanize(kana));

    [Theory]
    [InlineData("八王子P")]    // kanji (+ Latin) — not purely kana
    [InlineData("文脈")]       // kanji
    [InlineData("HachiojiP")]  // already Latin, no kana
    [InlineData("")]
    public void TryRomanize_returns_null_when_not_purely_kana(string s)
        => Assert.Null(Romaji.TryRomanize(s));

    [Theory]
    [InlineData("Mafumafu", "mafumafu")]   // exact
    [InlineData("Mafumafu", "mafumahu")]   // fu/hu romanization variant
    [InlineData("Kikuo", "kikuo")]
    [InlineData("HachiojiP", "hachioujip")] // long-vowel wobble, same name
    public void SoundsLike_bridges_romanization_variants(string a, string b)
        => Assert.True(Romaji.SoundsLike(a, b));

    [Theory]
    [InlineData("HachiojiP", "kurokumo")]  // the bug: genuinely different artists
    [InlineData("DECO27", "wowaka")]
    [InlineData("Mafumafu", "")]
    public void SoundsLike_rejects_different_names(string a, string b)
        => Assert.False(Romaji.SoundsLike(a, b));
}

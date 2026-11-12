using System.Globalization;
using Avalonia.Media;
using HorizonRadio.Sdk.UI.Theme;
using Xunit;

namespace HorizonRadio.Core.Tests;

public class ThemeToneTests
{
    [Fact]
    public void EveryToneHasItsOwnColor()
    {
        var colors = Enum.GetValues<Tone>().Select(ThemePalette.For).ToList();

        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    [Theory]
    [InlineData(Tone.Neutral)]
    [InlineData(Tone.Success)]
    [InlineData(Tone.Warning)]
    [InlineData(Tone.Danger)]
    [InlineData(Tone.Info)]
    public void TheBrushConverterPaintsTheTonesColor(Tone tone)
    {
        var brush = ToneConverters.Brush.Convert(tone, typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.Equal(ThemePalette.For(tone), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }

    [Theory]
    [InlineData(Tone.Neutral)]
    [InlineData(Tone.Warning)]
    [InlineData(Tone.Danger)]
    public void TheTintIsAFaintWashOfTheTonesColor(Tone tone)
    {
        var brush = ToneConverters.Tint.Convert(tone, typeof(IBrush), null, CultureInfo.InvariantCulture);

        var tint = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        Assert.Equal(ThemePalette.For(tone), tint.Color);
        Assert.Equal(ToneConverters.TintOpacity, tint.Opacity);
    }
}

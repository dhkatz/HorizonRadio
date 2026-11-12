using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace HorizonRadio.Sdk.UI.Theme;

/// <summary>What a status means, for the color that shows it: a view model says what state
/// something is in, and the view picks the color from the theme.</summary>
public enum Tone
{
    /// <summary>Nothing to report: off, not installed, not connected.</summary>
    Neutral,
    /// <summary>Working as it should: connected, installed, up to date.</summary>
    Success,
    /// <summary>Needs attention but works: an update available, a mismatched build.</summary>
    Warning,
    /// <summary>Failed or lost: an error, dropped audio.</summary>
    Danger,
    /// <summary>Informational, no judgement: a second series on a chart.</summary>
    Info,
}

/// <summary>The theme's colors, in one place. The XAML brushes (<c>ToneSuccessBrush</c>…, see
/// <c>Theme/Colors.axaml</c>) are built from these, and code that needs a raw color (chart series)
/// reads them here. Tones read well on both the light and the dark theme, so they don't vary
/// with it.</summary>
public static class ThemePalette
{
    public static readonly Color Neutral = Color.Parse("#6b7280");
    public static readonly Color Success = Color.Parse("#22c55e");
    public static readonly Color Warning = Color.Parse("#f59e0b");
    public static readonly Color Danger = Color.Parse("#ef4444");
    public static readonly Color Info = Color.Parse("#3b82f6");

    /// <summary>Marks what's live or chosen: the song playing, a focused search box.</summary>
    public static readonly Color Highlight = Color.Parse("#4a90f8");

    /// <summary>Text and icons drawn on a tone-colored fill.</summary>
    public static readonly Color OnTone = Colors.White;

    /// <summary>Dims what's behind an overlay (a play button over album art).</summary>
    public static readonly Color Scrim = Color.Parse("#73000000");

    public static Color For(Tone tone) => tone switch
    {
        Tone.Success => Success,
        Tone.Warning => Warning,
        Tone.Danger => Danger,
        Tone.Info => Info,
        _ => Neutral,
    };
}

/// <summary>Converters from a <see cref="Tone"/> to what draws it.</summary>
public static class ToneConverters
{
    private static readonly IImmutableSolidColorBrush[] Brushes =
        [.. Enum.GetValues<Tone>().Select(t => new ImmutableSolidColorBrush(ThemePalette.For(t)))];

    /// <summary>The tone's brush: <c>Background="{Binding Status, Converter={x:Static theme:ToneConverters.Brush}}"</c>.</summary>
    public static readonly IValueConverter Brush =
        new FuncValueConverter<Tone, IBrush>(tone => Brushes[(int)tone]);

    private static readonly IImmutableSolidColorBrush[] Tints =
        [.. Enum.GetValues<Tone>().Select(t => new ImmutableSolidColorBrush(ThemePalette.For(t), TintOpacity))];

    /// <summary>How strongly a tint shows its tone: enough to tell a warning from a note at a
    /// glance, faint enough that the text on it reads as ordinary text.</summary>
    public const double TintOpacity = 0.12;

    /// <summary>A faint wash of the tone, for an area that carries it (a notice's band):
    /// <c>Background="{Binding Tone, Converter={x:Static theme:ToneConverters.Tint}}"</c>.</summary>
    public static readonly IValueConverter Tint =
        new FuncValueConverter<Tone, IBrush>(tone => Tints[(int)tone]);

    /// <summary>Success when true, neutral when false: <c>Tone="{Binding IsConnected, Converter={x:Static theme:ToneConverters.SuccessWhenTrue}}"</c>.</summary>
    public static readonly IValueConverter SuccessWhenTrue =
        new FuncValueConverter<bool, Tone>(on => on ? Tone.Success : Tone.Neutral);
}

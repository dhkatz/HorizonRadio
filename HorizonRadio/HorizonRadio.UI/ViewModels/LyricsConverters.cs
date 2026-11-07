using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;

namespace HorizonRadio.UI.ViewModels;

/// <summary>Value converters for the lyrics panel.</summary>
public static class LyricsConverters
{
    /// <summary>Backing vocals are smaller than the main line.</summary>
    public static readonly IValueConverter LineSize =
        new FuncValueConverter<bool, double>(background => background ? 16 : 22);

    /// <summary>Backing vocals are italic.</summary>
    public static readonly IValueConverter LineStyle =
        new FuncValueConverter<bool, FontStyle>(background => background ? FontStyle.Italic : FontStyle.Normal);

    /// <summary>A hand over lines that jump to their place in the song when clicked.</summary>
    public static readonly IValueConverter HandCursor =
        new FuncValueConverter<bool, Cursor?>(canSeek => canSeek ? Hand : null);

    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    /// <summary>The space after a karaoke word (each word is its own text, so the line's spaces
    /// become gaps).</summary>
    public static readonly IValueConverter WordGap =
        new FuncValueConverter<bool, Avalonia.Thickness>(space => space ? new Avalonia.Thickness(0, 0, 6, 0) : default);

    /// <summary>The track card spans the whole page (and is centered in it) while there are no
    /// lyrics under it.</summary>
    public static readonly IValueConverter RowSpan =
        new FuncValueConverter<bool, int>(alone => alone ? 2 : 1);

    public static readonly IValueConverter CardAlignment =
        new FuncValueConverter<bool, Avalonia.Layout.VerticalAlignment>(withLyrics =>
            withLyrics ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center);
}

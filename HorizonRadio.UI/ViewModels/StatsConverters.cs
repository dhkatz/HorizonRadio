using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Maps a 0..2× gain value to a 0..200px bar width. The visual midpoint
/// (1.0×) lands at 100px, so a glance tells the user whether the stage
/// is cutting (left of midpoint) or boosting (right of midpoint).
/// </summary>
public sealed class GainToWidthConverter : IValueConverter
{
    public static readonly GainToWidthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is float f)  return Math.Clamp(f, 0.0, 2.0) * 100.0;
        if (value is double d) return Math.Clamp(d, 0.0, 2.0) * 100.0;
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Underrun counter colour: muted when zero, red-warm when any have
/// happened. Just a visual nudge — non-zero underruns mean something
/// did skip, even briefly.
/// </summary>
public sealed class UnderrunBrushConverter : IValueConverter
{
    public static readonly UnderrunBrushConverter Instance = new();

    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9ca3af"));
    private static readonly IBrush HotBrush   = new SolidColorBrush(Color.Parse("#ef4444"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ulong u && u > 0 ? HotBrush : MutedBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

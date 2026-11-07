using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HorizonRadio.UI.Views;

/// <summary>
/// Text that fills from left to right as it's sung: the first <see cref="Fill"/> of its width is
/// drawn at full strength, the rest at <see cref="UnsungOpacity"/>. One word of a karaoke line.
/// </summary>
public sealed class KaraokeText : TextBlock
{
    public static readonly StyledProperty<double> FillProperty =
        AvaloniaProperty.Register<KaraokeText, double>(nameof(Fill), 1.0);

    public static readonly StyledProperty<double> UnsungOpacityProperty =
        AvaloniaProperty.Register<KaraokeText, double>(nameof(UnsungOpacity), 0.4);

    static KaraokeText() => AffectsRender<KaraokeText>(FillProperty, UnsungOpacityProperty);

    // Styled like any TextBlock.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>How much has been sung, 0–1.</summary>
    public double Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double UnsungOpacity
    {
        get => GetValue(UnsungOpacityProperty);
        set => SetValue(UnsungOpacityProperty, value);
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        var fill = Math.Clamp(Fill, 0, 1);
        if (fill >= 1)
        {
            base.RenderTextLayout(context, origin);
            return;
        }

        using (context.PushOpacity(UnsungOpacity)) base.RenderTextLayout(context, origin);
        if (fill <= 0) return;
        using (context.PushClip(new Rect(0, 0, Bounds.Width * fill, Bounds.Height))) base.RenderTextLayout(context, origin);
    }
}

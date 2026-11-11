using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using HorizonRadio.Sdk.UI.Theme;
using Material.Icons;

namespace HorizonRadio.Sdk.UI.Controls;

/// <summary>A message about the state of things, with an icon in its tone's color and optional
/// actions: a setup step to take, a source that failed, a reason something is unavailable.
/// <c>Classes="inline"</c> drops the card around it, for a short line inside other content.</summary>
public class Notice : TemplatedControl
{
    public static readonly StyledProperty<Tone> ToneProperty =
        AvaloniaProperty.Register<Notice, Tone>(nameof(Tone), Tone.Info);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<Notice, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Notice, string?>(nameof(Text));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<Notice, object?>(nameof(Actions));

    public Tone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}

/// <summary>Converters for drawing a <see cref="Tone"/> as an icon.</summary>
public static class ToneIcons
{
    /// <summary>The icon a <see cref="Notice"/> of the tone shows: a warning triangle, an error
    /// circle, a check, an info mark.</summary>
    public static readonly IValueConverter Notice = new FuncValueConverter<Tone, MaterialIconKind>(tone => tone switch
    {
        Tone.Warning => MaterialIconKind.AlertOutline,
        Tone.Danger => MaterialIconKind.AlertCircleOutline,
        Tone.Success => MaterialIconKind.CheckCircleOutline,
        _ => MaterialIconKind.InformationOutline,
    });
}

/// <summary>What a list or page shows when it has nothing in it: an icon, a line saying so, what
/// to do about it, and optional actions.</summary>
public class EmptyState : TemplatedControl
{
    public static readonly StyledProperty<MaterialIconKind> IconProperty =
        AvaloniaProperty.Register<EmptyState, MaterialIconKind>(nameof(Icon), MaterialIconKind.InboxOutline);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Text));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<EmptyState, object?>(nameof(Actions));

    public MaterialIconKind Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}

/// <summary>An icon in a circle of its status's color: a tool that's installed, the mod's state,
/// the audio bridge's health.</summary>
public class StatusAvatar : TemplatedControl
{
    public static readonly StyledProperty<Tone> ToneProperty =
        AvaloniaProperty.Register<StatusAvatar, Tone>(nameof(Tone));

    public static readonly StyledProperty<MaterialIconKind> IconProperty =
        AvaloniaProperty.Register<StatusAvatar, MaterialIconKind>(nameof(Icon), MaterialIconKind.Circle);

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<StatusAvatar, double>(nameof(Size), 38);

    public Tone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public MaterialIconKind Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The circle's diameter; the icon is a little over half of it.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }
}

/// <summary>A small dot in a status's color: connected or not, live or idle.</summary>
public class StatusDot : TemplatedControl
{
    public static readonly StyledProperty<Tone> ToneProperty =
        AvaloniaProperty.Register<StatusDot, Tone>(nameof(Tone));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<StatusDot, double>(nameof(Size), 8);

    public Tone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }
}

/// <summary>Converters between control sizes.</summary>
public static class SizeConverters
{
    /// <summary>The icon size that sits well in a circle of the given diameter (a little over half).</summary>
    public static readonly IValueConverter IconForCircle =
        new FuncValueConverter<double, double>(diameter => Math.Round(diameter * 0.53));
}

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Material.Icons;

namespace HorizonRadio.Sdk.UI.Controls;

/// <summary>A square of album art over a placeholder icon, for when there's no art yet. Given a
/// <see cref="PlayCommand"/>, it shows a play button over the art under the pointer.</summary>
public class ArtTile : TemplatedControl
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ArtTile, IImage?>(nameof(Source));

    public static readonly StyledProperty<MaterialIconKind> IconProperty =
        AvaloniaProperty.Register<ArtTile, MaterialIconKind>(nameof(Icon), MaterialIconKind.MusicNote);

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ArtTile, double>(nameof(Size), 48);

    public static readonly StyledProperty<ICommand?> PlayCommandProperty =
        AvaloniaProperty.Register<ArtTile, ICommand?>(nameof(PlayCommand));

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>What shows when there's no art: a note for a song, a list for a playlist.</summary>
    public MaterialIconKind Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Plays the song; the tile shows a play button when set.</summary>
    public ICommand? PlayCommand
    {
        get => GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }
}

/// <summary>A song in a list: art, title, subtitle and a detail line, with actions that appear under
/// the pointer (or when a button in the row has keyboard focus) and fade the text out from under
/// them. Double-clicking the row plays it. The list attaches the song's right-click menu.</summary>
public class SongRow : TemplatedControl
{
    public static readonly StyledProperty<IImage?> ArtProperty =
        AvaloniaProperty.Register<SongRow, IImage?>(nameof(Art));

    public static readonly StyledProperty<MaterialIconKind> ArtIconProperty =
        AvaloniaProperty.Register<SongRow, MaterialIconKind>(nameof(ArtIcon), MaterialIconKind.MusicNote);

    public static readonly StyledProperty<double> ArtSizeProperty =
        AvaloniaProperty.Register<SongRow, double>(nameof(ArtSize), 48);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SongRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<SongRow, string?>(nameof(Subtitle));

    public static readonly StyledProperty<object?> LeadingProperty =
        AvaloniaProperty.Register<SongRow, object?>(nameof(Leading));

    public static readonly StyledProperty<object?> TitleAdornmentProperty =
        AvaloniaProperty.Register<SongRow, object?>(nameof(TitleAdornment));

    public static readonly StyledProperty<object?> DetailProperty =
        AvaloniaProperty.Register<SongRow, object?>(nameof(Detail));

    public static readonly StyledProperty<object?> TrailingProperty =
        AvaloniaProperty.Register<SongRow, object?>(nameof(Trailing));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<SongRow, object?>(nameof(Actions));

    public static readonly StyledProperty<ICommand?> PlayCommandProperty =
        AvaloniaProperty.Register<SongRow, ICommand?>(nameof(PlayCommand));

    public SongRow()
    {
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    public IImage? Art
    {
        get => GetValue(ArtProperty);
        set => SetValue(ArtProperty, value);
    }

    /// <summary>The placeholder icon while there's no art.</summary>
    public MaterialIconKind ArtIcon
    {
        get => GetValue(ArtIconProperty);
        set => SetValue(ArtIconProperty, value);
    }

    public double ArtSize
    {
        get => GetValue(ArtSizeProperty);
        set => SetValue(ArtSizeProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Before the art: a position number.</summary>
    public object? Leading
    {
        get => GetValue(LeadingProperty);
        set => SetValue(LeadingProperty, value);
    }

    /// <summary>Before the title, on its line: a warning to click.</summary>
    public object? TitleAdornment
    {
        get => GetValue(TitleAdornmentProperty);
        set => SetValue(TitleAdornmentProperty, value);
    }

    /// <summary>Under the subtitle: where it came from and when, a lookup in progress, an error.</summary>
    public object? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>After the text, always shown: which source plays it.</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    /// <summary>Shown over the end of the text under the pointer: play, queue, remove.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>Plays the song: a double-click on the row, or the play button over its art.</summary>
    public ICommand? PlayCommand
    {
        get => GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }

    // A double-click on one of the row's buttons (or its source picker) is two clicks on that
    // control, not a request to play the row.
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source.FindAncestorOfType<ComboBox>(includeSelf: true) is null
            && PlayCommand is { } play && play.CanExecute(null))
        {
            play.Execute(null);
            e.Handled = true;
        }
    }
}

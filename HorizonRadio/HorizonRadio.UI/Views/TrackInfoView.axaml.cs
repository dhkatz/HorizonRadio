using Avalonia;
using Avalonia.Controls;

namespace HorizonRadio.UI.Views;

/// <summary>
/// Album art + title/artist (+ album) for the current track, bound to a
/// <see cref="ViewModels.NowPlayingViewModel"/>. Shared by the Dashboard (large)
/// and the player bar (compact) — toggle via <see cref="Compact"/>.
/// </summary>
public partial class TrackInfoView : UserControl
{
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<TrackInfoView, bool>(nameof(Compact));

    /// <summary>When true, renders the small horizontal layout for the player
    /// bar; otherwise the large dashboard layout.</summary>
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public TrackInfoView()
    {
        InitializeComponent();
    }
}

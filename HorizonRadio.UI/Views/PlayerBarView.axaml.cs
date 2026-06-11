using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

/// <summary>
/// Persistent bottom player bar shown across every tab: track info (left),
/// transport + seek bar (center), source/output pickers + volume (right).
/// Bound to the shared <see cref="NowPlayingViewModel"/>.
/// </summary>
public partial class PlayerBarView : UserControl
{
    public PlayerBarView()
    {
        InitializeComponent();

        // Listen on the seek slider with handledEventsToo so we still hear the
        // press/release even though the Slider/Thumb mark them handled. Press
        // suppresses the position poll (so the thumb tracks the drag); release
        // commits the seek.
        var seek = this.FindControl<Slider>("SeekSlider");
        if (seek != null)
        {
            seek.AddHandler(PointerPressedEvent, OnSeekPressed,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            seek.AddHandler(PointerReleasedEvent, OnSeekReleased,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            // If the pointer capture is lost mid-drag (window deactivates, etc.)
            // the release may never fire; commit/clear the seek so the bar
            // doesn't get stuck frozen with _isSeeking left true.
            seek.AddHandler(PointerCaptureLostEvent, OnSeekCaptureLost,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private NowPlayingViewModel? Vm => DataContext as NowPlayingViewModel;

    private void OnSeekPressed(object? sender, PointerPressedEventArgs e) => Vm?.BeginSeek();

    private void OnSeekReleased(object? sender, PointerReleasedEventArgs e) => Vm?.EndSeek();

    private void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Vm?.EndSeek();

    // Close the picker flyout once a selection is tapped. Tapped (not
    // SelectionChanged) so opening the flyout — which sets the bound
    // SelectedItem — doesn't immediately dismiss it.
    private void OnSourceTapped(object? sender, TappedEventArgs e) => HideFlyout("SourceButton");

    private void OnOutputTapped(object? sender, TappedEventArgs e) => HideFlyout("OutputButton");

    private void HideFlyout(string buttonName) =>
        this.FindControl<Button>(buttonName)?.Flyout?.Hide();
}

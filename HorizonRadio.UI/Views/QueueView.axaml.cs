using Avalonia.Controls;
using Avalonia.Input;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

/// <summary>
/// The toggleable right-hand queue sidebar. Bound to <see cref="QueueViewModel"/>.
/// Handles the row interactions that don't belong in the VM: the + source flyout,
/// and double-click / thumbnail-click to play. (Drag-to-reorder is a follow-up; the
/// model side, <c>QueueModel.MoveExplicitTo</c> / <see cref="QueueViewModel.ReorderTo"/>,
/// is already in place for it.)
/// </summary>
public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }

    // Close the source flyout once a source is tapped (Tapped, not SelectionChanged,
    // so opening the flyout — which sets the bound selection — doesn't dismiss it).
    private void OnAddTapped(object? sender, TappedEventArgs e) =>
        this.FindControl<Button>("AddButton")?.Flyout?.Hide();

    // Double-click a queue row to play it now (Spotify-style).
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: QueueRowViewModel row } && row.PlayNowCommand.CanExecute(null))
            row.PlayNowCommand.Execute(null);
    }

    // Click the thumbnail's play overlay to play now.
    private void OnPlayOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: QueueRowViewModel row } && row.PlayNowCommand.CanExecute(null))
            row.PlayNowCommand.Execute(null);
        e.Handled = true;
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

/// <summary>
/// The toggleable right-hand queue sidebar. Bound to <see cref="QueueViewModel"/>.
/// Handles the + source flyout; rows play on double-click or their art's play button
/// (<c>SongRow</c>). (Drag-to-reorder is a follow-up; the model side,
/// <c>QueueModel.MoveExplicitTo</c> / <see cref="QueueViewModel.ReorderTo"/>, is already in
/// place for it.)
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
}

using Avalonia.Controls;
using Avalonia.Input;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

/// <summary>
/// The toggleable right-hand queue sidebar. Bound to <see cref="ViewModels.QueueViewModel"/>;
/// the + button's source flyout is closed on tap (the same pattern as the player
/// bar's pickers) once a source is chosen to add a one-off.
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
        if (sender is Control { DataContext: QueueRowViewModel row } &&
            row.PlayNowCommand.CanExecute(null))
            row.PlayNowCommand.Execute(null);
    }
}

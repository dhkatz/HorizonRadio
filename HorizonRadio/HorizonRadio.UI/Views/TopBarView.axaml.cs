using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

/// <summary>
/// The application top bar (unified search). Bound to <see cref="TopBarViewModel"/>.
/// Code-behind covers the keyboard/focus bits that don't belong in the VM: Enter
/// submits to the full search page, and refocusing the box re-opens the dropdown when
/// there are still live results to show.
/// </summary>
public partial class TopBarView : UserControl
{
    public TopBarView()
    {
        InitializeComponent();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TopBarViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            if (vm.SubmitCommand.CanExecute(null)) vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (vm.ClearCommand.CanExecute(null)) vm.ClearCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Refocusing a non-empty box (e.g. after a light-dismiss) re-opens the dropdown so
    // the last results come back without retyping.
    private void OnSearchGotFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TopBarViewModel { LiveResults.Count: > 0 } vm)
            vm.IsDropdownOpen = true;
    }
}

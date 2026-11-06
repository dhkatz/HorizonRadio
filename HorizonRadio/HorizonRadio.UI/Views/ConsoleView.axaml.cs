using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? _vm;

    public ConsoleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.LinesAppended -= ScrollToEnd;
        _vm = DataContext as ConsoleViewModel;
        if (_vm != null) _vm.LinesAppended += ScrollToEnd;
    }

    // Autoscroll: jump to the newest line after a flush. Guarded by the
    // VM only raising the event when AutoScroll is on.
    private void ScrollToEnd()
    {
        var items = LogList.ItemCount;
        if (items > 0)
            LogList.ScrollIntoView(items - 1);
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(vm.BuildCopyText());
    }
}

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using HorizonRadio.UI.ViewModels;

namespace HorizonRadio.UI.Views;

public partial class LyricsView : UserControl
{
    // After the user scrolls the lyrics themselves, leave the scroll position alone for a while.
    private static readonly TimeSpan HandsOff = TimeSpan.FromSeconds(4);

    private LyricsViewModel? _vm;
    private DateTime _userScrolledAt = DateTime.MinValue;

    public LyricsView()
    {
        InitializeComponent();
        Scroller.AddHandler(PointerWheelChangedEvent, (_, _) => _userScrolledAt = DateTime.UtcNow,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as LyricsViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(LyricsViewModel.ActiveIndex)) CenterLine(_vm.ActiveIndex);
        else if (e.PropertyName == nameof(LyricsViewModel.HasLines) && !_vm.HasLines) Scroller.Offset = default;
    }

    // Keep the line being sung in the middle of the panel.
    private void CenterLine(int index)
    {
        if (index < 0 || DateTime.UtcNow - _userScrolledAt < HandsOff) return;
        if (LinesList.ContainerFromIndex(index) is not Control line) return;

        var top = line.TranslatePoint(new Point(0, 0), LinesList);
        if (top == null) return;
        var target = top.Value.Y + line.Bounds.Height / 2 - Scroller.Viewport.Height / 2;
        var max = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        Scroller.Offset = new Vector(0, Math.Clamp(target, 0, max));
    }

    private void OnLineTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: LyricLineViewModel line }) _vm?.SeekToCommand.Execute(line);
    }
}

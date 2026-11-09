using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.UI.ViewModels;
using Material.Icons;
using Material.Icons.Avalonia;

namespace HorizonRadio.UI.Views;

/// <summary>
/// The song menus, attached in XAML to anything that shows a song:
/// <list type="bullet">
/// <item><c>SongMenu.Target</c> on a row or card: right-click opens Add To Queue and Add To Mix ▸
/// (each mix, then New Mix…).</item>
/// <item><c>SongMenu.MixPicker</c> on a button: clicking it opens the mix list.</item>
/// </list>
/// The menus are built when they open, so they always list the current mixes.
/// </summary>
public static class SongMenu
{
    public static readonly AttachedProperty<SongTarget?> TargetProperty =
        AvaloniaProperty.RegisterAttached<Control, SongTarget?>("Target", typeof(SongMenu));

    public static readonly AttachedProperty<SongTarget?> MixPickerProperty =
        AvaloniaProperty.RegisterAttached<Button, SongTarget?>("MixPicker", typeof(SongMenu));

    static SongMenu()
    {
        // Class handlers see every control once, so nothing is subscribed per row. A handled
        // request isn't passed on, so the innermost song under the pointer wins.
        Control.ContextRequestedEvent.AddClassHandler<Control>(OnContextRequested);
        Button.ClickEvent.AddClassHandler<Button>(OnButtonClick);
    }

    public static SongTarget? GetTarget(Control c) => c.GetValue(TargetProperty);
    public static void SetTarget(Control c, SongTarget? value) => c.SetValue(TargetProperty, value);
    public static SongTarget? GetMixPicker(Button b) => b.GetValue(MixPickerProperty);
    public static void SetMixPicker(Button b, SongTarget? value) => b.SetValue(MixPickerProperty, value);

    private static void OnContextRequested(Control control, ContextRequestedEventArgs e)
    {
        if (GetTarget(control) is not { } target) return;
        e.Handled = true;

        var mixes = new MenuItem { Header = "Add To Mix", Icon = Icon(MaterialIconKind.PlaylistPlus) };
        foreach (var item in MixItems(target)) mixes.Items.Add(item);

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem
        {
            Header = "Add To Queue",
            Icon = Icon(MaterialIconKind.PlaylistPlay),
            Command = target.AddToQueueCommand,
        });
        menu.Items.Add(mixes);
        menu.ShowAt(control, showAtPointer: e.TryGetPosition(control, out _));
    }

    private static void OnButtonClick(Button button, RoutedEventArgs e)
    {
        if (GetMixPicker(button) is not { } target) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var item in MixItems(target)) menu.Items.Add(item);
        menu.ShowAt(button);
    }

    // Each mix (checked and disabled when the song's already in it), then New Mix….
    private static IEnumerable<Control> MixItems(SongTarget target)
    {
        var actions = target.Actions;
        var song = target.Song;
        foreach (var mix in actions.Mixes)
        {
            var added = SongActions.IsIn(song, mix);
            yield return new MenuItem
            {
                // A TextBlock, not a string: a string header reads "_" as an access key.
                Header = new TextBlock { Text = mix.Name },
                Icon = added ? Icon(MaterialIconKind.Check) : null,
                IsEnabled = !added,
                Command = new AsyncRelayCommand(() => actions.AddToMixAsync(song, mix)),
            };
        }
        if (actions.Mixes.Count > 0) yield return new Separator();
        yield return new MenuItem
        {
            Header = "New Mix…",
            Icon = Icon(MaterialIconKind.Plus),
            Command = new AsyncRelayCommand(() => actions.NewMixAsync(song)),
        };
    }

    private static MaterialIcon Icon(MaterialIconKind kind) => new() { Kind = kind, Width = 16, Height = 16 };
}

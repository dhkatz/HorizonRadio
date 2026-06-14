using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;

namespace HorizonRadio.UI.Views;

public partial class MainWindow : ShadUI.Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Spotify-style single top bar: inject the unified search box into the centre of
    // ShadUI's title bar rather than adding a second bar below it. ShadUI's title-bar
    // row has no centred content slot, so we ride its template — find the named
    // "AppTitlePanel" part (logo + title, docked left) and add the search as the
    // DockPanel's fill child, centred between the logo and the window controls. Done in
    // OnApplyTemplate because the title-bar visual tree only exists once templated, and
    // it re-runs if the template is re-applied (e.g. theme change), so we re-inject.
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (e.NameScope.Find("AppTitlePanel") is not Control { Parent: Panel titleBar }) return;
        if (titleBar.Children.Any(c => c is TopBarView)) return; // already injected

        var search = new TopBarView
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // The title bar sizes to its tallest child; this pads it out so the search
            // pill has breathing room above/below rather than being crammed in (the
            // height Spotify's title bar uses too).
            MinHeight = 48,
        };
        // The title bar's DataContext is this window's (the shell VM); bind to its TopBar.
        search.Bind(StyledElement.DataContextProperty, new Binding("TopBar"));

        // Added last so the DockPanel (LastChildFill) makes it the centre fill; the
        // existing AppTitlePanel falls back to its default left dock.
        titleBar.Children.Add(search);
    }

    private void SwitchTheme_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant =
            Application.Current.ActualThemeVariant == ThemeVariant.Dark
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
    }

    private void ToggleFullscreen_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;
    }
}

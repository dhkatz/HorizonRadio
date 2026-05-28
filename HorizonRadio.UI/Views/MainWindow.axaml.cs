using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace HorizonRadio.UI.Views;

public partial class MainWindow : ShadUI.Window
{
    public MainWindow()
    {
        InitializeComponent();
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

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Sdk.Games;
using Material.Icons;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Sidebar entry + content host for one page contributed by the active game integration (see
/// <see cref="IGameIntegration.Pages"/>). The page's own view model renders through the
/// <see cref="ViewLocator"/>; this wrapper only adds the shell's selection state.
/// </summary>
public sealed partial class GamePageItem(GamePage page) : ObservableObject
{
    public string Id => page.Id;

    public string Title => page.Title;

    public object ViewModel => page.ViewModel;

    /// <summary>Sidebar route, distinct from the built-in pages' routes.</summary>
    public string Route => "game-" + page.Id;

    /// <summary>The page's icon, or a generic one if the plugin named an unknown icon.</summary>
    public MaterialIconKind Icon =>
        Enum.TryParse<MaterialIconKind>(page.Icon, ignoreCase: false, out var kind) ? kind : MaterialIconKind.PuzzleOutline;

    /// <summary>True while this page is the visible workspace.</summary>
    [ObservableProperty] private bool isSelected;
}

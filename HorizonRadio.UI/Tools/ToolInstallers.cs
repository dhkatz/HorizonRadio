using System.Linq;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// The tool installers in display order, derived from <see cref="ToolPlugins"/> (the single source
/// of truth). App startup (App.axaml.cs) and the <c>ToolsViewModel</c> design-time constructor both
/// call this, so the Tools tab and the catalog never drift apart.
/// </summary>
public static class ToolInstallers
{
    public static IToolInstaller[] CreateAll() =>
        [.. ToolPlugins.All.Select(p => p.Installer)];
}

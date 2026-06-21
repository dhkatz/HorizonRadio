namespace HorizonRadio.UI.Tools;

/// <summary>
/// The canonical set of tool installers, in display order. Single source
/// of truth: app startup (App.axaml.cs) and the <c>ToolsViewModel</c>
/// design-time constructor both call this instead of hand-listing the
/// installers, so adding a tool is a one-line change here rather than an
/// edit in two places that can silently drift apart.
/// </summary>
public static class ToolInstallers
{
    public static IToolInstaller[] CreateAll() =>
    [
        new YtDlpInstaller(),
        new FfmpegInstaller(),
        new LibrespotInstaller(),
        new TitleModelInstaller(),
    ];
}

using System.Collections.Generic;
using System.Linq;
using HorizonRadio.Core.Tools;
using HorizonRadio.Tools.FFmpeg;
using HorizonRadio.Tools.Librespot;
using HorizonRadio.Tools.TitleModel;
using HorizonRadio.Tools.YtDlp;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// The tool plugins the app ships with, in display order. This is the composition root's single
/// source of truth for tools: startup seeds the tool catalog from each plugin's <c>Descriptor</c>
/// (path resolution + the install registry) and builds the installer list from each <c>Installer</c>.
/// Adding a tool is a one-line change here once its <c>HorizonRadio.Tools.*</c> assembly is referenced.
/// </summary>
public static class ToolPlugins
{
    public static IReadOnlyList<IToolPlugin> All { get; } =
    [
        new YtDlpToolPlugin(),
        new FfmpegToolPlugin(),
        new LibrespotToolPlugin(),
        new TitleModelToolPlugin(),
    ];

    /// <summary>The plugins' tool descriptors, in display order — seeds <c>ToolCatalog</c> at startup.</summary>
    public static IReadOnlyList<ToolDescriptor> Descriptors { get; } = [.. All.Select(p => p.Descriptor)];
}

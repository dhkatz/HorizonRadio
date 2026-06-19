using System.Collections.Generic;
using System.Linq;

namespace HorizonRadio.Core.Tools;

/// <summary>
/// The external tools the app knows how to locate and install, keyed by id. The path resolver
/// (<see cref="ToolsPaths"/>) and the install registry read each tool's file name + data-vs-exe flag
/// from here instead of hardcoding a switch per tool.
///
/// Populated by the composition root via <see cref="Initialize"/>; until then a transitional default
/// list covers the built-in tools so tests and startup work without wiring. The default list goes
/// away once tools are their own plugins and the composition root supplies the descriptors.
/// </summary>
public static class ToolCatalog
{
    private static readonly ToolDescriptor[] Defaults =
    [
        new(ToolKind.YtDlp, "yt-dlp.exe"),
        new(ToolKind.Ffmpeg, "ffmpeg.exe"),
        new(ToolKind.Librespot, "librespot.exe"),
        new(ToolKind.TitleModel, "title-model.gguf", IsData: true),
    ];

    private static IReadOnlyList<ToolDescriptor> _tools = Defaults;

    /// <summary>Register the known tools, in display order. Called once at startup.</summary>
    public static void Initialize(IReadOnlyList<ToolDescriptor> tools) => _tools = tools;

    public static IReadOnlyList<ToolDescriptor> All => _tools;

    public static ToolDescriptor? Find(string id) => _tools.FirstOrDefault(t => t.Id == id);
}

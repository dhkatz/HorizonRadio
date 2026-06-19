using System.Collections.Generic;
using System.Linq;

namespace HorizonRadio.Core.Tools;

/// <summary>
/// The external tools the app knows how to locate and install, keyed by id. The path resolver
/// (<see cref="ToolsPaths"/>) and the install registry read each tool's file name + data-vs-exe flag
/// from here instead of hardcoding a switch per tool.
///
/// Populated by the composition root via <see cref="Initialize"/> from the tool plugins
/// (<c>HorizonRadio.Tools.*</c>); empty until then. Tests seed it the same way the host does.
/// </summary>
public static class ToolCatalog
{
    private static IReadOnlyList<ToolDescriptor> _tools = [];

    /// <summary>Register the known tools, in display order. Called once at startup.</summary>
    public static void Initialize(IReadOnlyList<ToolDescriptor> tools) => _tools = tools;

    public static IReadOnlyList<ToolDescriptor> All => _tools;

    public static ToolDescriptor? Find(string id) => _tools.FirstOrDefault(t => t.Id == id);
}

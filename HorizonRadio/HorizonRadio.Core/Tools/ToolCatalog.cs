using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.IO;
using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Core.Tools;

/// <summary>
/// The external tools the app knows how to find and install, from the tool plugins, in display order,
/// and where they live. A service built from the container: the host's <c>IToolLocator</c> answers
/// plugins from it, and the Tools tab lists and installs from it.
///
/// A tool installed from the Tools tab lands in its own folder under the shared tools root
/// (<c>%LOCALAPPDATA%\HorizonRadio\tools\&lt;kind&gt;\</c>, shared by every profile): one folder per
/// tool, since ffmpeg ships a bundle (ffmpeg.exe, ffprobe.exe, dlls), and so an uninstall can wipe
/// the binaries without touching librespot's login cache. A copy shipped next to the app counts too.
/// A source's own configured path wins when it's set and exists, so installing a tool once makes it
/// usable from every source without copying the path into each one's settings.
/// </summary>
public sealed class ToolCatalog
{
    private readonly string _bundled;

    public ToolCatalog(IEnumerable<IToolPlugin> plugins)
        : this(plugins, Path.Combine(AppPaths.SharedRoot, "tools"), AppContext.BaseDirectory)
    {
    }

    /// <param name="root">Where the Tools tab installs tools.</param>
    /// <param name="bundled">Where the app ships tools (next to it).</param>
    internal ToolCatalog(IEnumerable<IToolPlugin> plugins, string root, string bundled)
    {
        Plugins = plugins.InDisplayOrder();
        All = [.. Plugins.Select(p => p.Descriptor)];
        Root = root;
        _bundled = bundled;
    }

    /// <summary>No tools: the designer's.</summary>
    public static ToolCatalog Empty { get; } = new([]);

    /// <summary>The tool plugins, in display order (the Tools tab's).</summary>
    public IReadOnlyList<IToolPlugin> Plugins { get; }

    /// <summary>The tools' descriptors, in display order.</summary>
    public IReadOnlyList<ToolDescriptor> All { get; }

    /// <summary>Where the Tools tab installs tools: a folder per tool.</summary>
    public string Root { get; }

    public ToolDescriptor? Find(string kind) => All.FirstOrDefault(t => t.Id == kind);

    public string DirectoryFor(string kind) => Path.Combine(Root, kind);

    /// <summary>True for a tool that's a downloaded data file rather than an executable (the title
    /// model's GGUF), per its <see cref="ToolDescriptor"/>.</summary>
    public bool IsModel(string kind) => Find(kind)?.IsData ?? false;

    /// <summary>Where the Tools tab installs <paramref name="kind"/>'s file (an exe, or a model's
    /// data file).</summary>
    /// <exception cref="ArgumentOutOfRangeException">No tool plugin provides <paramref name="kind"/>.</exception>
    public string PathFor(string kind)
    {
        var descriptor = Find(kind)
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown tool kind");
        return Path.Combine(DirectoryFor(kind), descriptor.FileName);
    }

    /// <summary>Where the Tools tab installs <paramref name="kind"/>: its folder and file.</summary>
    public ToolInstallTarget TargetFor(string kind) => new(DirectoryFor(kind), PathFor(kind));

    /// <summary>The path to use for <paramref name="kind"/>: <paramref name="configuredPath"/> when
    /// set and present on disk, otherwise <see cref="Discover"/>. Null when it's nowhere (the caller
    /// says how to install it).</summary>
    public string? Resolve(string? configuredPath, string kind)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;
        return Discover(kind);
    }

    /// <summary>A copy of <paramref name="kind"/> shipped next to the app, else one installed from the
    /// Tools tab; null when there's neither (or no plugin provides it).</summary>
    public string? Discover(string kind)
    {
        if (Find(kind) is not { } descriptor) return null;
        string[] candidates =
        [
            Path.Combine(_bundled, descriptor.FileName), // bundled next to the app
            PathFor(kind),                               // installed from the Tools tab
        ];
        foreach (var c in candidates)
        {
            var resolved = Path.GetFullPath(c);
            if (File.Exists(resolved)) return resolved;
        }
        return null;
    }
}

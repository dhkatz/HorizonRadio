using System;
using System.IO;

namespace HorizonRadio.Core.Tools;

/// <summary>
/// Resolves the effective path to an external tool (ffmpeg, yt-dlp) for a source.
/// Tool paths are stored per-source in config, but a tool installed via the Tools tab
/// is shared by every source — so an explicit per-source path is preferred, and when
/// none is set we fall back to the managed install location (and the app directory for
/// a bundled copy). This mirrors <see cref="Sources.Spotify.Librespot.DiscoverExe"/>
/// and means installing a tool once makes it usable from every source without opening
/// each source's config form to copy the path across.
/// </summary>
public static class ToolResolver
{
    /// <summary>The path to use for <paramref name="kind"/>: the configured path when set
    /// and present on disk, otherwise a discovered managed/bundled copy. Null when the
    /// tool can't be found anywhere — the caller surfaces the "install it" message.</summary>
    public static string? Resolve(string? configuredPath, string kind)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;
        return Discover(kind);
    }

    /// <summary>Test seam: overrides managed/bundled discovery so unit tests are
    /// independent of what's actually installed on the dev machine. Null = real discovery.</summary>
    internal static Func<string, string?>? DiscoverOverride;

    /// <summary>Find a managed (Tools-tab) or app-bundled copy of <paramref name="kind"/>,
    /// independent of any per-source config. Null if none exists.</summary>
    public static string? Discover(string kind)
        => DiscoverOverride is { } over ? over(kind) : DiscoverManaged(kind);

    private static string? DiscoverManaged(string kind)
    {
        // Model-style tools are a data file (GGUF), not an exe.
        var managed = kind == ToolKind.TitleModel ? ToolsPaths.ModelFor(kind) : ToolsPaths.ExeFor(kind);
        var here = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(here, Path.GetFileName(managed)), // bundled next to the app
            managed,                                       // %LOCALAPPDATA%\HorizonRadio\tools\<kind>\
        ];
        foreach (var c in candidates)
        {
            var resolved = Path.GetFullPath(c);
            if (File.Exists(resolved)) return resolved;
        }
        return null;
    }
}

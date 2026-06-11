using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// The build-time tool manifest (<c>tools.manifest.json</c>), embedded
/// into the app as a resource and read offline — the single source of
/// truth for which tool versions this build expects.
///
/// Tools we build ourselves (librespot) are <c>pinned</c>: the manifest
/// carries the blobstore URL and the SHA-256 we expect, so the installer
/// verifies against *our* expectation rather than a source-supplied sums
/// file. Third-party tools (yt-dlp, ffmpeg) are <c>latest</c>: the
/// manifest just records intent; their installers resolve upstream's
/// current build. See docs/tool-provisioning.md.
/// </summary>
public sealed class ToolManifest
{
    public int SchemaVersion { get; init; }
    public Dictionary<string, ToolEntry> Tools { get; init; } = new();

    /// <summary>The runtime identifier we resolve tool platforms against.
    /// Windows-only today; widen when a non-Windows app build lands.</summary>
    public const string CurrentRid = "win-x64";

    public ToolEntry? For(string kind) =>
        Tools.TryGetValue(kind, out var entry) ? entry : null;

    // -- Embedded-resource loading --

    private static readonly Lazy<ToolManifest> Lazy = new(Load);

    /// <summary>The embedded manifest. Throws if the resource is missing
    /// or malformed — that's a build-packaging bug, not a runtime
    /// condition to paper over.</summary>
    public static ToolManifest Current => Lazy.Value;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static ToolManifest Load()
    {
        var asm = Assembly.GetExecutingAssembly();

        // Logical name is "<RootNamespace>.tools.manifest.json", but be
        // tolerant of how the resource path is computed across SDK
        // versions: fall back to any resource ending with the filename.
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("tools.manifest.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Embedded tools.manifest.json not found. Check the csproj <EmbeddedResource> item.");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Could not open embedded resource '{name}'.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        return JsonSerializer.Deserialize<ToolManifest>(json, JsonOpts)
            ?? throw new InvalidOperationException("tools.manifest.json deserialized to null.");
    }
}

/// <summary>One tool's entry in the manifest.</summary>
public sealed class ToolEntry
{
    /// <summary>"pinned" (install from <see cref="ToolPlatform.Url"/> and
    /// verify the hash) or "latest" (installer resolves upstream).</summary>
    public string Policy { get; init; } = "latest";

    /// <summary>The pinned version/rev, for display and the bump
    /// procedure. Null for latest-policy tools.</summary>
    public string? Version { get; init; }

    public Dictionary<string, ToolPlatform> Platforms { get; init; } = new();

    public bool IsPinned =>
        string.Equals(Policy, "pinned", StringComparison.OrdinalIgnoreCase);

    public ToolPlatform? Platform(string rid) =>
        Platforms.TryGetValue(rid, out var p) ? p : null;
}

/// <summary>Per-platform download coordinates for a pinned tool.</summary>
public sealed class ToolPlatform
{
    public string Url { get; init; } = "";

    /// <summary>Expected lowercase-hex SHA-256. Empty/null during the
    /// bootstrap window after a pin bump but before the hash is filled
    /// in — the installer then downloads-and-trusts with a warning.</summary>
    public string? Sha256 { get; init; }
}

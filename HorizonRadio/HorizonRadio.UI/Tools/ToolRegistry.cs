using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.UI.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, List<InstalledTool>> _byKind = new();

    public event Action? Changed;

    /// <summary>Designer ctor: no tools.</summary>
    public ToolRegistry() : this(ToolCatalog.Empty) { }

    public ToolRegistry(ToolCatalog catalog)
    {
        Catalog = catalog;
        Rescan();
    }

    /// <summary>The tools it tracks, and where they're installed.</summary>
    public ToolCatalog Catalog { get; }

    public void Rescan()
    {
        _byKind.Clear();
        foreach (var tool in Catalog.All) ScanKind(tool.Id);
        RaiseChanged();
    }

    public IReadOnlyList<InstalledTool> ForKind(string kind)
        => _byKind.TryGetValue(kind, out var list) ? list : Array.Empty<InstalledTool>();

    public bool IsInstalled(string kind) => ForKind(kind).Count > 0;

    public InstalledTool? PrimaryFor(string kind)
    {
        var installed = ForKind(kind);
        return installed.Count > 0 ? installed[0] : null;
    }

    public void RaiseChanged() => Changed?.Invoke();

    private void ScanKind(string kind)
    {
        var path = Catalog.PathFor(kind);
        if (!File.Exists(path)) return;

        // Model-style tools are a GGUF data file, not an exe — there's no FileVersionInfo to read.
        var version = Catalog.IsModel(kind) ? null : TryReadVersion(path);
        var sha = HashVerification.ReadSidecar(path);
        _byKind[kind] = new List<InstalledTool> { new InstalledTool(kind, path, version, sha) };
    }

    private static string? TryReadVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion;
        }
        catch { return null; }
    }
}

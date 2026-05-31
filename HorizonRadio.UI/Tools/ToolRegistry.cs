using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace HorizonRadio.UI.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, List<InstalledTool>> _byKind = new();

    public event Action? Changed;

    public ToolRegistry() { Rescan(); }

    public void Rescan()
    {
        _byKind.Clear();
        ScanKind(ToolKind.YtDlp);
        ScanKind(ToolKind.Ffmpeg);
        ScanKind(ToolKind.Librespot);
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
        var exe = ToolsPaths.ExeFor(kind);
        if (!File.Exists(exe)) return;

        var version = TryReadVersion(exe);
        var sha = HashVerification.ReadSidecar(exe);
        _byKind[kind] = new List<InstalledTool> { new InstalledTool(kind, exe, version, sha) };
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

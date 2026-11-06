using System;
using System.IO;

namespace HorizonRadio.Core.Tools;

/// <summary>
/// Canonical filesystem locations for downloaded external tools. Lives in
/// Core so both the UI installers and the source factories (which resolve
/// a tool exe at runtime) share one definition instead of hand-mirroring
/// the path string.
///
/// Everything lands under <c>%LOCALAPPDATA%\HorizonRadio\tools\</c> so a
/// clean uninstall can wipe the binaries without touching the existing
/// librespot OAuth cache under <c>HorizonRadio\librespot\</c>.
///
/// One subdirectory per tool — ffmpeg ships a bundle (ffmpeg.exe +
/// ffprobe.exe + dlls), isolating each tool keeps them swappable without
/// leaking stray files into siblings.
/// </summary>
public static class ToolsPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HorizonRadio", "tools");

    public static string DirectoryFor(string kind) => Path.Combine(Root, kind);

    /// <summary>True for tools that are a downloaded data file rather than an executable (e.g. the
    /// title-extraction model's GGUF). Read from the tool's <see cref="ToolDescriptor"/> — the single
    /// source of truth for "is this a model?", so callers branch on this rather than comparing kinds.</summary>
    public static bool IsModel(string kind) => ToolCatalog.Find(kind)?.IsData ?? false;

    /// <summary>The installed-file path for a tool: its <see cref="DirectoryFor">directory</see> plus
    /// the file name from its <see cref="ToolDescriptor"/> (an exe, or a model's data file).</summary>
    public static string PathFor(string kind)
    {
        var descriptor = ToolCatalog.Find(kind)
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown tool kind");
        return Path.Combine(DirectoryFor(kind), descriptor.FileName);
    }

    public static void EnsureDir(string kind) =>
        Directory.CreateDirectory(DirectoryFor(kind));
}

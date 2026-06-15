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

    public static string ExeFor(string kind) => kind switch
    {
        ToolKind.YtDlp => Path.Combine(DirectoryFor(kind), "yt-dlp.exe"),
        ToolKind.Ffmpeg => Path.Combine(DirectoryFor(kind), "ffmpeg.exe"),
        ToolKind.Librespot => Path.Combine(DirectoryFor(kind), "librespot.exe"),
        _ => throw new ArgumentOutOfRangeException(
                                  nameof(kind), kind, "unknown tool kind"),
    };

    /// <summary>The on-disk file for a non-exe tool (a downloaded data file rather than a
    /// program). Currently the title-extraction model's GGUF — <see cref="ExeFor"/> hard-codes
    /// an <c>.exe</c> name, so model-style tools resolve their path here instead.</summary>
    public static string ModelFor(string kind) => kind switch
    {
        ToolKind.TitleModel => Path.Combine(DirectoryFor(kind), "title-model.gguf"),
        _ => throw new ArgumentOutOfRangeException(
                                  nameof(kind), kind, "unknown model tool kind"),
    };

    public static void EnsureDir(string kind) =>
        Directory.CreateDirectory(DirectoryFor(kind));
}

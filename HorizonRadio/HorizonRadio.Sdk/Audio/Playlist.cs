using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HorizonRadio.Sdk.Audio;

/// <summary>
/// Resolves a local path into the ordered list of audio files it stands for — a
/// directory (recursive), an M3U's entries, or a single file. The local content
/// player turns each path into a playable item; ordering and shuffle belong to the
/// queue.
/// </summary>
public static class Playlist
{
    /// <summary>Resolve a path into the ordered list of audio file paths it
    /// stands for — a directory (recursive), an M3U's entries, or a single
    /// file. Empty if nothing resolves.</summary>
    public static IReadOnlyList<string> ResolvePaths(string path)
    {
        var list = new List<string>();
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".m3u" or ".m3u8") LoadM3u(path, list);
            else list.Add(path);
        }
        else if (Directory.Exists(path))
        {
            LoadDirectory(path, list, recursive: true);
        }
        return list;
    }

    private static readonly string[] _audioExt = { ".mp3", ".wav", ".flac", ".ogg" };

    private static void LoadDirectory(string dir, List<string> into, bool recursive)
    {
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var f in Directory.EnumerateFiles(dir, "*", opt)
                                   .Where(f => _audioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                   .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            into.Add(f);
        }
    }

    private static void LoadM3u(string m3u, List<string> into)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(m3u)) ?? ".";
        foreach (var raw in File.ReadAllLines(m3u))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            // http(s) URLs aren't supported yet — the loader is local-files only.
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = Path.IsPathRooted(line) ? line : Path.Combine(baseDir, line);
            if (File.Exists(resolved)) into.Add(resolved);
        }
    }
}

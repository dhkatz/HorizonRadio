using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// Ordered list of audio file paths plus an iteration cursor. Loaders
/// build one from a directory walk or an M3U file; the LocalFileSource
/// reads through it, looping when it reaches the end. Thread-safety
/// isn't built in — callers serialize via the source's own state lock.
/// </summary>
public sealed class Playlist
{
    private readonly List<string> _tracks = new();
    private int _cursor;

    public int Count => _tracks.Count;

    public string? Current => _cursor < _tracks.Count ? _tracks[_cursor] : null;

    /// <summary>Advance and wrap around at the end. Returns the new current track.</summary>
    public string? Next()
    {
        if (_tracks.Count == 0) return null;
        _cursor = (_cursor + 1) % _tracks.Count;
        return _tracks[_cursor];
    }

    /// <summary>Step back and wrap around at the start.</summary>
    public string? Previous()
    {
        if (_tracks.Count == 0) return null;
        _cursor = (_cursor - 1 + _tracks.Count) % _tracks.Count;
        return _tracks[_cursor];
    }

    public void Add(string path) => _tracks.Add(path);

    public void Clear()
    {
        _tracks.Clear();
        _cursor = 0;
    }

    // -- Loaders ---------------------------------------------------------

    /// <summary>Load any one of: an M3U file, a directory (recursive),
    /// or a single audio file. Returns an empty playlist if nothing
    /// resolves.</summary>
    public static Playlist FromPath(string path)
    {
        var p = new Playlist();
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".m3u" or ".m3u8") LoadM3uInto(path, p);
            else                          p.Add(path);
        }
        else if (Directory.Exists(path))
        {
            LoadDirectoryInto(path, p, recursive: true);
        }
        return p;
    }

    private static readonly string[] _audioExt = { ".mp3", ".wav", ".flac", ".ogg" };

    private static void LoadDirectoryInto(string dir, Playlist p, bool recursive)
    {
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var f in Directory.EnumerateFiles(dir, "*", opt)
                                   .Where(f => _audioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                   .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            p.Add(f);
        }
    }

    private static void LoadM3uInto(string m3u, Playlist p)
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
            if (File.Exists(resolved)) p.Add(resolved);
        }
    }
}

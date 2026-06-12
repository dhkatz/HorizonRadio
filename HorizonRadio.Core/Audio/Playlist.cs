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
///
/// Iteration order is delegated to <see cref="PlayOrder"/>, which is the
/// identity sequence until <see cref="SetShuffle"/> flips it to a random
/// permutation.
/// </summary>
public sealed class Playlist
{
    private readonly List<string> _tracks = new();
    private readonly PlayOrder _order = new();

    public int Count => _tracks.Count;

    /// <summary>Whether tracks are currently played in a shuffled order.</summary>
    public bool Shuffle => _order.Shuffled;

    public string? Current
    {
        get
        {
            int i = _order.CurrentIndex;
            return i >= 0 && i < _tracks.Count ? _tracks[i] : null;
        }
    }

    /// <summary>Advance and wrap around at the end. Returns the new current track.</summary>
    public string? Next()
    {
        _order.Advance(wrap: true);
        return Current;
    }

    /// <summary>Step back and wrap around at the start.</summary>
    public string? Previous()
    {
        _order.Retreat(wrap: true);
        return Current;
    }

    /// <summary>Enable/disable shuffle. <paramref name="keepCurrent"/> (default)
    /// keeps the current track playing and shuffles the rest around it; pass
    /// false to fully randomize from the start (e.g. starting shuffled).</summary>
    public void SetShuffle(bool on, bool keepCurrent = true) => _order.SetShuffle(on, keepCurrent);

    public void Add(string path)
    {
        _tracks.Add(path);
        _order.Append();
    }

    public void Clear()
    {
        _tracks.Clear();
        _order.Reset(0);
    }

    // -- Loaders ---------------------------------------------------------

    /// <summary>Load any one of: an M3U file, a directory (recursive),
    /// or a single audio file. Returns an empty playlist if nothing
    /// resolves.</summary>
    public static Playlist FromPath(string path)
    {
        var p = new Playlist();
        foreach (var track in ResolvePaths(path)) p.Add(track);
        return p;
    }

    /// <summary>Resolve a path into the ordered list of audio file paths it
    /// stands for — a directory (recursive), an M3U's entries, or a single
    /// file — without the iteration cursor. The mix engine uses this to expand
    /// a local <c>ContentRef</c> into items while owning ordering itself;
    /// <see cref="FromPath"/> is just this plus a cursor. Empty if nothing
    /// resolves.</summary>
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

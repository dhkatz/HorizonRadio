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
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".m3u" or ".m3u8") LoadM3uInto(path, p);
            else p.Add(path);
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

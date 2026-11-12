using System.Collections.Concurrent;
using System.Diagnostics;

namespace HorizonRadio.Sdk.Sources;

/// <summary>
/// Songs a source downloaded ahead of play, kept on disk so they start at once, can be skipped
/// through, and play again without the network. Each song is a folder named by its key (a YouTube
/// video id), filled by the source: the audio and whatever it keeps about the song. A download goes
/// into a folder of its own and is moved into place only once it's complete, so a song that's here
/// is whole. Downloads of the same song share one run, at most two run at a time (across every
/// cache sharing <c>downloads</c>), and once the cache is over <see cref="MaxBytes"/> the songs
/// used longest ago go first. A source gets its cache from the host (<see cref="IAudioCaches"/>),
/// which keeps one per source.
/// </summary>
public sealed class AudioCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _fetching = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly Lock _trim = new();
    private long _maxBytes = 1L << 30;

    /// <param name="directory">The cache's folder; one cache per folder.</param>
    /// <param name="downloads">Limits downloads at once, shared by every cache that should count
    /// together (the host's are); null for a limit of two of this cache's own.</param>
    public AudioCache(string directory, SemaphoreSlim? downloads = null)
    {
        Directory = directory;
        _slots = downloads ?? new SemaphoreSlim(2);
        // A download the app didn't finish (it closed, or crashed) is never coming.
        TryDeleteDirectory(Incoming);
    }

    public string Directory { get; }

    private string Incoming => Path.Combine(Directory, ".incoming");

    /// <summary>How much to keep. Past it, the songs used longest ago are removed after each download.</summary>
    public long MaxBytes
    {
        get => Interlocked.Read(ref _maxBytes);
        set => Interlocked.Exchange(ref _maxBytes, Math.Max(0, value));
    }

    private static void Log(string message) => Trace.WriteLine($"[hzn-cache] {message}");

    /// <summary>The folder of a song that's here (and marks it used now), or null.</summary>
    public string? Find(string key)
    {
        var folder = FolderFor(key);
        if (!System.IO.Directory.Exists(folder)) return null;
        try { System.IO.Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return folder;
    }

    /// <summary>Whether a download of the song is under way.</summary>
    public bool IsFetching(string key) => _fetching.ContainsKey(Safe(key));

    /// <summary>
    /// The song's folder, downloading it first if it isn't here: <paramref name="download"/> fills
    /// an empty folder, which becomes the song's once it returns. Null when the download failed.
    /// A download already under way for the song is joined, not started again, and runs to its end
    /// even when <paramref name="ct"/> gives up waiting for it.
    /// </summary>
    public Task<string?> FetchAsync(string key, Func<string, CancellationToken, Task> download, CancellationToken ct)
    {
        if (Find(key) is { } here) return Task.FromResult<string?>(here);
        var run = _fetching.GetOrAdd(Safe(key), k => new Lazy<Task<string?>>(() => DownloadAsync(k, download)));
        return run.Value.WaitAsync(ct);
    }

    private async Task<string?> DownloadAsync(string key, Func<string, CancellationToken, Task> download)
    {
        try
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Find(key) is { } here) return here; // finished by a run that just ended
                var incoming = Path.Combine(Incoming, $"{key}-{Guid.NewGuid():N}");
                System.IO.Directory.CreateDirectory(incoming);
                try
                {
                    var started = Stopwatch.StartNew();
                    await download(incoming, CancellationToken.None).ConfigureAwait(false);
                    var folder = FolderFor(key);
                    if (System.IO.Directory.Exists(folder)) return folder;
                    System.IO.Directory.Move(incoming, folder);
                    Log($"{key} downloaded in {started.Elapsed.TotalSeconds:0.0} s ({SizeOf(folder) / 1024} KB)");
                    Trim(keep: key);
                    return folder;
                }
                finally
                {
                    TryDeleteDirectory(incoming);
                }
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception ex)
        {
            Log($"{key} didn't download: {ex.Message}");
            return null;
        }
        finally
        {
            _fetching.TryRemove(key, out _);
        }
    }

    /// <summary>The space the cache takes.</summary>
    public long Size => Songs().Sum(s => s.Bytes);

    /// <summary>Remove every song (one playing now stays: its file is open).</summary>
    public void Clear()
    {
        lock (_trim)
        {
            foreach (var song in Songs()) TryRemove(song.Folder);
        }
    }

    // Remove the songs used longest ago until the cache fits, never `keep` (the one just added).
    private void Trim(string keep)
    {
        lock (_trim)
        {
            var songs = Songs().OrderBy(s => s.Used).ToList();
            var total = songs.Sum(s => s.Bytes);
            var max = MaxBytes;
            foreach (var song in songs)
            {
                if (total <= max) break;
                if (string.Equals(Path.GetFileName(song.Folder), keep, StringComparison.Ordinal)) continue;
                if (TryRemove(song.Folder)) total -= song.Bytes;
            }
        }
    }

    private sealed record Song(string Folder, long Bytes, DateTime Used);

    private List<Song> Songs()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return new DirectoryInfo(Directory).EnumerateDirectories()
            .Where(d => d.Name != ".incoming")
            .Select(d => new Song(d.FullName, SizeOf(d.FullName), d.LastWriteTimeUtc))
            .ToList();
    }

    private string FolderFor(string key) => Path.Combine(Directory, Safe(key));

    // A key as a folder name: anything but letters, digits, '-' and '_' becomes '_'.
    private static string Safe(string key) =>
        string.Concat(key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static long SizeOf(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // Remove a song whole or not at all: it's moved out first, which Windows refuses while a file
    // in it is open (the song is playing), so a song is never left half deleted.
    private bool TryRemove(string folder)
    {
        var gone = Path.Combine(Incoming, $"gone-{Guid.NewGuid():N}");
        try
        {
            System.IO.Directory.CreateDirectory(Incoming);
            System.IO.Directory.Move(folder, gone);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        TryDeleteDirectory(gone);
        return true;
    }

    private static bool TryDeleteDirectory(string folder)
    {
        try
        {
            if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false; // in use (playing)
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

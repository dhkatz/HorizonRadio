using System.Diagnostics;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Party;

/// <summary>
/// Finds a party's song on this PC: from the source it's played from elsewhere when that works here
/// (the same Spotify track, YouTube video or station), else by this PC's search by title and artist.
/// A station is only ever tuned into, not searched for.
/// </summary>
public sealed class PartySongFinder
{
    private readonly Func<IAudioSourceFactory, string, CancellationToken, Task<PlayableItem>> _resolve;
    private readonly Func<PartySong, CancellationToken, Task<IReadOnlyList<PartySource>>> _search;
    private readonly Func<string, IAudioSourceFactory?> _find;

    /// <param name="search">Finds a song on this PC's sources (default: <see cref="SongSources.SearchAsync"/>).</param>
    /// <param name="find">A source by id (default: <see cref="SourceCatalog.Find"/>).</param>
    public PartySongFinder(QueuePlayback queue, Func<PartySong, CancellationToken, Task<IReadOnlyList<PartySource>>>? search = null,
        Func<string, IAudioSourceFactory?>? find = null)
        : this((factory, locator, ct) => queue.ResolveAsync(factory, locator, ct), search, find)
    {
    }

    /// <param name="resolve">What a source here plays for a locator.</param>
    internal PartySongFinder(Func<IAudioSourceFactory, string, CancellationToken, Task<PlayableItem>> resolve,
        Func<PartySong, CancellationToken, Task<IReadOnlyList<PartySource>>>? search = null, Func<string, IAudioSourceFactory?>? find = null)
    {
        _resolve = resolve;
        _search = search ?? SearchAsync;
        _find = find ?? SourceCatalog.Find;
    }

    /// <summary>The song, to play here; null when nothing here has it.</summary>
    /// <exception cref="PartyException">A source here has it but can't play it (its tools aren't
    /// installed, say); the message says why, for the page.</exception>
    public async Task<PlayableItem?> FindAsync(PartySong song, CancellationToken ct)
    {
        var tried = new HashSet<PartySource>();
        var failed = new List<(IAudioSourceFactory Source, Exception Error)>();
        if (await TryAsync(song, song.Sources, tried, failed, ct).ConfigureAwait(false) is { } item) return item;
        if (!song.Live) // a station is tuned into, not found by name
        {
            var found = await _search(song, ct).ConfigureAwait(false);
            if (await TryAsync(song, found, tried, failed, ct).ConfigureAwait(false) is { } other) return other;
        }
        if (failed.Count == 0) return null; // nothing here has it
        throw new PartyException(WhyNot(song, failed));
    }

    // A source here had the song but couldn't play it. A missing tool is the one the user can fix.
    private static string WhyNot(PartySong song, List<(IAudioSourceFactory Source, Exception Error)> failed)
    {
        foreach (var (source, error) in failed)
        {
            if (error is not MissingToolException { ToolKinds: { Count: > 0 } tools }) continue;
            return $"Can't play \"{song.Title}\" here: {source.DisplayName} needs {SourceRequirements.HumanJoin(tools)}. " +
                   $"Install {(tools.Count > 1 ? "them" : "it")} from the Tools tab.";
        }
        return $"Couldn't play \"{song.Title}\" from {failed[0].Source.DisplayName} here.";
    }

    private async Task<PlayableItem?> TryAsync(PartySong song, IEnumerable<PartySource> sources, HashSet<PartySource> tried,
        List<(IAudioSourceFactory, Exception)> failed, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            if (!tried.Add(source) || _find(source.SourceId) is not IContentSourceFactory factory) continue;
            if (source.SourceId == "local" && !File.Exists(source.Locator)) continue; // someone else's file, not ours
            try
            {
                var item = await _resolve(factory, source.Locator, ct).ConfigureAwait(false);
                Log($"\"{song.Title}\" from {source.SourceId}");
                return item;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"\"{song.Title}\" from {source.SourceId} didn't play: {ex.Message}");
                failed.Add((factory, ex));
            }
        }
        return null;
    }

    private static async Task<IReadOnlyList<PartySource>> SearchAsync(PartySong song, CancellationToken ct)
    {
        var (found, _) = await SongSources.SearchAsync(new Track(song.Title, song.Artist, null, null, "", ""), ct).ConfigureAwait(false);
        return [.. found.Select(s => new PartySource(s.SourceId, s.Locator))];
    }

    private static void Log(string message) => Trace.WriteLine($"[hzn-party] {message}");
}

/// <summary>
/// A party's song in a queue before it has been found here: the host's song in a guest's copy of the
/// party's queue, or a guest's song added to the host's. It's found (<see cref="PartySongFinder"/>)
/// the first time it's loaded ahead, looked up for the queue's list, or played, once however many
/// ask, and then plays as what was found. Not finding it fails that one song, not the queue.
/// </summary>
public sealed class PartySongItem : PlayableItem
{
    private readonly PartySongFinder _finder;
    private readonly Lock _lock = new();
    private Task<PlayableItem?>? _finding;
    private volatile PlayableItem? _found;

    public PartySongItem(PartySong song, PartySongFinder finder)
    {
        Song = song;
        _finder = finder;
        Metadata = new Track(song.Title, song.Artist, null, null, "", "");
        Duration = song.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
    }

    /// <summary>The song as the party told it.</summary>
    public PartySong Song { get; }

    /// <summary>What it plays here, once found.</summary>
    public PlayableItem? Found => _found;

    /// <summary>Find it here, or wait for the search under way; null when nothing here has it. The
    /// search runs to its end even if the caller stops waiting. A search that threw is tried again
    /// next time (the tool it lacked may be installed by then).</summary>
    /// <exception cref="PartyException">A source here has it but can't play it.</exception>
    public async Task<PlayableItem?> FindAsync(CancellationToken ct)
    {
        Task<PlayableItem?> task;
        lock (_lock) task = _finding ??= Task.Run(() => _finder.FindAsync(Song, CancellationToken.None), CancellationToken.None);
        try
        {
            var found = await task.WaitAsync(ct).ConfigureAwait(false);
            if (found != null && _found == null)
            {
                _found = found;
                TakeFrom(found);
            }
            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_finding, task)) _finding = null;
            }
            throw;
        }
    }

    private async Task<PlayableItem> FoundAsync(CancellationToken ct) =>
        await FindAsync(ct).ConfigureAwait(false)
        ?? throw new PartyException($"Couldn't find \"{Song.Title}\"{(Song.Artist.Length > 0 ? " by " + Song.Artist : "")} here.");

    private void TakeFrom(PlayableItem found)
    {
        Metadata = found.Metadata;
        Duration = found.Duration ?? Duration;
    }

    public override TimeSpan Position => _found?.Position ?? TimeSpan.Zero;

    public override TimeSpan? SongPosition => _found is { } found ? found.SongPosition : base.SongPosition;

    public override ContentRef? LiveOrigin =>
        _found?.LiveOrigin ?? (Song.Live && Song.Sources.Count > 0 ? new ContentRef(Song.Sources[0].SourceId, Song.Sources[0].Locator) : null);

    public override bool CanSeek => _found?.CanSeek ?? false;

    public override void Seek(TimeSpan position) => _found?.Seek(position);

    public override async Task PrepareAsync(CancellationToken ct)
    {
        var found = await FoundAsync(ct).ConfigureAwait(false);
        await found.PrepareAsync(ct).ConfigureAwait(false);
        TakeFrom(found);
    }

    public override async Task PrefetchAsync(CancellationToken ct)
    {
        if (await FindAsync(ct).ConfigureAwait(false) is { } found) await found.PrefetchAsync(ct).ConfigureAwait(false);
    }

    public override async Task<Track?> TryGetMetadataAsync(CancellationToken ct)
    {
        PlayableItem? found;
        try
        {
            found = await FindAsync(ct).ConfigureAwait(false);
        }
        catch (PartyException)
        {
            return null;
        }
        if (found == null) return null;
        return await found.TryGetMetadataAsync(ct).ConfigureAwait(false) ?? found.Metadata;
    }

    public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
    {
        var found = await FoundAsync(ct).ConfigureAwait(false);
        // The engine sees this item: what was found reports through it.
        var start = ctx.StartPosition;
        await found.PlayAsync(new PumpContext
        {
            Sink = ctx.Sink,
            IsPaused = ctx.IsPaused,
            ResumeGate = ctx.ResumeGate,
            OnStarted = _ =>
            {
                TakeFrom(found);
                ctx.OnStarted?.Invoke(this);
            },
            OnMetadataUpdated = _ =>
            {
                TakeFrom(found);
                ctx.OnMetadataUpdated?.Invoke(this);
            },
            StartPosition = start is null ? null : _ => start(this),
        }, ct).ConfigureAwait(false);
    }
}

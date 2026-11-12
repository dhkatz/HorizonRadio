using System.Diagnostics;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Party;

/// <summary>
/// The host's player, as a party sees it. On the queue, a song is the queued item (so playing it again
/// is a new song for the guests), a station is its stream, and a song counts as paused until it has
/// started, so guests wait for it rather than run ahead while it loads. Between songs nothing is
/// playing (not the last song, paused, which would pause the guests). On a self-driven source
/// (Spotify Connect) it's the track that source reports.
///
/// The queue is the party's: guests see it and change it as the host can. A song a guest adds is
/// found on this PC when it's loaded ahead or played (<see cref="PartySongItem"/>).
/// </summary>
public sealed class PlayerPartySource : IPartyHostSource, IDisposable
{
    // Guests are shown this much of the queue (a message has to stay small).
    private const int MaxQueued = 100;
    private const int MaxUpcoming = 10;

    private readonly SourceRunner _runner;
    private readonly QueuePlayback _queue;
    private readonly PartySongFinder _finder;
    private readonly Lock _lock = new();
    private volatile Track? _track;
    private ITransportControls? _transport;

    /// <param name="finder">Finds the songs guests add (default: on this PC's sources).</param>
    public PlayerPartySource(SourceRunner runner, QueuePlayback queue, PartySongFinder? finder = null)
    {
        _runner = runner;
        _queue = queue;
        _finder = finder ?? new PartySongFinder(queue);
        _runner.TrackChanged += OnTrackChanged;
        _runner.ActiveSourceChanged += OnActiveSourceChanged;
        _queue.Model.Changed += Raise;
        Rebind();
    }

    public event Action? Changed;

    public PartyNow? Now
    {
        get
        {
            if (_runner.ActiveSource is not { } active) return null;
            var paused = (active as ITransportControls)?.IsPaused ?? false;

            if (_queue.ActiveEngine is { } engine)
            {
                if (engine.PlayingNow is not var (item, started)) return null;
                var meta = item.Metadata;
                if (item.Item.LiveOrigin is { } station)
                {
                    var title = meta.Resolvable && meta.Title.Length > 0 ? meta.Title : station.DisplayName ?? "Radio";
                    return new PartyNow(
                        new PartySong($"live:{station.SourceId}:{station.Locator}", title, meta.Resolvable ? meta.Artist : "", null, true,
                            [new PartySource(station.SourceId, station.Locator)]),
                        null, paused);
                }
                return new PartyNow(PartySongs.Of(item.Item, item.Id), started ? item.Item.Position : null, paused || !started);
            }

            if (_track is not { Resolvable: true } track || string.IsNullOrWhiteSpace(track.Title)) return null;
            var progress = active as IPlaybackProgress;
            return new PartyNow(
                new PartySong(track.ExternalId ?? $"{track.SourceId}|{track.Artist}|{track.Title}", track.Title, track.Artist,
                    Ms(progress?.Duration), false, SourcesOf(track)),
                progress?.Duration != null ? progress.Position : null, paused);
        }
    }

    public PartyQueueList Queue
    {
        get
        {
            var snap = _queue.Model.Snapshot(MaxUpcoming);
            return new PartyQueueList(
                [.. snap.Explicit.Take(MaxQueued).Select(q => PartySongs.Of(q.Item, q.Id))],
                snap.ContextName,
                [.. snap.ContextPeek.Select(p => new PartyUpcoming(p.Title, p.Subtitle, p.IsPlaceholder,
                    p.Origin is { } o ? new PartySource(o.SourceId, o.Locator) : null))]);
        }
    }

    public async Task ChangeQueueAsync(PartyQueueChange change)
    {
        var model = _queue.Model;
        switch (change.Op)
        {
            case PartyQueueChange.Add when change.Songs is { Count: > 0 } songs:
                model.AppendExplicit(songs.Select(s => new PartySongItem(s, _finder)), change.PlayNow);
                await _queue.EnsureActiveAsync().ConfigureAwait(false);
                break;
            case PartyQueueChange.Remove when change.Id is { } id:
                model.RemoveExplicit(id);
                break;
            case PartyQueueChange.MoveTo when change.Id is { } id && change.To is { } to:
                model.MoveExplicitTo(id, to);
                break;
            case PartyQueueChange.Move when change.Id is { } id:
                model.MoveExplicit(id, change.By);
                break;
            case PartyQueueChange.Play when change.Id is { } id:
                model.JumpToExplicit(id);
                await _queue.EnsureActiveAsync().ConfigureAwait(false);
                break;
            case PartyQueueChange.Clear:
                model.ClearExplicit();
                break;
            case PartyQueueChange.Skip when _runner.ActiveSource is ITransportControls { CanSkipNext: true } transport:
                await transport.NextAsync().ConfigureAwait(false);
                break;
            case PartyQueueChange.Back when _runner.ActiveSource is ITransportControls { CanSkipPrevious: true } transport:
                await transport.PreviousAsync().ConfigureAwait(false);
                break;
        }
    }

    // Where the song can be played again: its own origin (a Spotify uri, a YouTube video, a file).
    private static PartySource[] SourcesOf(Track track) => PartySongs.SourcesOf(track);

    private static long? Ms(TimeSpan? duration) => duration is { } d ? (long)d.TotalMilliseconds : null;

    private void OnTrackChanged(Track track)
    {
        _track = track;
        Raise();
    }

    private void OnActiveSourceChanged(IAudioSourceFactory? factory)
    {
        _track = null;
        Rebind();
        Raise();
    }

    private void Rebind()
    {
        lock (_lock)
        {
            if (_transport != null) _transport.PausedChanged -= OnPausedChanged;
            _transport = _runner.ActiveSource as ITransportControls;
            if (_transport != null) _transport.PausedChanged += OnPausedChanged;
        }
    }

    private void OnPausedChanged(bool paused) => Raise();

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        _runner.TrackChanged -= OnTrackChanged;
        _runner.ActiveSourceChanged -= OnActiveSourceChanged;
        _queue.Model.Changed -= Raise;
        lock (_lock)
        {
            if (_transport != null) _transport.PausedChanged -= OnPausedChanged;
            _transport = null;
        }
    }
}

/// <summary>
/// A guest's player: the queue. While in a party the queue is the host's: the guest's own is set
/// aside, the host's songs are shown (each found here when it's loaded ahead, as the next song, or
/// looked up for the list), and what the user does to the queue is asked of the host. The party puts
/// each song on as the host plays it, from where the host is the moment it starts. It's played from
/// the host's own source when that works here (the same Spotify track, YouTube video or station);
/// otherwise this PC's sources are searched for it by title and artist, and a song nothing here has
/// is skipped. A song a source here has but can't play (its tools aren't installed, say) is reported
/// as that, not as missing.
/// </summary>
public sealed class QueuePartyPlayer : IPartyPlayer
{
    // Songs the host has queued, kept a while after they leave its list: a song is taken off the
    // list as it starts, and the guest may hear that before it hears the song is on.
    private const int KeepSongs = 200;

    private readonly QueuePlayback _queue;
    private readonly PartySongFinder _finder;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, QueueItem> _songs = new(StringComparer.Ordinal);
    private readonly List<string> _order = []; // _songs' keys, oldest first
    private volatile QueueItem? _item;
    private volatile string? _key;

    /// <param name="search">Finds a song on this PC's sources (default: <see cref="SongSources.SearchAsync"/>).</param>
    /// <param name="find">A source by id (default: the queue's sources).</param>
    public QueuePartyPlayer(QueuePlayback queue, Func<PartySong, CancellationToken, Task<IReadOnlyList<PartySource>>>? search = null,
        Func<string, IAudioSourceFactory?>? find = null)
        : this(queue, new PartySongFinder(queue, search, find))
    {
    }

    public QueuePartyPlayer(QueuePlayback queue, PartySongFinder finder)
    {
        _queue = queue;
        _finder = finder;
    }

    public async Task<bool> PlayAsync(PartySong song, Func<TimeSpan> startAt, CancellationToken ct)
    {
        // A song from the party's queue was likely found, and loaded, ahead.
        PartySongItem entry;
        lock (_lock) entry = _songs.TryGetValue(song.Key, out var queued) ? (PartySongItem)queued.Item : new PartySongItem(song, _finder);
        if (await entry.FindAsync(ct).ConfigureAwait(false) is not { } found) return false;
        var item = await _queue.PlayNowAsync(found, song.Live ? null : startAt, song.Key, ct).ConfigureAwait(false);
        _key = song.Key;
        _item = item;
        return true;
    }

    public void ShareQueue(IQueueOwner host) => _queue.Model.Share(host);

    public void MirrorQueue(PartyQueueList queue)
    {
        var items = new List<QueueItem>(queue.Songs.Count);
        lock (_lock)
        {
            foreach (var song in queue.Songs)
            {
                if (!_songs.TryGetValue(song.Key, out var item))
                {
                    _songs[song.Key] = item = new QueueItem(new PartySongItem(song, _finder), id: song.Key);
                    _order.Add(song.Key);
                }
                items.Add(item);
            }
            var listed = queue.Songs.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; _songs.Count > KeepSongs && i < _order.Count;)
            {
                if (listed.Contains(_order[i]))
                {
                    i++;
                    continue;
                }
                _songs.Remove(_order[i]);
                _order.RemoveAt(i);
            }
        }
        _queue.Model.Mirror(items, queue.From, [.. queue.Next.Select(n => new QueuePreview(n.Title, n.Subtitle, n.Placeholder,
            n.Source is { } s ? new ContentRef(s.SourceId, s.Locator) : null))]);
    }

    public void UnshareQueue()
    {
        _queue.Model.Unshare();
        lock (_lock)
        {
            _songs.Clear();
            _order.Clear();
        }
    }

    public PartyPlayerState? Current
    {
        get
        {
            if (_item is not { } item || _key is not { } key || _queue.ActiveEngine is not { } engine) return null;
            if (engine.PlayingNow is not var (playing, started) || !ReferenceEquals(playing, item)) return null;
            return new PartyPlayerState(key, item.Item.Position, started, engine.IsPaused, item.Item.CanSeek);
        }
    }

    public Task SeekAsync(TimeSpan position) => _queue.ActiveEngine?.SeekAsync(position) ?? Task.CompletedTask;

    public Task SetPausedAsync(bool paused) => _queue.ActiveEngine?.SetPausedAsync(paused) ?? Task.CompletedTask;

    private static void Log(string message) => Trace.WriteLine($"[hzn-party] {message}");
}

/// <summary>Songs as a party tells them.</summary>
internal static class PartySongs
{
    /// <summary>A queued item as a party's song, keyed <paramref name="key"/>: what's known of it, and
    /// where it plays from. A song a guest added that isn't found here yet is as the guest told it.</summary>
    public static PartySong Of(PlayableItem item, string key)
    {
        if (item is PartySongItem { Found: null } told) return told.Song with { Key = key };
        var meta = item.Metadata;
        IEnumerable<PartySource> sources = item.LiveOrigin is { } live ? [new PartySource(live.SourceId, live.Locator)] : SourcesOf(meta);
        if (item is PartySongItem added) sources = sources.Concat(added.Song.Sources).Distinct();
        return new PartySong(key, meta.Title, meta.Artist, item.Duration is { } d ? (long)d.TotalMilliseconds : null,
            item.LiveOrigin != null, [.. sources]);
    }

    /// <summary>Where a track can be played again: its own origin (a Spotify uri, a YouTube video, a file).</summary>
    public static PartySource[] SourcesOf(Track track) =>
        HistoryReplay.OriginOf(track) is { } origin ? [new PartySource(origin.SourceId, origin.Locator)] : [];
}

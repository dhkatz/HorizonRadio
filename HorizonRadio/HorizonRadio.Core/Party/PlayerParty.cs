using System.Diagnostics;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Party;

/// <summary>
/// The host's player, as a party sees it. On the queue, a song is the queued item (so playing it again
/// is a new song for the guests), a station is its stream, and a song counts as paused until it has
/// started, so guests wait for it rather than run ahead while it loads. Between songs nothing is
/// playing (not the last song, paused, which would pause the guests). On a self-driven source
/// (Spotify Connect) it's the track that source reports.
/// </summary>
public sealed class PlayerPartySource : IPartyHostSource, IDisposable
{
    private readonly SourceRunner _runner;
    private readonly QueuePlayback _queue;
    private readonly Lock _lock = new();
    private volatile Track? _track;
    private ITransportControls? _transport;

    public PlayerPartySource(SourceRunner runner, QueuePlayback queue)
    {
        _runner = runner;
        _queue = queue;
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
                return new PartyNow(new PartySong(item.Id, meta.Title, meta.Artist, Ms(item.Item.Duration), false, SourcesOf(meta)),
                    started ? item.Item.Position : null, paused || !started);
            }

            if (_track is not { Resolvable: true } track || string.IsNullOrWhiteSpace(track.Title)) return null;
            var progress = active as IPlaybackProgress;
            return new PartyNow(
                new PartySong(track.ExternalId ?? $"{track.SourceId}|{track.Artist}|{track.Title}", track.Title, track.Artist,
                    Ms(progress?.Duration), false, SourcesOf(track)),
                progress?.Duration != null ? progress.Position : null, paused);
        }
    }

    // Where the song can be played again: its own origin (a Spotify uri, a YouTube video, a file).
    private static PartySource[] SourcesOf(Track track) =>
        HistoryReplay.OriginOf(track) is { } origin ? [new PartySource(origin.SourceId, origin.Locator)] : [];

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
/// A guest's player: the queue. A party song plays next, ahead of what's queued (which stays), from
/// where the host is the moment it starts. It's played from the host's own source when that works
/// here (the same Spotify track, YouTube video or station); otherwise this PC's sources are searched
/// for it by title and artist, and a song nothing here has is skipped. A song a source here has but
/// can't play (its tools aren't installed, say) is reported as that, not as missing.
/// </summary>
public sealed class QueuePartyPlayer : IPartyPlayer
{
    private readonly QueuePlayback _queue;
    private readonly Func<PartySong, CancellationToken, Task<IReadOnlyList<PartySource>>> _search;
    private readonly Func<string, IAudioSourceFactory?> _find;
    private volatile QueueItem? _item;
    private volatile string? _key;

    /// <param name="search">Finds a song on this PC's sources (default: <see cref="SongSources.SearchAsync"/>).</param>
    /// <param name="find">A source by id (default: <see cref="SourceCatalog.Find"/>).</param>
    public QueuePartyPlayer(QueuePlayback queue, Func<PartySong, CancellationToken, Task<IReadOnlyList<PartySource>>>? search = null,
        Func<string, IAudioSourceFactory?>? find = null)
    {
        _queue = queue;
        _search = search ?? SearchAsync;
        _find = find ?? SourceCatalog.Find;
    }

    public async Task<bool> PlayAsync(PartySong song, Func<TimeSpan> startAt, CancellationToken ct)
    {
        var tried = new HashSet<PartySource>();
        var failed = new List<(IAudioSourceFactory Source, Exception Error)>();
        if (await TryAsync(song, song.Sources, startAt, tried, failed, ct).ConfigureAwait(false)) return true;
        if (!song.Live) // a station is tuned into, not found by name
        {
            var found = await _search(song, ct).ConfigureAwait(false);
            if (await TryAsync(song, found, startAt, tried, failed, ct).ConfigureAwait(false)) return true;
        }
        if (failed.Count == 0) return false; // nothing here has it
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

    private async Task<bool> TryAsync(PartySong song, IEnumerable<PartySource> sources, Func<TimeSpan> startAt,
        HashSet<PartySource> tried, List<(IAudioSourceFactory, Exception)> failed, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            if (!tried.Add(source) || _find(source.SourceId) is not IContentSourceFactory factory) continue;
            if (source.SourceId == "local" && !File.Exists(source.Locator)) continue; // the host's file, not ours
            try
            {
                var item = await _queue.PlayNowAsync(factory, source.Locator, song.Live ? null : startAt, ct).ConfigureAwait(false);
                _key = song.Key;
                _item = item;
                Log($"\"{song.Title}\" from {source.SourceId}");
                return true;
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
        return false;
    }

    private static async Task<IReadOnlyList<PartySource>> SearchAsync(PartySong song, CancellationToken ct)
    {
        var (found, _) = await SongSources.SearchAsync(new Track(song.Title, song.Artist, null, null, "", ""), ct).ConfigureAwait(false);
        return [.. found.Select(s => new PartySource(s.SourceId, s.Locator))];
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

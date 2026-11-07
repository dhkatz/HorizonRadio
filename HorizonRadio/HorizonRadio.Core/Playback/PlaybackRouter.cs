using System.Diagnostics;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Playback;

/// <summary>
/// The host's <see cref="IPlaybackRouter"/>: switches the single playback engine between
/// channels as a game asks (e.g. as the player tunes the in-game radio).
///
/// Channels share the one <see cref="SourceRunner"/>; only the routed one runs. The player
/// channel is the app's own queue (<see cref="QueuePlayback"/>); each mix gets a channel of its
/// own, created on first use — a <see cref="QueueSource"/> over a private queue whose context is
/// that mix. Leaving a channel parks it (<see cref="SourceRunner.ParkAsync"/>), keeping its
/// place; coming back resumes it per the route's <see cref="StationSwitchMode"/>. A self-driven
/// source the user started (Spotify Connect, the test tone) can't be parked, so leaving the
/// player stops it.
/// </summary>
public sealed class PlaybackRouter : IPlaybackRouter, IAsyncDisposable
{
    private readonly SourceRunner _runner;
    private readonly QueuePlayback _player;
    private readonly MixStore _mixes;
    private readonly MixContentResolver _resolver;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MixChannel> _channels = new(StringComparer.Ordinal);

    // The route in effect (engine state) and the latest one asked for. Route() only records the
    // request; DrainAsync applies requests in order until it catches up, so rapid tuning collapses
    // to the last station without overlapping switches.
    private volatile PlaybackRoute _current = PlaybackRoute.Player;
    private volatile PlaybackRoute _requested = PlaybackRoute.Player;

    private sealed class MixChannel(QueueSource source)
    {
        public QueueSource Source { get; } = source;
        public DateTimeOffset? ParkedAt { get; set; }
    }

    public PlaybackRouter(SourceRunner runner, QueuePlayback player, MixStore mixes, MixContentResolver resolver,
                          TimeProvider? time = null)
    {
        _runner = runner;
        _player = player;
        _mixes = mixes;
        _resolver = resolver;
        _time = time ?? TimeProvider.System;
        _player.CanActivate = () => _current.Kind == PlaybackRouteKind.Player;
        _mixes.Changed += OnMixesChanged;
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-router] {msg}");

    public IReadOnlyList<MixInfo> Mixes => [.. _mixes.All.Select(m => new MixInfo(m.Id, m.Name))];

    public event Action? MixesChanged;

    /// <summary>The route currently applied (lags <see cref="Route"/> briefly while switching).</summary>
    public PlaybackRoute Current => _current;

    /// <summary>Raised after a route takes effect. Fires on a background thread.</summary>
    public event Action<PlaybackRoute>? Routed;

    public void Route(PlaybackRoute route)
    {
        _requested = route;
        _ = DrainAsync();
    }

    /// <summary>Apply the latest requested route and wait for it (tests; awaitable form of
    /// <see cref="Route"/>).</summary>
    public async Task RouteAsync(PlaybackRoute route)
    {
        _requested = route;
        await DrainAsync().ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (_requested is var next && next != _current)
            {
                try { await ApplyAsync(next).ConfigureAwait(false); }
                catch (Exception ex) { Log($"route to {next.Kind} {next.MixId} failed: {ex.Message}"); }
                Routed?.Invoke(_current);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task ApplyAsync(PlaybackRoute route)
    {
        var leaving = _current;
        await LeaveAsync(leaving).ConfigureAwait(false);
        _current = route;

        switch (route.Kind)
        {
            case PlaybackRouteKind.Player:
                await _player.ResumeAsync().ConfigureAwait(false);
                break;

            case PlaybackRouteKind.Mix when route.MixId != null:
                if (_mixes.Get(route.MixId) is not { } mix)
                {
                    Log($"mix {route.MixId} no longer exists; staying silent");
                    break;
                }
                var channel = GetOrCreateChannel(mix);
                var advance = route.Mode == StationSwitchMode.Live && channel.ParkedAt is { } parkedAt
                    ? _time.GetUtcNow() - parkedAt
                    : TimeSpan.Zero;
                channel.ParkedAt = null;
                channel.Source.PauseLocked = route.Mode == StationSwitchMode.Live;
                channel.Source.ResumeFromPark(advance);
                await _runner.StartSourceAsync(channel.Source).ConfigureAwait(false);
                break;

            case PlaybackRouteKind.Silent:
            default:
                break;
        }
    }

    // Take whatever is on the runner off it: park a channel (it notes its own place as it
    // stops), or stop a self-driven source that can't be parked.
    private async Task LeaveAsync(PlaybackRoute leaving)
    {
        var active = _runner.ActiveSource;
        if (active is QueueSource queue)
        {
            await _runner.ParkAsync().ConfigureAwait(false);

            if (FindChannelId(queue) is { } mixId)
            {
                _channels[mixId].ParkedAt = _time.GetUtcNow();
                // Restart mode: nothing to come back to.
                if (leaving.Mode == StationSwitchMode.Restart)
                {
                    _channels.Remove(mixId);
                    await queue.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        else if (active != null)
        {
            await _runner.StopAsync().ConfigureAwait(false);
        }
    }

    private MixChannel GetOrCreateChannel(Mix mix)
    {
        if (_channels.TryGetValue(mix.Id, out var existing) && !existing.Source.IsDisposed)
            return existing;

        var model = new QueueModel();
        model.SetContext(new MixContextProvider(mix, _resolver, _runner.Shuffle), mix.Id);
        var channel = new MixChannel(new QueueSource(model));
        _channels[mix.Id] = channel;
        return channel;
    }

    private string? FindChannelId(QueueSource source) =>
        _channels.FirstOrDefault(kv => ReferenceEquals(kv.Value.Source, source)).Key;

    private void OnMixesChanged() => MixesChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        _mixes.Changed -= OnMixesChanged;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The runner disposes whatever it's running; parked channels are ours.
            foreach (var channel in _channels.Values)
            {
                if (!ReferenceEquals(_runner.ActiveSource, channel.Source))
                    await channel.Source.DisposeAsync().ConfigureAwait(false);
            }
            _channels.Clear();
            if (_player.Source is { IsDisposed: false } player && !ReferenceEquals(_runner.ActiveSource, player))
                await player.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}

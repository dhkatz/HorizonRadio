using System.Diagnostics;
using System.Threading.Channels;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Games;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Playback;

/// <summary>
/// The one owner of the audio engine (<see cref="SourceRunner"/>): nothing else starts, stops or
/// parks a source on it. Everything that changes what plays sends a command, and one loop applies
/// them in order, so none of them can overlap on the runner or change it behind the others' backs:
/// <list type="bullet">
/// <item>a game routes playback (<see cref="Route"/>, e.g. as the player tunes the in-game
/// radio): the app's player, one of the user's mixes, or nothing;</item>
/// <item>the player's queue gets something to play (<see cref="Player"/>);</item>
/// <item>the user starts a self-driven source (<see cref="StartSourceAsync"/>: Spotify Connect,
/// the test tone) or stops playback (<see cref="StopAsync"/>);</item>
/// <item>the user's mixes change.</item>
/// </list>
///
/// <para>Channels share the one runner; only the one the engine is on runs. The player channel
/// is the app's own queue (<see cref="QueuePlayback"/>), or a self-driven source the user
/// started in its place; each mix gets a channel of its own, created on first use — a
/// <see cref="QueueSource"/> over a private queue whose context is that mix. Leaving a channel
/// parks it, keeping its place; coming back resumes it per the route's
/// <see cref="StationSwitchMode"/>. A self-driven source can't be parked, so leaving the player
/// stops it.</para>
/// <code>
///   OnMix(m) ─────── RouteTo(m, mode) ──► OnMix(m, mode): plays on (a stopped one resumes)
///   OnPlayer ─────── RouteTo(player) ───► unchanged
///   any ──────────── RouteTo(elsewhere) ► leave it (park, or stop a self-driven source), enter
///   OnPlayer ─────── PlayerHasWork ─────► the queue engine on, if it wasn't
///   OnMix | Silent ─ PlayerHasWork ─────► unchanged: the work waits in the queue
///   any ──────────── StartExternal ─────► OnPlayer(the source); a mix station is parked
///   OnMix ────────── StopAll ───────────► OnMix(stopped): parked, its place kept
///   OnPlayer ─────── StopAll ───────────► OnPlayer(nothing)
///   any ──────────── MixesEdited ───────► unchanged; channels of edited mixes are dropped
/// </code>
/// </summary>
public sealed class PlaybackRouter : IPlaybackRouter, IAsyncDisposable
{
    // -- Commands: every change to what the engine plays. --

    private abstract record Command
    {
        /// <summary>Completed once the command has been applied, for a caller that waits.</summary>
        public TaskCompletionSource? Done { get; init; }
    }

    /// <summary>The game routes playback. Only the latest counts, so rapid tuning collapses to
    /// the last station.</summary>
    private sealed record RouteTo(PlaybackRoute Route, long Version) : Command;

    /// <summary>The player's queue has something to play.</summary>
    private sealed record PlayerHasWork : Command;

    /// <summary>The user starts a self-driven source.</summary>
    private sealed record StartExternal(ISelfDrivenSourceFactory Factory, ConfigValues Values) : Command;

    /// <summary>The user stops playback.</summary>
    private sealed record StopAll : Command;

    /// <summary>The user's mixes changed.</summary>
    private sealed record MixesEdited(IReadOnlyList<Mix> Mixes) : Command;

    // -- States: what the engine is on. Only the loop moves it on. --

    private abstract record EngineState;

    /// <summary>The game routes to nothing (its own music is on): nothing runs.</summary>
    private sealed record Silent : EngineState;

    /// <summary>The player. <paramref name="Running"/> is its queue engine, a self-driven source
    /// the user started, or null when nothing plays (stopped, or nothing to play).</summary>
    private sealed record OnPlayer(IAudioSource? Running) : EngineState;

    /// <summary>A mix station. <paramref name="Running"/> is its channel, or null when the user
    /// stopped it (the channel is parked) or the mix doesn't exist.</summary>
    private sealed record OnMix(string MixId, StationSwitchMode Mode, MixChannel? Running) : EngineState;

    private static readonly EngineState SilentState = new Silent();
    private static readonly EngineState PlayerIdle = new OnPlayer(null);

    private sealed class MixChannel(Mix mix, QueueSource source)
    {
        /// <summary>The mix as it was when the channel was built.</summary>
        public Mix Mix { get; } = mix;

        public QueueSource Source { get; } = source;

        public DateTimeOffset? ParkedAt { get; set; }

        /// <summary>Its mix was edited or deleted while it played: it plays on, and is dropped
        /// once it stops, so the station rebuilds from the new version.</summary>
        public bool Stale { get; set; }
    }

    private readonly SourceRunner _runner;
    private readonly MixStore _mixes;
    private readonly TimeProvider _time;
    private readonly Channel<Command> _commands =
        Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;

    private readonly Lock _routeLock = new();
    private long _routeVersion;

    // The user's mixes as of their last change, taken on the thread that changed them, so the
    // loop never reads the store while it's being edited.
    private volatile IReadOnlyList<Mix> _mixList;

    // ---- The loop's own: ----
    private readonly Dictionary<string, MixChannel> _channels = new(StringComparer.Ordinal);
    private volatile EngineState _state = PlayerIdle;

    public PlaybackRouter(SourceRunner runner, SourceConfigStore config, MixStore mixes, MixContentResolver resolver,
                          TimeProvider? time = null)
    {
        _runner = runner;
        _mixes = mixes;
        _time = time ?? TimeProvider.System;
        _mixList = [.. mixes.All];
        MixContexts = (mix, shuffle) => new MixContextProvider(mix, resolver, shuffle);

        // The persisted preference; SetShuffleAsync keeps the runner in step from here on.
        _runner.Shuffle = config.Settings.Get(PlaybackSettings.Shuffle);
        Player = new QueuePlayback(runner, config, resolver, () => Send(new PlayerHasWork { Done = NewDone() }));

        _mixes.Changed += OnMixesChanged;
        _loop = Task.Run(RunAsync);
    }

    private static void Log(string msg) => Trace.WriteLine($"[hzn-router] {msg}");

    /// <summary>The engine. Read what's playing from it; change that only through the router.</summary>
    public SourceRunner Runner => _runner;

    /// <summary>The player channel: the app's queue.</summary>
    public QueuePlayback Player { get; }

    /// <summary>The sources it plays from.</summary>
    public SourceCatalog Sources => Player.Sources;

    /// <summary>Builds the context a mix channel plays (tests swap in their own).</summary>
    internal Func<Mix, bool, IQueueContext> MixContexts { get; set; }

    public IReadOnlyList<MixInfo> Mixes => [.. _mixList.Select(m => new MixInfo(m.Id, m.Name))];

    public event Action? MixesChanged;

    /// <summary>The route the engine is on (lags <see cref="Route"/> briefly while switching).</summary>
    public PlaybackRoute Current => _state switch
    {
        OnPlayer => PlaybackRoute.Player,
        OnMix on => PlaybackRoute.Mix(on.MixId, on.Mode),
        _ => PlaybackRoute.Silent,
    };

    /// <summary>Raised after a route takes effect. Fires on a background thread.</summary>
    public event Action<PlaybackRoute>? Routed;

    public void Route(PlaybackRoute route) => SendRoute(route, done: null);

    /// <summary>Route, and wait until it (or a later route) has been applied.</summary>
    public Task RouteAsync(PlaybackRoute route) => SendRoute(route, NewDone());

    /// <summary>
    /// Start a self-driven source (Spotify Connect, the test tone) as the player's content, in
    /// place of whatever plays: the player's queue gives way, and a mix station is parked, so
    /// tuning back to it resumes it. Completes once it's running; throws if it can't start (a
    /// <see cref="MissingToolException"/> before anything is stopped).
    /// </summary>
    public Task StartSourceAsync(ISelfDrivenSourceFactory factory, ConfigValues values) =>
        Send(new StartExternal(factory, values) { Done = NewDone() });

    /// <summary>Stop playback. A mix station is parked, not discarded: when the game routes to it
    /// again it picks up where it stopped.</summary>
    public Task StopAsync() => Send(new StopAll { Done = NewDone() });

    /// <summary>Set the shuffle preference: applied to what's playing now, and to every channel
    /// as it starts or comes back.</summary>
    public Task SetShuffleAsync(bool enabled) => _runner.SetShuffleAsync(enabled);

    private static TaskCompletionSource NewDone() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task Send(Command command)
    {
        if (!_commands.Writer.TryWrite(command))
            command.Done?.TrySetException(new ObjectDisposedException(nameof(PlaybackRouter)));
        return command.Done?.Task ?? Task.CompletedTask;
    }

    private Task SendRoute(PlaybackRoute route, TaskCompletionSource? done)
    {
        // Numbered in the order they're queued, so the loop can tell one a later one replaces.
        lock (_routeLock) return Send(new RouteTo(route, ++_routeVersion) { Done = done });
    }

    // On the thread that changed them (the UI's).
    private void OnMixesChanged()
    {
        IReadOnlyList<Mix> mixes = [.. _mixes.All];
        _mixList = mixes;
        Send(new MixesEdited(mixes));
        MixesChanged?.Invoke();
    }

    private Mix? FindMix(string id) => _mixList.FirstOrDefault(m => m.Id == id);

    // -- The loop --

    private async Task RunAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await ApplyAsync(command).ConfigureAwait(false);
                command.Done?.TrySetResult();
            }
            catch (Exception ex)
            {
                Log($"{command.GetType().Name} failed: {ex.GetType().Name}: {ex.Message}");
                command.Done?.TrySetException(ex);
            }
        }
    }

    private Task ApplyAsync(Command command) => command switch
    {
        RouteTo route => RouteToAsync(route),
        PlayerHasWork => PlayQueueAsync(),
        StartExternal start => StartExternalAsync(start.Factory, start.Values),
        StopAll => StopAllAsync(),
        MixesEdited edited => DropEditedChannelsAsync(edited.Mixes),
        _ => Task.CompletedTask,
    };

    private async Task RouteToAsync(RouteTo command)
    {
        // A later route is queued behind this one: go straight there instead.
        if (command.Version != Interlocked.Read(ref _routeVersion)) return;

        var route = command.Route;
        try { await GoAsync(route).ConfigureAwait(false); }
        catch (Exception ex) { Log($"route to {route.Kind} {route.MixId} failed: {ex.Message}"); }
        Routed?.Invoke(Current);
    }

    private async Task GoAsync(PlaybackRoute route)
    {
        switch (_state)
        {
            // Already on that station: at most its switch mode changed, which only changes how it
            // behaves while away and whether it can be paused. It plays on.
            case OnMix { Running: { } channel } on when route.Kind == PlaybackRouteKind.Mix && route.MixId == on.MixId:
                channel.Source.PauseLocked = route.Mode == StationSwitchMode.Live;
                _state = on with { Mode = route.Mode };
                return;

            // The player stays as it is, even stopped: it has controls of its own to play again.
            case OnPlayer when route.Kind == PlaybackRouteKind.Player:
            case Silent when route.Kind == PlaybackRouteKind.Silent:
                return;

            // The user stopped this station. A station has no play button of its own, so when the
            // game routes to it again, it picks up where it stopped. It's still the same station:
            // there's nothing to leave.
            case OnMix { Running: null } on when route.Kind == PlaybackRouteKind.Mix && route.MixId == on.MixId:
                break;

            default:
                await LeaveAsync().ConfigureAwait(false);
                break;
        }
        await EnterAsync(route).ConfigureAwait(false);
    }

    // Take the engine off the channel it's on: park a channel (it notes its own place as it
    // stops), or stop a self-driven source, which can't be parked.
    private async Task LeaveAsync()
    {
        switch (_state)
        {
            case OnPlayer { Running: { } running }:
                if (ReferenceEquals(running, Player.Source)) await _runner.ParkAsync().ConfigureAwait(false);
                else await _runner.StopAsync().ConfigureAwait(false);
                break;

            case OnMix on:
                if (on.Running is { } channel)
                {
                    await _runner.ParkAsync().ConfigureAwait(false);
                    channel.ParkedAt = _time.GetUtcNow();
                }
                // Restart mode: nothing to come back to. An edited mix: rebuild it next time.
                if (_channels.TryGetValue(on.MixId, out var parked) && (on.Mode == StationSwitchMode.Restart || parked.Stale))
                    await DropChannelAsync(on.MixId).ConfigureAwait(false);
                break;
        }
        _state = SilentState;
    }

    private async Task EnterAsync(PlaybackRoute route)
    {
        switch (route.Kind)
        {
            case PlaybackRouteKind.Player:
                _state = PlayerIdle;
                await StartPlayerAsync().ConfigureAwait(false);
                break;

            case PlaybackRouteKind.Mix when route.MixId != null:
                _state = new OnMix(route.MixId, route.Mode, null);
                if (FindMix(route.MixId) is not { } mix)
                {
                    Log($"mix {route.MixId} no longer exists; staying silent");
                    break;
                }
                var channel = await GetOrCreateChannelAsync(mix).ConfigureAwait(false);
                // Live: time kept passing while we were away.
                var advance = route.Mode == StationSwitchMode.Live && channel.ParkedAt is { } parkedAt
                    ? _time.GetUtcNow() - parkedAt
                    : TimeSpan.Zero;
                channel.ParkedAt = null;
                channel.Source.PauseLocked = route.Mode == StationSwitchMode.Live;
                channel.Source.ResumeFromPark(advance);
                await _runner.StartSourceAsync(channel.Source).ConfigureAwait(false);
                _state = new OnMix(route.MixId, route.Mode, channel);
                break;

            default:
                _state = SilentState;
                break;
        }
    }

    // Put the player's queue engine on the runner, in place of anything else there: the parked
    // engine, which carries on with its track, or a new one if there's something to play.
    private async Task StartPlayerAsync()
    {
        var engine = Player.Source;
        if (engine is not { IsDisposed: false })
        {
            if (!Player.Model.HasWork) return;
            engine = Player.NewEngine();
        }
        await _runner.StartSourceAsync(engine).ConfigureAwait(false);
        _state = new OnPlayer(engine);
    }

    // Only the player plays the queue. On a mix station, or silent, the work waits in the queue
    // until the game routes back to the player.
    private async Task PlayQueueAsync()
    {
        if (_state is not OnPlayer on) return;
        if (on.Running != null && ReferenceEquals(on.Running, Player.Source)) return; // on: it picks the work up itself
        // Nothing playing, or a self-driven source, which gives way to the queue.
        await StartPlayerAsync().ConfigureAwait(false);
    }

    private async Task StartExternalAsync(ISelfDrivenSourceFactory factory, ConfigValues values)
    {
        // Check before stopping anything: a source whose tools are missing shouldn't tear down
        // what's playing. Throws MissingToolException with a Tools-tab hint.
        SourceRequirements.EnsureToolsAvailable(factory, values);
        var source = factory.Create(values);

        // It becomes the player's content. A mix station is parked (tuning back resumes it); the
        // player's queue engine gives way for good, as to any source that takes the runner.
        if (_state is not OnPlayer) await LeaveAsync().ConfigureAwait(false);
        if (Player.Source is { IsDisposed: false } engine && !ReferenceEquals(_runner.ActiveSource, engine))
            await engine.DisposeAsync().ConfigureAwait(false);

        _state = PlayerIdle;
        // Stops (and disposes) what the player was playing; on failure, the new source too.
        await _runner.StartSourceAsync(source, factory).ConfigureAwait(false);
        _state = new OnPlayer(source);
    }

    private async Task StopAllAsync()
    {
        if (_state is OnMix { Running: { } channel } on)
        {
            // Parked, not discarded: when the game routes to the station again (tuning back, or
            // the station's setting changing), it picks up where it stopped.
            await _runner.ParkAsync().ConfigureAwait(false);
            channel.ParkedAt = _time.GetUtcNow();
            _state = on with { Running = null };
            if (channel.Stale) await DropChannelAsync(on.MixId).ConfigureAwait(false);
            return;
        }

        await _runner.StopAsync().ConfigureAwait(false);
        if (_state is OnPlayer) _state = PlayerIdle;
    }

    // A channel keeps the mix it was built from, so one whose mix was edited or deleted is
    // dropped (its place goes with it) and the station rebuilds from the new version.
    private async Task DropEditedChannelsAsync(IReadOnlyList<Mix> mixes)
    {
        foreach (var (mixId, channel) in _channels.ToList())
        {
            if (mixes.FirstOrDefault(m => m.Id == mixId) is { } mix && mix.Entries.SequenceEqual(channel.Mix.Entries))
                continue;

            if (_state is OnMix { Running: { } running } && ReferenceEquals(running, channel))
            {
                // On air: no cut mid-song. It's dropped once it stops.
                channel.Stale = true;
                continue;
            }
            await DropChannelAsync(mixId).ConfigureAwait(false);
        }
    }

    private async Task<MixChannel> GetOrCreateChannelAsync(Mix mix)
    {
        if (_channels.TryGetValue(mix.Id, out var existing))
        {
            if (!existing.Source.IsDisposed && !existing.Stale) return existing;
            await DropChannelAsync(mix.Id).ConfigureAwait(false);
        }

        var model = new QueueModel();
        model.SetContext(MixContexts(mix, _runner.Shuffle), mix.Id);
        var channel = new MixChannel(mix, new QueueSource(model));
        _channels[mix.Id] = channel;
        return channel;
    }

    private async Task DropChannelAsync(string mixId)
    {
        if (_channels.Remove(mixId, out var channel))
            await channel.Source.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _mixes.Changed -= OnMixesChanged;
        _commands.Writer.TryComplete();
        await _loop.ConfigureAwait(false);

        // What's on the runner stops (and is disposed); parked channels and the player's engine
        // are ours.
        await _runner.StopAsync().ConfigureAwait(false);
        foreach (var channel in _channels.Values)
            await channel.Source.DisposeAsync().ConfigureAwait(false);
        _channels.Clear();
        if (Player.Source is { } engine) await engine.DisposeAsync().ConfigureAwait(false);
        Player.Dispose();
    }
}

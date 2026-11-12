using System.Diagnostics;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Audio;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Tools.Librespot;
using SpotifyAPI.Web;

namespace HorizonRadio.Source.Spotify;

/// <summary>
/// Drives Spotify playback <em>track-by-track</em> from our own engine, the way
/// <c>YouTubePlayableItem</c> drives ffmpeg — the opposite of the
/// legacy self-driven receiver, where Spotify (the user's phone) decided what
/// played and we were a passive Connect target.
///
/// One long-lived librespot process is the audio device; the Web API is the
/// remote control. Its PCM stdout is read continuously by a single
/// <see cref="SubprocessPcmSource"/> into a <see cref="RoutingSink"/> that we
/// re-point at the current track's sink for each <see cref="PlayTrackAsync"/> and
/// detach between tracks. librespot runs with <c>--autoplay off</c>, so a
/// single-URI play stops at <c>end_of_track</c> and our engine advances to the
/// next item (which may be a YouTube/local track on its own decode path).
///
/// We lean on the authed Web API as little as possible: one play call per track
/// (plus a one-time device lookup, and seek on demand). Position/duration come
/// from librespot's <c>--onevent</c> breadcrumb, not from polling the Web API.
///
/// Only one Spotify track plays at a time (the engine is sequential), so the
/// "current track" signalling state below is single-writer by construction.
/// </summary>
public sealed class SpotifyPlaybackService : IAsyncDisposable
{
    private readonly SpotifyConnection _connection;
    private readonly Func<LibrespotOptions> _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly RoutingSink _routing = new();
    private readonly object _sync = new();

    private SubprocessPcmSource? _librespot;
    private CancellationTokenSource? _serviceCts;
    private string? _deviceId;
    // The device name librespot was last launched with — what EnsureDeviceAsync looks
    // up. Captured at launch (options are read fresh each launch, so this tracks them).
    // The Connect device librespot registers as: set on each launch, read before the first from the settings.
    private string? _deviceName;
    private string DeviceName => _deviceName ?? _options().DeviceName;

    // Per-track signalling, swapped under _sync at the start of each PlayTrackAsync
    // and read from the stderr-drain thread.
    private TaskCompletionSource? _trackStarted;
    private TaskCompletionSource? _trackEnded;
    // Set when librespot reports the track unavailable; the result says whether Spotify
    // refused its audio key (seen on stderr since the play began).
    private TaskCompletionSource<bool>? _trackRefused;
    private volatile bool _keyRefused;
    // The track this play asked for and the one librespot is loading for it (a relink can
    // differ): an "unavailable" for any other track is a late one from a skipped play.
    private string? _requestedId, _loadingId;

    /// <summary>Spotify refused librespot a track's audio key this session. It refuses per
    /// account, so later tracks can skip librespot (and the second it takes to be refused).</summary>
    public bool AudioKeyRefused { get; private set; }

    /// <summary>The librespot the next play would launch (<see cref="SpotifyPlayerChoice.BuildOf"/>).</summary>
    public string? LibrespotBuild => SpotifyPlayerChoice.BuildOf(_options().ExecutablePath);
    private Action? _onPlaying;
    private bool _playingFired;

    // How long a playing (not paused) track may deliver no audio before we give up on
    // it. Generous: a cold librespot buffers in a second or two, and a seek re-buffers
    // faster than that, so 15 s of silence means the stream is genuinely gone.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    // True from the pause watcher's pause edge until its resume (including recovery)
    // finishes. The end-wait treats that whole span as paused: the engine's pause flag
    // flips back a beat before the watcher acts on it, and a librespot that died during
    // the pause is the resume recovery's to relaunch, not a reason to end the track.
    private volatile bool _pauseHeld;

    // Position is PCM-throughput based, like the ffmpeg-backed items: a base offset
    // (0 at track start, the target on seek) plus the frames actually delivered
    // since that base. While paused, librespot stops producing PCM, so no frames are
    // delivered and the position freezes on its own — no event-clock that would drift
    // forward across a pause. Duration comes from librespot's breadcrumb (the relinked
    // track's true length) or the known value. ms; Interlocked for torn-free 64-bit
    // reads from the UI poll.
    private long _durationMs;
    private long _baseMs;

    /// <param name="options">Read fresh on each librespot launch, so installing
    /// librespot or editing the Spotify config mid-session takes effect on the next
    /// (re)launch instead of being frozen at app startup.</param>
    public SpotifyPlaybackService(SpotifyConnection connection, Func<LibrespotOptions> options,
        IProcesses processes, IPluginLog log)
    {
        _connection = connection;
        _options = options;
        _processes = processes;
        _log = log;
    }

    private readonly IProcesses _processes;
    private readonly IPluginLog _log;

    // Surface to the Console tab (alongside librespot's own lines) as well as the
    // debugger, so pause/resume/recovery behaviour is diagnosable from a normal run.
    private void Log(string msg)
    {
        _log.Trace("spotify-svc", msg);
        _log.Write("spotify", msg);
    }

    // Run a Web API control call, logging failures (with HTTP status) instead of
    // throwing — except cancellation, which propagates.
    private async Task TryControlAsync(Func<Task> action, string label)
    {
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (APIException ex) { Log($"{label} failed: HTTP {(int?)ex.Response?.StatusCode} — {ex.Message}"); }
        catch (Exception ex) { Log($"{label} failed: {ex.Message}"); }
    }

    // -- position/duration surface for the active item --

    public TimeSpan? Duration
    {
        get { var d = Interlocked.Read(ref _durationMs); return d > 0 ? TimeSpan.FromMilliseconds(d) : null; }
    }

    public TimeSpan Position
    {
        get
        {
            var ms = Interlocked.Read(ref _baseMs)
                     + _routing.Frames * 1000 / AudioFormat.SampleRate;
            var dur = Interlocked.Read(ref _durationMs);
            if (dur > 0 && ms > dur) ms = dur;
            return TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        }
    }

    /// <summary>
    /// Play exactly one Spotify track on our librespot device and pump its PCM into
    /// <paramref name="ctx"/> until it ends naturally (returns) or <paramref name="ct"/>
    /// fires (throws <see cref="OperationCanceledException"/> — skip/stop). Pause is
    /// honored by dropping samples + pausing Spotify via the Web API (not by blocking
    /// the read loop). <paramref name="onPlaying"/> fires once, when librespot reports
    /// the track actually started, so the caller can publish the HUD track timed to
    /// real audio rather than to the (laggy) play command.
    /// </summary>
    public async Task PlayTrackAsync(
        string trackUri, TimeSpan? duration, PumpContext ctx, Action? onPlaying, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        var client = await _connection.GetClientAsync(ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Spotify is not connected.");
        var deviceId = await EnsureDeviceAsync(client, ct).ConfigureAwait(false);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Reset position/duration BEFORE publishing the new _trackEnded, so a late
        // end_of_track for the *previous* track (delivered on the stderr thread in the
        // gap) evaluates ReachedEnd() against this track's fresh, zeroed position
        // (pos 0 < newDur) and is ignored — rather than instantly completing the new
        // track's `ended`.
        ResetPosition(duration);
        lock (_sync)
        {
            _trackStarted = started;
            _trackEnded = ended;
            _trackRefused = refused;
            _keyRefused = false;
            _requestedId = trackUri.Split(':')[^1];
            _loadingId = null;
            _onPlaying = onPlaying;
            _playingFired = false;
        }

        // CRUCIAL: do NOT gate-block the read loop on pause. Blocking it fills
        // librespot's stdout pipe, and after a long pause that stalled writer never
        // recovers (Spotify resumes, but librespot stops emitting PCM — silent, stuck).
        // Instead: a watcher task pauses/resumes librespot via the Web API (so it stops
        // producing), and the sink simply drops samples while paused (instant silence,
        // frozen position) without ever blocking the loop — so the pipe never stalls,
        // regardless of whether paused librespot stops writing or emits silence.
        _routing.Target = new PauseAwareSink(ctx.Sink, ctx.IsPaused);
        _pauseHeld = false;
        var handedOff = false;
        long handOffFrames = 0;
        using var pauseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pauseWatcher = Task.Run(
            () => WatchPauseAsync(client, trackUri, ctx.IsPaused, pauseCts.Token), pauseCts.Token);
        try
        {
            // Play this one track; --autoplay off means librespot stops after it.
            //
            // On a cold start the device can register in the Web API device list (so
            // EnsureDeviceAsync already found it) a beat BEFORE librespot's Connect
            // session is ready to receive commands. The first play is then accepted by
            // Spotify's cloud but reaches no live session, so no PCM ever flows and the
            // track hangs silently — the engine's end-wait below never completes because
            // position stays at 0. (This is the "first queued track never plays until you
            // skip to the next" bug: only the first play after a cold start races the
            // session handshake; by the next track the session is up.)
            //
            // So don't trust a single fire-and-forget play: re-issue it until librespot
            // actually reports it started (or begins delivering frames, covering an older
            // librespot that doesn't emit the event). We only re-issue when nothing
            // happened at all, so a track that's merely slow to emit "playing" — but is
            // already streaming — isn't restarted.
            var playRequest = new PlayerResumePlaybackRequest
            {
                DeviceId = deviceId,
                Uris = new List<string> { trackUri },
            };

            // Re-issue the play until librespot confirms it actually started, so a
            // command lost to the cold-start session race doesn't leave the track
            // hanging silently. "Confirmed" means any of:
            //   • the playing/track_changed event arrived (started.Task), or
            //   • frames are flowing (older librespot that emits no event), or
            //   • the user paused — frames stop by design while paused, so a 0-frame
            //     reading there is NOT "didn't start"; re-issuing would replay the
            //     track from 0 and fight the pause watcher, so treat pause as confirmed.
            const int maxAttempts = 3;
            var confirmed = false;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await TryControlAsync(
                    () => client.Player.ResumePlayback(playRequest, ct), "play").ConfigureAwait(false);

                // Give librespot a moment to actually start streaming before we trust the
                // end signal — also guards against a stale end_of_track from a previous
                // track racing the new play.
                await WaitWithTimeoutAsync(Task.WhenAny(started.Task, refused.Task), TimeSpan.FromSeconds(5), ct)
                    .ConfigureAwait(false);

                // librespot gave up on the track: asking again gets the same answer.
                if (refused.Task.IsCompleted)
                {
                    var keyRefused = refused.Task.Result;
                    Log(keyRefused
                        ? "play: Spotify refused the track's audio key to librespot"
                        : "play: librespot reports the track unavailable");
                    if (keyRefused) AudioKeyRefused = true;
                    throw new SpotifyTrackUnavailableException(trackUri, keyRefused);
                }

                confirmed = started.Task.IsCompleted || _routing.Frames > 0 || ctx.IsPaused();
                if (confirmed || ct.IsCancellationRequested) break;
                Log($"play: no start after 5s (attempt {attempt}); re-issuing");
            }

            // Never confirmed after the full budget — the play genuinely failed (a
            // swallowed device error, a dead/unauthorized device). Throw so the engine
            // logs "item failed" and advances to the next track, rather than spinning
            // forever in the end-wait below with a position frozen at 0.
            if (!confirmed && !ct.IsCancellationRequested)
                throw new InvalidOperationException(
                    $"Spotify never confirmed playback after {maxAttempts} attempts — the device may be unavailable.");

            // Wait for the track to finish. Both end signals are PLAYBACK-based and so
            // freeze while paused — a long pause can no longer end the track early.
            // (A wall-clock backstop here used to count paused time: a pause longer
            // than the track's remaining length silently "ended" it and advanced the
            // queue, leaving resume with no track to resume.)
            //   • librespot's end_of_track breadcrumb (gated by ReachedEnd), or
            //   • position reaching the track's duration (we delivered all its audio).
            // Neither arrives if librespot dies or its audio stops for good, so the
            // watchdog also fails the track then (QueueSource logs it and moves on) —
            // judged only while playing, for the same reason.
            var watchdog = new TrackEndWatchdog(StallTimeout);
            var clock = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                var verdict = watchdog.Check(new TrackWaitState(
                    EndEventSeen: ended.Task.IsCompleted,
                    PositionMs: (long)Position.TotalMilliseconds,
                    DurationMs: Interlocked.Read(ref _durationMs),
                    Frames: _routing.Frames,
                    Suspended: ctx.IsPaused() || _pauseHeld,
                    LibrespotAlive: IsAlive(_librespot)), clock.Elapsed);

                if (verdict == TrackWaitVerdict.Ended) break;
                if (verdict == TrackWaitVerdict.LibrespotExited)
                {
                    Log($"librespot exited mid-track at {(long)Position.TotalMilliseconds}ms; ending the track");
                    throw new InvalidOperationException("librespot exited mid-track.");
                }
                if (verdict == TrackWaitVerdict.Stalled && !handedOff)
                {
                    // Playback the user moved to another device (their phone) also goes
                    // quiet here. Advancing would make the next track's play command
                    // pull it straight back, so hold this track instead: no steal, no
                    // loop. A skip, a pause/resume, or moving playback back to us
                    // resumes control — all explicit user actions.
                    if (await FindOtherActiveDeviceAsync(client, deviceId, ct).ConfigureAwait(false) is { } other)
                    {
                        handedOff = true;
                        handOffFrames = _routing.Frames;
                        Log($"playback moved to \"{other}\"; holding this track (skip, or pause and resume, to take it back)");
                    }
                    else
                    {
                        Log($"no audio for {StallTimeout.TotalSeconds:0}s at {(long)Position.TotalMilliseconds}ms; ending the track");
                        throw new InvalidOperationException(
                            $"Spotify playback stalled: no audio for {StallTimeout.TotalSeconds:0}s.");
                    }
                }
                else if (handedOff && _routing.Frames != handOffFrames)
                {
                    // Audio again (moved back to us, or a resume took it back): a later
                    // stall is judged afresh.
                    handedOff = false;
                    Log("playback is back on this device");
                }

                try { await ended.Task.WaitAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
                catch (TimeoutException) { /* re-check progress */ }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            pauseCts.Cancel();
            try { await pauseWatcher.ConfigureAwait(false); } catch { }
            _pauseHeld = false;

            _routing.Target = null;
            lock (_sync)
            {
                _trackStarted = null;
                _trackEnded = null;
                _trackRefused = null;
                _onPlaying = null;
            }

            // Stop the device so audio doesn't keep playing under the next item (a
            // YouTube/local track won't issue its own Spotify command). Best-effort:
            // on a natural end librespot has already stopped, so this may no-op/404.
            // CancellationToken.None: must run even when ct fired (the skip case).
            // Skipped when playback moved to another device: it isn't ours to pause.
            if (!handedOff)
            {
                try { await client.Player.PausePlayback(new PlayerPausePlaybackRequest { DeviceId = deviceId }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Log($"pause-on-exit: {ex.Message}"); }
            }
        }

        // Completion above returns on natural end or cancellation; the token says which.
        ct.ThrowIfCancellationRequested();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var deviceId = _deviceId;
        if (deviceId is null) return;
        var client = await _connection.GetClientAsync().ConfigureAwait(false);
        if (client is null) return;

        var ms = (long)position.TotalMilliseconds;
        if (ms < 0) ms = 0;
        try
        {
            await client.Player.SeekTo(new PlayerSeekToRequest(ms) { DeviceId = deviceId }).ConfigureAwait(false);
            // Rebase position to the seek target and recount frames from here.
            Interlocked.Exchange(ref _baseMs, ms);
            _routing.ResetFrames();
        }
        catch (Exception ex) { Log($"seek: {ex.Message}"); }
    }

    // -- librespot lifecycle --

    /// <summary>
    /// Stop and dispose librespot, freeing the "Horizon Radio" Connect device — e.g.
    /// when the user switches to the zero-config <see cref="SpotifySource"/> cast
    /// receiver, which wants the same device. Idempotent; the next
    /// <see cref="PlayTrackAsync"/> relaunches it.
    /// </summary>
    public async Task ReleaseAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { _serviceCts?.Cancel(); } catch { }
            if (_librespot != null) await _librespot.DisposeAsync().ConfigureAwait(false);
            _librespot = null;
            _serviceCts?.Dispose();
            _serviceCts = null;
            _deviceId = null;
        }
        finally { _startGate.Release(); }
    }

    // Alive = the process is running AND its PCM read loop is still going. A process
    // that's up but whose stdout EOF'd/errored will never deliver audio again either.
    private static bool IsAlive(SubprocessPcmSource? librespot) =>
        librespot is { IsRunning: true, Completion.IsCompleted: false };

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsAlive(_librespot)) return;

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsAlive(_librespot)) return;

            // A read loop that EOF'd / a dead process: tear it all down before the
            // relaunch, including the cached device id (re-resolved for the new session).
            if (_librespot != null)
            {
                Log("librespot is not running; relaunching");
                try { _serviceCts?.Cancel(); } catch { }
                await _librespot.DisposeAsync().ConfigureAwait(false);
                _librespot = null;
            }
            _serviceCts?.Dispose();
            _serviceCts = null;
            _deviceId = null;

            var o = _options();
            _deviceName = o.DeviceName;
            Directory.CreateDirectory(o.CacheDirectory);
            _serviceCts = new CancellationTokenSource();

            var subproc = new SubprocessPcmSource(new SubprocessPcmSource.Config
            {
                ExecutablePath = o.ExecutablePath,
                Args = Librespot.BuildArgs(o, autoplay: false),
                ToolName = "librespot",
                OnStderrLine = OnStderr,
            }, _processes, _log);
            await subproc.StartAsync(_routing, _serviceCts.Token).ConfigureAwait(false);
            _librespot = subproc;
        }
        finally { _startGate.Release(); }
    }

    // When our audio stalls, ask Spotify whether the user's playback is now on another
    // Connect device (they moved it to their phone). Returns that device's name, or
    // null when it's still ours, nothing is active, or the lookup failed — all of
    // which leave the stall a genuine failure. One Web API call, only on a stall.
    private async Task<string?> FindOtherActiveDeviceAsync(SpotifyClient client, string deviceId, CancellationToken ct)
    {
        try
        {
            var device = (await client.Player.GetCurrentPlayback(ct).ConfigureAwait(false))?.Device;
            if (device is not { IsActive: true }) return null;
            if (device.Id == deviceId || device.Id == _deviceId) return null;
            if (string.Equals(device.Name, DeviceName, StringComparison.OrdinalIgnoreCase)) return null;
            return device.Name ?? device.Id ?? "another device";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"playback lookup failed: {ex.Message}");
            return null;
        }
    }

    // Find our librespot device in the user's Connect device list. Freshly launched,
    // it takes a beat to register over zeroconf + sync to Spotify's cloud, so poll.
    private async Task<string> EnsureDeviceAsync(SpotifyClient client, CancellationToken ct)
    {
        if (_deviceId is { } cached) return cached;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var devices = await client.Player.GetAvailableDevices(ct).ConfigureAwait(false);
                var match = devices.Devices.FirstOrDefault(d =>
                    string.Equals(d.Name, DeviceName, StringComparison.OrdinalIgnoreCase));
                if (match?.Id is { } id)
                {
                    _deviceId = id;
                    return id;
                }
            }
            catch (Exception ex) { Log($"device lookup: {ex.Message}"); }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Spotify device \"{DeviceName}\" never appeared. Make sure librespot is logged in " +
            "(cast to it once from your Spotify app) and the account is Premium.");
    }

    // -- librespot --onevent breadcrumb parsing (subset of the legacy receiver) --

    private void OnStderr(string line)
    {
        // librespot's own log: Spotify refusing the key that decrypts the track ("error audio
        // key <seq> <code>"); it then reports the track "unavailable" (below). Code 2 is Spotify
        // rate-limiting the requests, which passes; the others refuse the account.
        if (AudioKeyErrorCode(line) is { } code)
        {
            if (code != "2") _keyRefused = true;
            return;
        }

        // "HZNEV <event> <track_id?> <position_ms?> <duration_ms?>"
        if (!line.StartsWith("HZNEV ", StringComparison.Ordinal)) return;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;

        var ev = parts[1];

        // The relinked track's true duration arrives on track_changed; prefer it
        // over the value we passed in (which was for the requested, pre-relink id).
        if (parts.Length >= 5 && long.TryParse(parts[4], out var durMs) && durMs > 0)
            Interlocked.Exchange(ref _durationMs, durMs);

        switch (ev)
        {
            case "playing":
            case "track_changed":
                Action? onPlaying = null;
                lock (_sync)
                {
                    _trackStarted?.TrySetResult();
                    if (!_playingFired)
                    {
                        _playingFired = true;
                        onPlaying = _onPlaying;
                    }
                }
                try { onPlaying?.Invoke(); }
                catch (Exception ex) { Log($"onPlaying: {ex.Message}"); }
                break;

            case "end_of_track":
            case "stopped":
                // These also fire on a pause-stop and on a context replacement (our
                // resume-recovery restart of the same track) — not just a genuine end.
                // Only end the item if we actually reached ~the track's length; a
                // truly missed end is caught by the duration backstop or, failing that,
                // the end-wait's stall watchdog.
                if (ReachedEnd()) lock (_sync) { _trackEnded?.TrySetResult(); }
                break;

            case "loading" when parts.Length >= 3:
                // A fresh load: what earlier tracks were refused doesn't count for it.
                lock (_sync)
                {
                    _loadingId = parts[2];
                    _keyRefused = false;
                }
                break;

            case "unavailable":
                // librespot couldn't load the track and gave up on it: no audio will come.
                lock (_sync)
                {
                    var id = parts.Length >= 3 ? parts[2] : null;
                    if (id == null || id == _requestedId || id == _loadingId) _trackRefused?.TrySetResult(_keyRefused);
                }
                break;
        }
    }

    /// <summary>The code in librespot's "error audio key &lt;seq&gt; &lt;code&gt;" log line, or null
    /// for any other line.</summary>
    internal static string? AudioKeyErrorCode(string line)
    {
        const string Marker = "error audio key ";
        var at = line.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var fields = line[(at + Marker.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 ? fields[1] : null;
    }

    // True when playback has advanced to within a small margin of the track length
    // (or the length is unknown, so we trust the event). The margin is capped at a
    // quarter of the track so SHORT tracks (interludes/skits/intros) don't treat a
    // stray end/stop at position 0 as "reached the end" (a fixed 10s margin went
    // negative for sub-10s tracks and ended them instantly).
    private bool ReachedEnd()
    {
        var dur = Interlocked.Read(ref _durationMs);
        if (dur <= 0) return true;
        var pos = Interlocked.Read(ref _baseMs) + _routing.Frames * 1000 / AudioFormat.SampleRate;
        var margin = Math.Min(10_000, dur / 4);
        return pos >= dur - margin;
    }

    private void ResetPosition(TimeSpan? duration)
    {
        Interlocked.Exchange(ref _durationMs, duration is { } d ? (long)d.TotalMilliseconds : 0);
        Interlocked.Exchange(ref _baseMs, 0);
        _routing.ResetFrames();
    }

    // Apply the engine's pause state to Spotify via the Web API (off the read loop, so
    // the pipe never stalls). Polls the pause flag and, on each edge, pauses/resumes
    // the device (read from _deviceId so a recovery re-resolve takes effect) — with
    // resume recovery for a stream librespot couldn't revive.
    private async Task WatchPauseAsync(
        SpotifyClient client, string trackUri, Func<bool> isPaused, CancellationToken ct)
    {
        var lastPaused = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var paused = isPaused();
                if (paused != lastPaused)
                {
                    lastPaused = paused;
                    if (paused)
                    {
                        _pauseHeld = true;
                        Log("pause: pausing device");
                        await TryControlAsync(
                            () => client.Player.PausePlayback(new PlayerPausePlaybackRequest { DeviceId = _deviceId }, ct),
                            "pause").ConfigureAwait(false);
                    }
                    else
                    {
                        // Held until the resume (and any recovery) settles, so the end-wait's
                        // stall clock starts from a resumed stream, not mid-recovery.
                        try { await ResumeWithRecoveryAsync(client, trackUri, isPaused, ct).ConfigureAwait(false); }
                        finally { _pauseHeld = false; }
                    }
                }
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Resume the current track. A plain resume is enough for short pauses, but after a
    // long pause librespot can be wedged — or its process/Connect session may have
    // dropped entirely — so Spotify accepts the resume yet no PCM flows. If no audio
    // appears within a few seconds, fully re-establish: relaunch librespot if it died,
    // re-resolve the (possibly stale) device, and replay the track from the saved
    // position. The end-of-track gate (ReachedEnd) keeps that replay from being
    // mistaken for the track finishing.
    private async Task ResumeWithRecoveryAsync(
        SpotifyClient client, string trackUri, Func<bool> isPaused, CancellationToken ct)
    {
        var framesBefore = _routing.Frames;
        Log("resume: requesting playback resume");
        await TryControlAsync(
            () => client.Player.ResumePlayback(new PlayerResumePlaybackRequest { DeviceId = _deviceId }, ct),
            "resume").ConfigureAwait(false);

        try { await Task.Delay(3000, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (isPaused() || ct.IsCancellationRequested) return;
        if (_routing.Frames > framesBefore) { Log("resume: audio flowing"); return; }

        var posMs = Math.Max(0, (long)Position.TotalMilliseconds);
        var running = IsAlive(_librespot);
        Log($"resume: no audio after 3s (librespot running={running}); re-establishing at {posMs}ms");
        try
        {
            await EnsureStartedAsync(ct).ConfigureAwait(false); // relaunch if the process died
            _deviceId = null;                                   // force a fresh device lookup
            var dev = await EnsureDeviceAsync(client, ct).ConfigureAwait(false);
            await client.Player.ResumePlayback(new PlayerResumePlaybackRequest
            {
                DeviceId = dev,
                Uris = new List<string> { trackUri },
                PositionMs = (int)posMs,
            }, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _baseMs, posMs);
            _routing.ResetFrames();
            Log("resume: re-established stream");
        }
        catch (OperationCanceledException) { throw; }
        catch (APIException ex) { Log($"resume re-establish failed: HTTP {(int?)ex.Response?.StatusCode} — {ex.Message}"); }
        catch (Exception ex) { Log($"resume re-establish failed: {ex.Message}"); }
    }

    private static async Task WaitWithTimeoutAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The timeout (not the caller) fired — treat as "done" and move on.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _serviceCts?.Cancel(); } catch { }
        if (_librespot != null) await _librespot.DisposeAsync().ConfigureAwait(false);
        _librespot = null;
        _serviceCts?.Dispose();
        _serviceCts = null;
        _startGate.Dispose();
    }
}

/// <summary>
/// Forwards PCM to the inner sink unless paused, in which case it drops the samples
/// (instant silence) without blocking the producer — so librespot's read loop keeps
/// draining and never stalls a full pipe. Actually stopping librespot on pause is the
/// Web API watcher's job; this just gates what reaches the game/sink meanwhile.
/// </summary>
internal sealed class PauseAwareSink(IPcmSink inner, Func<bool> isPaused) : IPcmSink
{
    public bool Send(ReadOnlySpan<short> samples) => isPaused() ? false : inner.Send(samples);
}

/// <summary>
/// An <see cref="IPcmSink"/> whose destination can be re-pointed at runtime. The
/// long-lived librespot read loop always writes here; <see cref="SpotifyPlaybackService"/>
/// aims <see cref="Target"/> at the current track's sink and nulls it between
/// tracks (where samples are dropped — but librespot, stopped, isn't producing any).
///
/// Also counts the frames actually delivered to the target, which is the position
/// clock: while paused, librespot (paused via the Web API) stops producing, so no
/// frames arrive and the count — and thus reported position — freezes, resuming
/// exactly where it left off. <see cref="ResetFrames"/> rebases it at track start
/// and on seek.
/// </summary>
internal sealed class RoutingSink : IPcmSink
{
    private volatile IPcmSink? _target;
    private long _frames;

    public IPcmSink? Target
    {
        get => _target;
        set => _target = value;
    }

    /// <summary>Frames (stereo sample pairs) delivered to the target since the last reset.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    public void ResetFrames() => Interlocked.Exchange(ref _frames, 0);

    public bool Send(ReadOnlySpan<short> samples)
    {
        var target = _target;
        if (target is null) return true; // between tracks: drop (librespot is stopped)

        var ok = target.Send(samples);
        if (ok) Interlocked.Add(ref _frames, samples.Length / AudioFormat.Channels);
        return ok;
    }
}

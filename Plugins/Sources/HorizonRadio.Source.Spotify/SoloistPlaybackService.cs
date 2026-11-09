using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Tools.Soloist;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>Soloist couldn't play: not set up, not paired yet, an expired build, or the tap failed.
/// The message says what to do.</summary>
public sealed class SoloistUnavailableException : Exception
{
    public SoloistUnavailableException() { }
    public SoloistUnavailableException(string message) : base(message) { }
    public SoloistUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Plays Spotify tracks through Spotify Soloist, for the ones Spotify refuses librespot the audio
/// of. Starts <c>soloist.exe</c> with the user's key and loads the audio tap into it before it opens
/// any audio, so what Soloist plays reaches the pipeline (<see cref="AudioTap"/>) and never the
/// speakers; controls it over its local WebSocket API. One Soloist for the app's lifetime, one track
/// at a time (the engine plays one item at a time).
/// </summary>
public sealed class SoloistPlaybackService : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TrackTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<SoloistOptions?> _options;
    private readonly Func<string?> _tapDll;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly RoutingSink _routing = new();
    private readonly object _sync = new();

    private Process? _process;
    private AudioTap? _tap;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private TaskCompletionSource<bool>? _authKnown;
    private TaskCompletionSource? _loggedInSignal;
    private volatile bool _loggedIn;
    private volatile bool _isActive;

    // The track playing now, and how its steps reach PlayTrackAsync.
    private SoloistTrack? _track;
    private TaskCompletionSource? _started, _playing, _ended;
    // Where this track's audio goes, from the moment it starts (not before: becoming the active
    // device resumes Soloist's previous track first).
    private IPcmSink? _trackSink;
    private Action? _onPlaying;

    /// <param name="options">The user's Soloist settings, read fresh on each start; null when
    /// Soloist isn't set up (no key or no soloist.exe).</param>
    /// <param name="tapDll">The bundled audio tap, or null when this build doesn't have it.</param>
    public SoloistPlaybackService(Func<SoloistOptions?> options, Func<string?>? tapDll = null)
    {
        _options = options;
        _tapDll = tapDll ?? AudioTap.FindDll;
    }

    /// <summary>A key, soloist.exe and the tap are all there: refused tracks can play here.</summary>
    public bool IsConfigured => _options() != null && _tapDll() != null;

    public TimeSpan Position
    {
        get
        {
            lock (_sync) return _track?.PositionAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ?? TimeSpan.Zero;
        }
    }

    private static void Log(string message)
    {
        Trace.WriteLine($"[hzn-soloist] {message}");
        ProcessConsole.Append("soloist", message);
    }

    /// <summary>
    /// Play <paramref name="trackUri"/> through Soloist until it ends; returns then. Throws
    /// <see cref="SoloistUnavailableException"/> when Soloist can't play (the message says why),
    /// <see cref="OperationCanceledException"/> on skip/stop.
    /// </summary>
    public async Task PlayTrackAsync(string trackUri, TimeSpan? duration, PumpContext ctx, Action? onPlaying,
                                     CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await EnsureStartedAsync(ct).ConfigureAwait(false);

        var track = new SoloistTrack(trackUri, duration, ctx.IsPaused);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _track = track;
            (_started, _playing, _ended) = (started, playing, ended);
            _onPlaying = onPlaying;
            _trackSink = new PauseAwareSink(ctx.Sink, ctx.IsPaused);
        }
        _routing.ResetFrames();
        _routing.Target = null;
        var pausedHere = false;
        try
        {
            if (!_isActive) await SendAsync("activate", ct).ConfigureAwait(false);
            await SendAsync("play", ct, ("uri", trackUri)).ConfigureAwait(false);

            await WaitStepAsync(started.Task, "Soloist didn't start the track", ct).ConfigureAwait(false);
            await WaitStepAsync(playing.Task, "Soloist started the track but no audio came", ct).ConfigureAwait(false);

            // Until it ends: follow the user's pause, and give up on a stream that's gone quiet.
            var lastFrames = _routing.Frames;
            var quiet = Stopwatch.StartNew();
            while (!ended.Task.IsCompleted)
            {
                if (ctx.IsPaused() != pausedHere)
                {
                    pausedHere = !pausedHere;
                    await SendAsync(pausedHere ? "pause" : "play", ct).ConfigureAwait(false);
                    quiet.Restart();
                }
                if (_routing.Frames != lastFrames || pausedHere)
                {
                    lastFrames = _routing.Frames;
                    quiet.Restart();
                }
                else if (quiet.Elapsed > StallTimeout)
                {
                    throw new InvalidOperationException(
                        $"Soloist stopped sending audio ({StallTimeout.TotalSeconds:0} s of silence).");
                }
                if (_process is { HasExited: true })
                    throw new SoloistUnavailableException("Soloist exited mid-track.");

                try { await ended.Task.WaitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
        finally
        {
            // Nothing more from this track (or the one Soloist autoplays after it) reaches the sink.
            _routing.Target = null;
            lock (_sync)
            {
                _track = null;
                _started = _playing = _ended = null;
                _onPlaying = null;
                _trackSink = null;
            }
            try { await SendAsync("pause", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        try { await SendAsync("seek", CancellationToken.None, ("position_ms", (long)position.TotalMilliseconds)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException) { Log($"seek: {ex.Message}"); }
    }

    private static async Task WaitStepAsync(Task step, string failure, CancellationToken ct)
    {
        try { await step.WaitAsync(TrackTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new InvalidOperationException($"{failure} within {TrackTimeout.TotalSeconds:0} s."); }
    }

    // -- Soloist's process, tap and socket --

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _socket is { State: WebSocketState.Open })
            {
                RequireLoggedIn();
                return;
            }
            await StopAsync().ConfigureAwait(false);

            var options = _options() ?? throw new SoloistUnavailableException(
                "Spotify Soloist isn't set up: install it in the Tools tab and add your Soloist key in the Spotify settings.");
            var dll = _tapDll() ?? throw new SoloistUnavailableException(
                "This build of Horizon Radio is missing horizon_audio_tap.dll, which Soloist playback needs.");

            Directory.CreateDirectory(options.DataDirectory);
            var portFile = Path.Combine(options.DataDirectory, "ws.port");
            File.Delete(portFile); // a stale one from a crash would point at nothing

            var psi = new ProcessStartInfo(options.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in Soloist.BuildArgs(options)) psi.ArgumentList.Add(arg);
            var process = Process.Start(psi) ?? throw new SoloistUnavailableException("Soloist didn't start.");
            ChildProcesses.TieToApp(process);
            process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) ProcessConsole.Append("soloist", line); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) ProcessConsole.Append("soloist", line); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _lifetime = new CancellationTokenSource();
            Log($"started soloist pid={process.Id}");

            // The tap goes in before Soloist opens any audio (it does when it first plays).
            try
            {
                _tap = await AudioTap.AttachAsync(process, dll, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SoloistUnavailableException($"Couldn't take Soloist's audio: {ex.Message}", ex);
            }
            _tap.Log += line => Log($"tap: {line}");
            var lifetime = _lifetime.Token;
            _ = Task.Run(() => _tap.RunAsync(_routing, lifetime), CancellationToken.None);

            var port = await WaitForPortAsync(process, portFile, ct).ConfigureAwait(false);
            _authKnown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loggedInSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), ct).ConfigureAwait(false);
            _socket = socket;
            _ = Task.Run(() => ReceiveAsync(socket, lifetime), CancellationToken.None);

            // Soloist says whether it's logged in as soon as we connect, but a paired Soloist is
            // still restoring its session then (measured: logged in ~1 s later), so a "no" gets a
            // few seconds to become a yes before it means "pair it".
            try { await _authKnown.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new SoloistUnavailableException("Soloist didn't report whether it's logged in."); }
            if (!_loggedIn)
            {
                try { await _loggedInSignal.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            RequireLoggedIn();
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void RequireLoggedIn()
    {
        if (_loggedIn) return;
        // Soloist stays running so the user can pair it now; the next track plays once they have.
        throw new SoloistUnavailableException(
            $"Soloist needs pairing once: in the Spotify app, pick the device \"{_options()?.DeviceName ?? Soloist.DefaultDeviceName}\".");
    }

    private static async Task<int> WaitForPortAsync(Process process, string portFile, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < StartTimeout)
        {
            if (process.HasExited)
            {
                throw new SoloistUnavailableException(process.ExitCode == Soloist.ExpiredExitCode
                    ? "This Soloist build has expired: update it in the Tools tab."
                    : $"Soloist exited at start (code {process.ExitCode}); see the Console tab.");
            }
            if (File.Exists(portFile))
            {
                try
                {
                    var text = (await File.ReadAllTextAsync(portFile, ct).ConfigureAwait(false)).Trim();
                    if (int.TryParse(text, out var port) && port > 0) return port;
                }
                catch (IOException) { } // still being written
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new SoloistUnavailableException("Soloist didn't open its control port in time.");
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (SoloistEvent.Parse(json) is { } e) Handle(e);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        Log("control connection closed");
    }

    private void Handle(SoloistEvent e)
    {
        if (e.LoggedIn is { } loggedIn && e.Type == "auth_state")
        {
            if (loggedIn != _loggedIn) Log(loggedIn ? "logged in" : "not logged in: pair it from the Spotify app");
            _loggedIn = loggedIn;
            _authKnown?.TrySetResult(loggedIn);
            if (loggedIn) _loggedInSignal?.TrySetResult();
        }
        if (e.IsActive is { } active) _isActive = active;
        if (e.Type == "error") Log($"error: {e.Message}");
        if (e.Type != "position_sync")
            Trace.WriteLine($"[hzn-soloist] event {e.Type} {e.Status ?? e.Command ?? ""} {e.Uri ?? ""}".TrimEnd());

        Action? onPlaying = null;
        lock (_sync)
        {
            if (_track == null) return;
            switch (_track.Apply(e))
            {
                case SoloistTrackStep.Started:
                    _routing.Target = _trackSink;
                    _started?.TrySetResult();
                    break;
                case SoloistTrackStep.Playing:
                    _routing.Target ??= _trackSink; // a replay can go straight to playing
                    _started?.TrySetResult();
                    _playing?.TrySetResult();
                    onPlaying = _onPlaying;
                    break;
                case SoloistTrackStep.Ended:
                    _routing.Target = null; // Soloist's autoplay mustn't reach the game
                    _ended?.TrySetResult();
                    break;
            }
        }
        try { onPlaying?.Invoke(); }
        catch (Exception ex) { Log($"onPlaying: {ex.Message}"); }
    }

    // {"type":"command","command":<command>, <fields>}
    private async Task SendAsync(string command, CancellationToken ct, params (string Name, object Value)[] fields)
    {
        var socket = _socket ?? throw new InvalidOperationException("Soloist isn't connected.");
        var body = new Dictionary<string, object> { ["type"] = "command", ["command"] = command };
        foreach (var (name, value) in fields) body[name] = value;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    private async Task StopAsync()
    {
        if (_lifetime != null) await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime?.Dispose();
        _lifetime = null;
        if (_socket != null)
        {
            _socket.Abort();
            _socket.Dispose();
            _socket = null;
        }
        if (_tap != null)
        {
            await _tap.DisposeAsync().ConfigureAwait(false);
            _tap = null;
        }
        if (_process != null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            _process.Dispose();
            _process = null;
        }
        _loggedIn = false;
        _isActive = false;
    }

    public async ValueTask DisposeAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { _startGate.Release(); }
        _startGate.Dispose();
        _sendGate.Dispose();
    }
}

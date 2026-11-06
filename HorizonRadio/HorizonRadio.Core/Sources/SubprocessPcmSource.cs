using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Generic "spawn a process, read raw s16/44.1k/stereo PCM from its
/// stdout, route stderr text upward" audio source. Mirrors the C++
/// <c>SubprocessSource</c>: realtime-paced pipe reads (so a stdout-
/// backed encoder like librespot doesn't run faster than wall-clock
/// and cycle through tracks instantly), per-line stderr forwarding,
/// kill-on-dispose via <see cref="Process.Kill(bool)"/>.
///
/// Subclasses or wrappers customize:
///   - the command line (executable + args)
///   - what to do with each stderr line (e.g. parse for track changes)
///   - the canonical Track info to publish at startup
///
/// .NET handles the pipe-inheritance footgun automatically — there's
/// no equivalent of the "close-our-end" dance needed in the Win32
/// version, because ProcessStartInfo.RedirectStandardOutput plumbs it
/// through a managed stream that we own exclusively.
/// </summary>
public sealed class SubprocessPcmSource(SubprocessPcmSource.Config config) : IAsyncDisposable
{
    /// <summary>Configuration knobs the wrapper supplies.</summary>
    public sealed class Config
    {
        public required string ExecutablePath { get; init; }
        public required string[] Args { get; init; }
        public string? WorkingDirectory { get; init; }

        /// <summary>Logical name this process's stderr is tagged with in
        /// the Console tab (e.g. "librespot", "ffmpeg"). Defaults to the
        /// executable's file name.</summary>
        public string? ToolName { get; init; }

        /// <summary>Frames per chunk read out of the child's stdout.
        /// 2048 ≈ 46 ms at 44.1 kHz, matches LocalFileSource.</summary>
        public int ReadChunkFrames { get; init; } = 2048;

        /// <summary>Redirect the child's stdin so the caller can feed it bytes via
        /// <see cref="StandardInput"/> (e.g. the radio source pipes ICY-stripped
        /// audio into ffmpeg's <c>pipe:0</c>). Default false — sources that hand the
        /// child a URL/file argument leave stdin attached to the console.</summary>
        public bool RedirectStdin { get; init; }

        /// <summary>Called for each non-empty stderr line. Used by
        /// wrappers to parse metadata events.</summary>
        public Action<string>? OnStderrLine { get; init; }
    }

    private Process? _process;
    private Task? _stderrTask;
    private CancellationTokenSource? _cts;
    private long _pcmBytes;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-subproc] {msg}");

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The child's stdin stream when <see cref="Config.RedirectStdin"/> is set,
    /// else null. The caller writes the child's input here (e.g. ICY-stripped audio into
    /// ffmpeg) and closes it to signal EOF, which lets the child flush and exit so
    /// <see cref="Completion"/> fires.</summary>
    public Stream? StandardInput => config.RedirectStdin ? _process?.StandardInput.BaseStream : null;

    /// <summary>Wall-clock audio emitted so far, derived from PCM bytes pushed
    /// at the canonical format. Stream wrappers (e.g. YouTube) use this as the
    /// playback position since they pace reads to real time and own transport.</summary>
    public TimeSpan Elapsed =>
        TimeSpan.FromSeconds((double)Interlocked.Read(ref _pcmBytes) / AudioFormat.BytesPerFrame / AudioFormat.SampleRate);

    /// <summary>Task that completes when the PCM read loop exits (EOF,
    /// cancellation, or stream error). Null until <see cref="StartAsync"/>
    /// has been called. Wrappers that drive a sequence of subprocesses
    /// (e.g. YouTube playlist) await this to know when one process is
    /// done so they can start the next.</summary>
    public Task? Completion { get; private set; }

    public async Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_process != null) throw new InvalidOperationException("already running");

        var psi = new ProcessStartInfo
        {
            FileName = config.ExecutablePath,
            WorkingDirectory = config.WorkingDirectory ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = config.RedirectStdin,
            // librespot (and most Rust programs) write UTF-8 to stderr,
            // but the default StreamReader uses the console code page,
            // which on US Windows is usually CP1252. That mangles any
            // multi-byte sequence — Japanese / Chinese / Korean track
            // titles arrive as mojibake. Force UTF-8 to round-trip
            // intact. StandardOutputEncoding only applies to the
            // TextReader path; our PCM ReadAsync against BaseStream
            // bypasses it, so this is safe to set.
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in config.Args) psi.ArgumentList.Add(arg);

        _process = Process.Start(psi)
                ?? throw new InvalidOperationException($"failed to spawn {config.ExecutablePath}");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Make sure the child dies if our process dies. ProcessStartInfo
        // doesn't expose Win32 job objects, but Process.Kill(true) in
        // the finally path is enough for the user-driven stop case.
        // For host-crash scenarios we'd need a managed job-object
        // wrapper — out of scope for the source migration.
        Log($"started pid={_process.Id} cmd={config.ExecutablePath}");

        _stderrTask = Task.Run(() => DrainStderrAsync(_process, _cts.Token), _cts.Token);
        Completion = Task.Run(() => ReadPcmLoopAsync(_process, sink, _cts.Token), _cts.Token);

        await Task.CompletedTask;
    }

    private async Task DrainStderrAsync(Process proc, CancellationToken ct)
    {
        var toolName = string.IsNullOrEmpty(config.ToolName)
            ? Path.GetFileNameWithoutExtension(config.ExecutablePath)
            : config.ToolName;
        try
        {
            using var reader = proc.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                Diagnostics.ProcessConsole.Append(toolName, line);
                try { config.OnStderrLine?.Invoke(line); }
                catch (Exception ex) { Log($"stderr handler threw: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"stderr drain: {ex.Message}"); }
    }

    private async Task ReadPcmLoopAsync(Process proc, IPcmSink sink, CancellationToken ct)
    {
        int chunkFrames = config.ReadChunkFrames;
        int bytesPerFrame = AudioFormat.BytesPerFrame;
        int chunkBytes = chunkFrames * bytesPerFrame;
        var buffer = new byte[chunkBytes];
        var samples = new short[chunkFrames * AudioFormat.Channels];

        var chunkPeriod = TimeSpan.FromMicroseconds(
            (long)chunkFrames * 1_000_000 / AudioFormat.SampleRate);

        var sw = Stopwatch.StartNew();
        var nextChunk = TimeSpan.Zero;

        Stream stdout;
        try { stdout = proc.StandardOutput.BaseStream; }
        catch (Exception ex) { Log($"stdout open: {ex.Message}"); return; }

        Interlocked.Exchange(ref _pcmBytes, 0);

        while (!ct.IsCancellationRequested)
        {
            // Read exactly `chunkBytes` or until EOF.
            int filled = 0;
            while (filled < chunkBytes)
            {
                int got;
                try
                {
                    got = await stdout.ReadAsync(buffer.AsMemory(filled, chunkBytes - filled), ct)
                                      .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log($"stdout read: {ex.Message}"); return; }
                if (got <= 0) { Log($"stdout EOF after {Interlocked.Read(ref _pcmBytes)} bytes"); return; }
                filled += got;
                Interlocked.Add(ref _pcmBytes, got);
            }

            // s16-LE byte buffer → short[] in one go via BlockCopy.
            Buffer.BlockCopy(buffer, 0, samples, 0, chunkBytes);
            sink.Send(samples);

            // Realtime pacing. Without this, librespot writes as fast
            // as we can read; its internal playback advances faster
            // than wall-clock, Spotify Connect logs every track played
            // in seconds, and Spotify cycles tracks endlessly.
            nextChunk += chunkPeriod;
            var now = sw.Elapsed;
            if (nextChunk > now)
            {
                try { await Task.Delay(nextChunk - now, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            else
            {
                nextChunk = now;  // re-anchor if we fell behind
            }
        }
    }

    public async Task StopAsync()
    {
        var proc = _process;
        if (proc == null) return;

        try { _cts?.Cancel(); } catch { }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
            }
        }
        catch (Exception ex) { Log($"kill: {ex.Message}"); }

        if (Completion != null) { try { await Completion.ConfigureAwait(false); } catch { } }
        if (_stderrTask != null) { try { await _stderrTask.ConfigureAwait(false); } catch { } }

        try { proc.Dispose(); } catch { }
        _process = null;
        Completion = null;
        _stderrTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

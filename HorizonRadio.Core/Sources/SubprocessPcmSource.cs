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
public sealed class SubprocessPcmSource : IAsyncDisposable
{
    /// <summary>Configuration knobs the wrapper supplies.</summary>
    public sealed class Config
    {
        public required string   ExecutablePath { get; init; }
        public required string[] Args           { get; init; }
        public string?           WorkingDirectory { get; init; }

        /// <summary>Frames per chunk read out of the child's stdout.
        /// 2048 ≈ 46 ms at 44.1 kHz, matches LocalFileSource.</summary>
        public int ReadChunkFrames { get; init; } = 2048;

        /// <summary>Called for each non-empty stderr line. Used by
        /// wrappers to parse metadata events.</summary>
        public Action<string>? OnStderrLine { get; init; }
    }

    private readonly Config _config;
    private Process?                 _process;
    private Task?                    _readerTask;
    private Task?                    _stderrTask;
    private CancellationTokenSource? _cts;

    public SubprocessPcmSource(Config config) { _config = config; }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-subproc] {msg}");

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_process != null) throw new InvalidOperationException("already running");

        var psi = new ProcessStartInfo
        {
            FileName               = _config.ExecutablePath,
            WorkingDirectory       = _config.WorkingDirectory ?? "",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = false,
            // librespot (and most Rust programs) write UTF-8 to stderr,
            // but the default StreamReader uses the console code page,
            // which on US Windows is usually CP1252. That mangles any
            // multi-byte sequence — Japanese / Chinese / Korean track
            // titles arrive as mojibake. Force UTF-8 to round-trip
            // intact. StandardOutputEncoding only applies to the
            // TextReader path; our PCM ReadAsync against BaseStream
            // bypasses it, so this is safe to set.
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in _config.Args) psi.ArgumentList.Add(arg);

        _process = Process.Start(psi)
                ?? throw new InvalidOperationException($"failed to spawn {_config.ExecutablePath}");
        _cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Make sure the child dies if our process dies. ProcessStartInfo
        // doesn't expose Win32 job objects, but Process.Kill(true) in
        // the finally path is enough for the user-driven stop case.
        // For host-crash scenarios we'd need a managed job-object
        // wrapper — out of scope for the source migration.
        Log($"started pid={_process.Id} cmd={_config.ExecutablePath}");

        _stderrTask = Task.Run(() => DrainStderrAsync(_process, _cts.Token));
        _readerTask = Task.Run(() => ReadPcmLoopAsync(_process, sink, _cts.Token));

        await Task.CompletedTask;
    }

    private async Task DrainStderrAsync(Process proc, CancellationToken ct)
    {
        try
        {
            using var reader = proc.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                try { _config.OnStderrLine?.Invoke(line); }
                catch (Exception ex) { Log($"stderr handler threw: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"stderr drain: {ex.Message}"); }
    }

    private async Task ReadPcmLoopAsync(Process proc, IPcmSink sink, CancellationToken ct)
    {
        int chunkFrames = _config.ReadChunkFrames;
        int bytesPerFrame = AudioFormat.BytesPerFrame;
        int chunkBytes    = chunkFrames * bytesPerFrame;
        var buffer        = new byte[chunkBytes];
        var samples       = new short[chunkFrames * AudioFormat.Channels];

        var chunkPeriod = TimeSpan.FromMicroseconds(
            (long)chunkFrames * 1_000_000 / AudioFormat.SampleRate);

        var sw = Stopwatch.StartNew();
        var nextChunk = TimeSpan.Zero;

        Stream stdout;
        try { stdout = proc.StandardOutput.BaseStream; }
        catch (Exception ex) { Log($"stdout open: {ex.Message}"); return; }

        ulong totalBytes = 0;

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
                if (got <= 0) { Log($"stdout EOF after {totalBytes} bytes"); return; }
                filled     += got;
                totalBytes += (ulong)got;
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

        if (_readerTask != null) { try { await _readerTask.ConfigureAwait(false); } catch { } }
        if (_stderrTask != null) { try { await _stderrTask.ConfigureAwait(false); } catch { } }

        try { proc.Dispose(); } catch { }
        _process    = null;
        _readerTask = null;
        _stderrTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

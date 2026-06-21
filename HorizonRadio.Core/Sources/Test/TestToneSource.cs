using System.Diagnostics;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Test;

/// <summary>
/// Diagnostic source: emits a 440 Hz sine wave continuously. Used to
/// verify the C# → IPC → DLL → FMOD bridge → game-audio path before
/// pointing real sources (Local files, Spotify) at it. If you hear a
/// tone in the in-game radio, the pipe is working.
///
/// Lives in Core because it's useful for any consumer that wants to
/// test the pipe (UI smoke test, future integration tests).
/// </summary>
public sealed class TestToneSource(double frequencyHz = 440.0, double amplitude = 0.15) : IAudioSource
{
    public string Id => "testtone";
    public string DisplayName => "Test Tone (440 Hz)";

    public event Action<Track>? TrackChanged;

    private CancellationTokenSource? _stopCts;
    private Task? _runLoop;

    public Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_runLoop != null) return Task.CompletedTask;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TrackChanged?.Invoke(new Track(
            Title: "Test Tone",
            Artist: $"{frequencyHz:F0} Hz sine wave",
            Album: null,
            AlbumArt: null,
            SourceId: Id,
            SourceDisplay: DisplayName,
            ExternalId: null));
        _runLoop = Task.Run(() => RunAsync(sink, _stopCts.Token), _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopCts?.Cancel();
        if (_runLoop != null) { try { await _runLoop.ConfigureAwait(false); } catch { } _runLoop = null; }
        _stopCts?.Dispose();
        _stopCts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        // 2048 frames per chunk @ 44.1 kHz ≈ 46.4 ms. Matches the DLL
        // reader's expected granularity and the LocalFileSource pacing.
        const int chunkFrames = 2048;
        var chunkPeriod = TimeSpan.FromMicroseconds(
            (long)chunkFrames * 1_000_000 / AudioFormat.SampleRate);

        var samples = new short[chunkFrames * AudioFormat.Channels];
        double phase = 0;
        double phaseInc = 2.0 * Math.PI * frequencyHz / AudioFormat.SampleRate;
        short peak = (short)(amplitude * short.MaxValue);

        var sw = Stopwatch.StartNew();
        var nextChunk = TimeSpan.Zero;

        Debug.WriteLine($"[hzn-tone] starting {frequencyHz} Hz");

        while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < chunkFrames; ++i)
            {
                short s = (short)(Math.Sin(phase) * peak);
                samples[i * 2 + 0] = s;
                samples[i * 2 + 1] = s;
                phase += phaseInc;
                if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
            }
            sink.Send(samples);

            nextChunk += chunkPeriod;
            var now = sw.Elapsed;
            if (nextChunk > now)
            {
                try { await Task.Delay(nextChunk - now, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            else { nextChunk = now; }
        }
    }
}

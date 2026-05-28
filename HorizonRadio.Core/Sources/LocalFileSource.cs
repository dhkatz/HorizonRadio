using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;
using NAudio.Wave;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Plays through a <see cref="Playlist"/> of local audio files. Decodes
/// each file via NAudio (MP3/WAV/FLAC built-in; OGG via NAudio.Vorbis),
/// resamples to the canonical 44.1 kHz s16 stereo, and paces the PCM
/// pump to wall-clock so the DLL ring buffer doesn't get stuffed.
///
/// Implements <see cref="ITransportControls"/>: pause/play/next/prev
/// all work — pause halts PCM pumping, next/prev cancel the current
/// file's decode loop and the outer playlist runner advances.
/// </summary>
public sealed class LocalFileSource : IAudioSource, ITransportControls
{
    public string Id           => "local";
    public string DisplayName  => "Local Files";

    public event Action<Track>? TrackChanged;
    public event Action<bool>?  PausedChanged;

    private readonly Playlist _playlist;

    private CancellationTokenSource? _stopCts;
    private Task?                    _runLoop;

    // Per-track CTS: cancelled to skip the current file (Next/Previous).
    // Recreated each loop iteration. Holds the direction we want to go
    // when it gets cancelled — Next or Previous — so the outer loop can
    // step the right way.
    private CancellationTokenSource? _trackCts;
    private bool                     _stepBackwards;

    // Pause state. Pump loop polls _paused; PauseGate is signaled when
    // we resume, so the loop can sleep efficiently while paused.
    private volatile bool _paused;
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    public LocalFileSource(Playlist playlist) { _playlist = playlist; }

    public Task StartAsync(IPcmSink sink, CancellationToken externalCt)
    {
        if (_runLoop != null) return Task.CompletedTask;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _runLoop = Task.Run(() => RunAsync(sink, _stopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopCts?.Cancel();
        _trackCts?.Cancel();
        _resumeGate.Set();                 // unblock pump if paused
        if (_runLoop != null)
        {
            try { await _runLoop.ConfigureAwait(false); }
            catch { }
            _runLoop = null;
        }
        _stopCts?.Dispose();
        _stopCts = null;
        _trackCts?.Dispose();
        _trackCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _resumeGate.Dispose();
    }

    // -- ITransportControls --

    public bool CanPause        => true;
    public bool CanSkipNext     => _playlist.Count > 1;
    public bool CanSkipPrevious => _playlist.Count > 1;
    public bool IsPaused        => _paused;

    public Task TogglePauseAsync()
    {
        _paused = !_paused;
        if (_paused) _resumeGate.Reset(); else _resumeGate.Set();
        PausedChanged?.Invoke(_paused);
        return Task.CompletedTask;
    }

    public Task NextAsync()
    {
        _stepBackwards = false;
        _trackCts?.Cancel();
        return Task.CompletedTask;
    }

    public Task PreviousAsync()
    {
        _stepBackwards = true;
        _trackCts?.Cancel();
        return Task.CompletedTask;
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-local] {msg}");

    private async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_playlist.Count == 0)
        {
            Log("playlist is empty; source idle");
            return;
        }

        const int ChunkFrames = 2048;
        var       chunkPeriod = TimeSpan.FromMicroseconds(
            (long)ChunkFrames * 1_000_000 / AudioFormat.SampleRate);

        while (!ct.IsCancellationRequested)
        {
            var path = _playlist.Current;
            if (path == null) break;

            // Fresh per-track CTS that links to the outer stop. Next/
            // Previous cancel just this one.
            using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _trackCts = trackCts;
            _stepBackwards = false;

            Log($"opening {Path.GetFileName(path)}");
            try
            {
                PublishTrackInfo(path);
                await PumpFileAsync(path, sink, ChunkFrames, chunkPeriod, trackCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Track-level cancel (Next/Previous). Fall through to the
                // playlist advance; direction depends on _stepBackwards.
            }
            catch (Exception ex)
            {
                Log($"decode failed for {path}: {ex.GetType().Name}: {ex.Message}");
            }

            if (_stepBackwards) _playlist.Previous();
            else                _playlist.Next();
        }
    }

    private void PublishTrackInfo(string path)
    {
        string title = Path.GetFileNameWithoutExtension(path);
        string artist = "";
        string? album = null;
        byte[]? art = null;

        try
        {
            using var tag = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(tag.Tag.Title))       title  = tag.Tag.Title!;
            if (tag.Tag.Performers is { Length: > 0 } artists)   artist = string.Join(", ", artists);
            if (!string.IsNullOrWhiteSpace(tag.Tag.Album))       album  = tag.Tag.Album;
            if (tag.Tag.Pictures is { Length: > 0 } pics)        art    = pics[0].Data.Data;
        }
        catch (Exception ex)
        {
            Log($"tag read failed for {path}: {ex.Message}");
        }

        TrackChanged?.Invoke(new Track(
            Title:         title,
            Artist:        artist,
            Album:         album,
            AlbumArt:      art,
            SourceId:      Id,
            SourceDisplay: DisplayName,
            ExternalId:    null));
    }

    private async Task PumpFileAsync(string path, IPcmSink sink,
                                     int chunkFrames,
                                     TimeSpan chunkPeriod,
                                     CancellationToken ct)
    {
        using var reader = OpenReader(path);
        ISampleProvider samples = reader;

        if (samples.WaveFormat.Channels == 1)
        {
            samples = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(samples);
        }

        if (samples.WaveFormat.SampleRate != AudioFormat.SampleRate)
        {
            samples = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(
                samples, AudioFormat.SampleRate);
        }

        var floatBuf = new float[chunkFrames * AudioFormat.Channels];
        var shortBuf = new short[chunkFrames * AudioFormat.Channels];

        var stopwatch = Stopwatch.StartNew();
        var nextChunk = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            // Honor pause: wait on the resume gate. Cancellation breaks
            // out of the wait immediately. Re-anchor the chunk schedule
            // on resume so we don't burst out a backlog.
            if (_paused)
            {
                _resumeGate.Wait(ct);
                stopwatch.Restart();
                nextChunk = TimeSpan.Zero;
                if (ct.IsCancellationRequested) return;
            }

            int read = samples.Read(floatBuf, 0, floatBuf.Length);
            if (read == 0) return;

            for (int i = 0; i < read;             ++i) shortBuf[i] = ToInt16(floatBuf[i]);
            for (int i = read; i < shortBuf.Length; ++i) shortBuf[i] = 0;

            sink.Send(shortBuf);

            nextChunk += chunkPeriod;
            var now = stopwatch.Elapsed;
            if (nextChunk > now)
            {
                try { await Task.Delay(nextChunk - now, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            else
            {
                nextChunk = now;
            }
        }
    }

    private static AudioFileReader OpenReader(string path)
    {
        return new AudioFileReader(path);
    }

    private static short ToInt16(float f)
    {
        if (f >  1f) f =  1f;
        if (f < -1f) f = -1f;
        return (short)(f * short.MaxValue);
    }
}

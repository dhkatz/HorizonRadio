using System.Diagnostics;
using System.Globalization;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// YouTube audio source. Resolves a single-video or playlist URL via
/// yt-dlp, then per-track spawns ffmpeg to decode the direct audio
/// stream into our canonical s16/44.1k/stereo PCM. Transport controls
/// (next/prev/pause) are supported when the URL expanded to a
/// multi-entry playlist.
///
/// Why two processes per track rather than one piped chain: yt-dlp's
/// direct-URL flow (`-f bestaudio -g`) returns a signed URL with a
/// few-hour expiry, so we always re-resolve immediately before
/// playback. ffmpeg then fetches that URL itself — letting us swap in
/// HLS / DASH formats later without changing the pump.
/// </summary>
public sealed class YouTubeSource(YouTubeOptions options) : IAudioSource, ITransportControls
{
    public string Id => "youtube";
    public string DisplayName => "YouTube";

    public event Action<Track>? TrackChanged;
    public event Action<bool>? PausedChanged;

    private CancellationTokenSource? _stopCts;
    private Task? _runLoop;
    private List<YtDlpClient.Entry> _entries = new();
    private int _cursor;

    // Per-track cancellation: separates "skip current track" from "stop
    // the source". NextAsync / PreviousAsync cancel just this; StopAsync
    // cancels _stopCts which is its parent.
    private CancellationTokenSource? _trackCts;
    private volatile bool _stepBackwards;
    // Set by RestartAsync: cancels the current entry but replays it instead
    // of advancing the cursor.
    private volatile bool _restartCurrent;

    private volatile bool _paused;
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    private static void Log(string msg) => Debug.WriteLine($"[hzn-yt] {msg}");

    // -- IAudioSource --

    public Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_runLoop != null) return Task.CompletedTask;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runLoop = Task.Run(() => RunAsync(sink, _stopCts.Token), _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopCts?.Cancel();
        _trackCts?.Cancel();
        _resumeGate.Set();
        if (_runLoop != null)
        {
            try
            {
                await _runLoop.ConfigureAwait(false);
            }
            catch
            {
            }

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

    public bool CanPause => true;
    public bool CanSkipNext => _entries.Count > 1;
    public bool CanSkipPrevious => _entries.Count > 1;
    public bool IsPaused => _paused;

    public Task TogglePauseAsync()
    {
        _paused = !_paused;
        if (_paused) _resumeGate.Reset();
        else _resumeGate.Set();
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

    public Task RestartAsync()
    {
        _restartCurrent = true;
        _trackCts?.Cancel();
        return Task.CompletedTask;
    }

    // -- Run loop --

    private async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        // 1) Resolve the playlist/video URL up front. A single Loading
        //    placeholder shows in the HUD until ffmpeg starts emitting
        //    PCM, mirroring SpotifyLibrespotSource's UX.
        TrackChanged?.Invoke(new Track(
            Title: "Resolving…",
            Artist: options.Url,
            Album: null,
            AlbumArt: null,
            SourceId: Id,
            SourceDisplay: DisplayName,
            ExternalId: null));

        try
        {
            _entries = (await YtDlpClient.EnumerateAsync(
                    options.YtDlpPath, options.Url, ct).ConfigureAwait(false))
                .AsList();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log($"enumerate failed: {ex.Message}");
            TrackChanged?.Invoke(new Track(
                Title: "yt-dlp failed",
                Artist: ex.Message,
                Album: null,
                AlbumArt: null,
                SourceId: Id,
                SourceDisplay: DisplayName,
                ExternalId: null));
            return;
        }

        if (_entries.Count == 0)
        {
            Log("no entries resolved; idle");
            return;
        }

        _cursor = 0;

        while (!ct.IsCancellationRequested && _cursor < _entries.Count && _cursor >= 0)
        {
            using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _trackCts = trackCts;
            _stepBackwards = false;

            var entry = _entries[_cursor];
            Log($"track {_cursor + 1}/{_entries.Count}: {entry.Title}");

            try
            {
                await PlayEntryAsync(entry, sink, trackCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Per-track skip; outer loop advances.
            }
            catch (Exception ex)
            {
                Log($"track {entry.Id} failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (ReferenceEquals(_trackCts, trackCts)) _trackCts = null;

            if (_restartCurrent) _restartCurrent = false; // replay same entry
            else _cursor += _stepBackwards ? -1 : +1;
            if (_cursor < 0) _cursor = 0; // clamp at start; don't wrap
        }
    }

    private async Task PlayEntryAsync(
        YtDlpClient.Entry entry, IPcmSink sink, CancellationToken ct)
    {
        // Resolve fresh URL + canonical metadata right before playback.
        YtDlpClient.Resolved resolved;
        try
        {
            resolved = await YtDlpClient.ResolveAsync(
                options.YtDlpPath, entry.WebpageUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"resolve {entry.Id} failed: {ex.Message}");
            // Fall back to the flat-playlist metadata so the HUD still
            // shows *something*, then bail to advance to the next entry.
            TrackChanged?.Invoke(EntryToTrack(entry, art: null));
            await Task.Delay(500, ct).ConfigureAwait(false);
            return;
        }

        var albumArt = resolved.ThumbnailUrl != null
            ? await TryDownloadThumbnailAsync(resolved.ThumbnailUrl, ct).ConfigureAwait(false)
            : null;

        TrackChanged?.Invoke(new Track(
            Title: resolved.Title,
            Artist: resolved.Uploader,
            Album: resolved.Album,
            AlbumArt: albumArt,
            SourceId: Id,
            SourceDisplay: DisplayName,
            ExternalId: $"youtube:{entry.Id}"));

        // ffmpeg -i <streamUrl> -f s16le -ac 2 -ar 44100 -vn -loglevel error pipe:1
        // -vn drops any video stream (some YouTube formats are muxed); we
        // only want audio. -f s16le on stdout matches the bridge format.
        var ffmpegArgs = BuildFfmpegArgs(resolved.StreamUrl);

        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pausingSink = new PausingSink(sink, _resumeGate, () => _paused, pumpCts.Token);

        await using var subproc = new SubprocessPcmSource(new SubprocessPcmSource.Config
        {
            ExecutablePath = options.FfmpegPath,
            Args = ffmpegArgs,
            ToolName = "ffmpeg",
            OnStderrLine = line => Log($"ffmpeg: {line}"),
        });

        await subproc.StartAsync(pausingSink, pumpCts.Token).ConfigureAwait(false);

        // Wait for ffmpeg to finish (EOF on stdout = full track played)
        // or for the per-track CTS to fire (Skip / Stop).
        if (subproc.Completion is { } completion)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch
            {
                /* StopAsync below handles cleanup */
            }
        }
    }

    private string[] BuildFfmpegArgs(string streamUrl)
    {
        // -reconnect 1 + -reconnect_streamed 1: ffmpeg will retry a
        // dropped HTTP read instead of EOF-ing the stream. YouTube's
        // googlevideo CDN occasionally tears down idle connections.
        var list = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
            "-i", streamUrl,
            "-vn",
            "-f", "s16le",
            "-ac", AudioFormat.Channels.ToString(CultureInfo.InvariantCulture),
            "-ar", AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture),
        };
        if (options.EnableVolumeNormalisation)
        {
            // loudnorm is the most permissive option for live decode:
            // single-pass, EBU R128-aligned, no two-pass measurement.
            list.Add("-af");
            list.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        list.Add("pipe:1");
        return list.ToArray();
    }

    private static async Task<byte[]?> TryDownloadThumbnailAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-yt] thumbnail fetch failed: {ex.Message}");
            return null;
        }
    }

    private Track EntryToTrack(YtDlpClient.Entry e, byte[]? art) => new(
        Title: e.Title,
        Artist: e.Uploader,
        Album: null,
        AlbumArt: art,
        SourceId: Id,
        SourceDisplay: DisplayName,
        ExternalId: $"youtube:{e.Id}");

    /// <summary>
    /// Wraps an IPcmSink with the pause gate. While paused, swallows
    /// the chunk and blocks the writer (ffmpeg) by NOT calling Send,
    /// instead waiting on the gate. Matches LocalFileSource semantics:
    /// pause holds the producer in place. ffmpeg's stdout pipe will
    /// back-pressure once its kernel buffer fills, so the upstream
    /// HTTP read also stalls — fine for short pauses, may cause a
    /// segmented stream to fall behind on very long pauses.
    /// </summary>
    private sealed class PausingSink(
        IPcmSink inner,
        ManualResetEventSlim gate,
        Func<bool> isPaused,
        CancellationToken ct)
        : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples)
        {
            if (isPaused())
            {
                try
                {
                    gate.Wait(ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return inner.Send(samples);
        }
    }
}

internal static class EnumerableExtensions
{
    public static List<T> AsList<T>(this IReadOnlyList<T> src)
        => src as List<T> ?? [.. src];
}

public sealed class YouTubeOptions
{
    public required string YtDlpPath { get; init; }
    public required string FfmpegPath { get; init; }
    public required string Url { get; init; }
    public bool EnableVolumeNormalisation { get; init; }
}

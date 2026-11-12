using System.Diagnostics;
using System.Text.Json;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Tools.FFmpeg;
using HorizonRadio.Tools.YtDlp;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// One YouTube video as a <see cref="PlayableItem"/>, decoded with ffmpeg into the canonical PCM.
/// Loaded ahead (<see cref="PrefetchAsync"/>, while the song before it plays), its audio is
/// downloaded whole into the <see cref="AudioCache"/>, so it starts at once and plays from the file,
/// network or not. Otherwise <see cref="PrepareAsync"/> resolves a fresh signed stream URL (and
/// canonical metadata/art) and it streams from that, while it downloads for next time. Either way
/// it seeks by starting ffmpeg again at the new place: the file if it has arrived, else the stream.
/// </summary>
public sealed class YouTubePlayableItem : PlayableItem
{
    private const string InfoFile = "info.json";
    private const string ArtFile = "art";

    private readonly YtDlpClient.Entry _entry;
    private readonly string _ytDlpPath;
    private readonly string _ffmpegPath;
    private readonly bool _normalise;
    private readonly AudioCache? _cache;

    private YtDlpClient.Resolved? _resolved;
    private volatile SubprocessPcmSource? _subproc;
    private volatile bool _playing;

    // A seek asked for while playing (ticks; -1 = none), and the signal that wakes the play for it.
    private long _seekTo = -1;
    private volatile TaskCompletionSource _seeked = NewSignal();

    /// <param name="cache">Where songs are downloaded ahead and kept; null to only ever stream.</param>
    public YouTubePlayableItem(YtDlpClient.Entry entry, string ytDlpPath, string ffmpegPath, bool normalise, AudioCache? cache = null)
    {
        _entry = entry;
        _ytDlpPath = ytDlpPath;
        _ffmpegPath = ffmpegPath;
        _normalise = normalise;
        _cache = cache;

        // Preliminary metadata from the flat-playlist entry — already parsed
        // ("Artist - Title" → split, channel as weak artist) so the queue/mix
        // lists show something sensible before the per-track resolve runs.
        Metadata = BuildTrack(entry.Id, entry.Title, entry.Uploader,
            track: null, artist: null, album: null, art: null, year: null);
    }

    // Prefer YouTube's canonical song fields (present for music videos); otherwise
    // fall back to the heuristic title parser. The downstream metadata pipeline can
    // still override any field per the user's policy (e.g. square art from Spotify).
    private static Track BuildTrack(
        string id, string rawTitle, string? uploader,
        string? track, string? artist, string? album, byte[]? art, int? year)
    {
        string title;
        string outArtist;
        if (!string.IsNullOrWhiteSpace(track) && !string.IsNullOrWhiteSpace(artist))
        {
            title = track!;
            outArtist = artist!;
        }
        else
        {
            var parsed = TitleArtistParser.Parse(rawTitle, uploader);
            title = parsed.Title;
            outArtist = parsed.Artist ?? uploader ?? "";
        }

        return new Track(
            Title: title,
            Artist: outArtist,
            Album: album,
            AlbumArt: art,
            SourceId: "youtube",
            SourceDisplay: "YouTube",
            ExternalId: $"youtube:{id}",
            Year: year);
    }

    // Where ffmpeg started this time (resuming mid-track, or a seek), plus how far it has decoded since.
    private TimeSpan _startOffset;

    public override TimeSpan Position =>
        _subproc is { } subproc ? _startOffset + subproc.Elapsed : _playing ? _startOffset : TimeSpan.Zero;

    public override bool CanSeek => _playing;

    public override void Seek(TimeSpan position)
    {
        if (!_playing) return;
        Interlocked.Exchange(ref _seekTo, Math.Max(0, position.Ticks));
        _seeked.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Log(string msg) => Trace.WriteLine($"[hzn-yt-item] {msg}");

    // Ready to play: from the download when it's here, else a stream resolved now.
    public override Task PrepareAsync(CancellationToken ct) => PrepareOnceAsync(async token =>
    {
        if (Downloaded() == null) await ResolveAsync(token).ConfigureAwait(false);
    }, ct);

    /// <summary>Download the song (unless it's here already), so it plays from the file. If the
    /// download fails, it streams, resolved now.</summary>
    public override async Task PrefetchAsync(CancellationToken ct)
    {
        if (_cache != null && await _cache.FetchAsync(_entry.Id, DownloadAsync, ct).ConfigureAwait(false) is { } folder
            && FromDownload(folder) != null)
        {
            return;
        }
        await PrepareAsync(ct).ConfigureAwait(false);
    }

    // Fill a cache folder: the audio, what's known about the song, and its thumbnail.
    private async Task DownloadAsync(string folder, CancellationToken ct)
    {
        var got = await YtDlpClient.DownloadAudioAsync(_ytDlpPath, _entry.WebpageUrl, folder, ct).ConfigureAwait(false);
        var meta = got.Meta;
        if (meta.ThumbnailUrl != null && await TryDownloadThumbnailAsync(meta.ThumbnailUrl, ct).ConfigureAwait(false) is { } art)
            await File.WriteAllBytesAsync(Path.Combine(folder, ArtFile), art, ct).ConfigureAwait(false);
        var info = new DownloadInfo(Path.GetFileName(got.AudioPath), meta.Title, meta.Uploader, meta.Track, meta.Artist,
            meta.Album, meta.ReleaseYear, got.Duration is { } d ? (long)d.TotalMilliseconds : null);
        await File.WriteAllTextAsync(Path.Combine(folder, InfoFile), JsonSerializer.Serialize(info), ct).ConfigureAwait(false);
    }

    private sealed record DownloadInfo(string Audio, string Title, string Uploader, string? Track, string? Artist,
        string? Album, int? Year, long? DurationMs);

    // The downloaded audio when the song is here, with its metadata taken in; else null.
    private string? Downloaded() => _cache?.Find(_entry.Id) is { } folder ? FromDownload(folder) : null;

    private string? FromDownload(string folder)
    {
        try
        {
            var info = JsonSerializer.Deserialize<DownloadInfo>(File.ReadAllText(Path.Combine(folder, InfoFile)));
            if (info is null || Path.Combine(folder, info.Audio) is not { } audio || !File.Exists(audio)) return null;
            var artPath = Path.Combine(folder, ArtFile);
            var art = File.Exists(artPath) ? File.ReadAllBytes(artPath) : null;
            if (info.DurationMs is { } ms) Duration = TimeSpan.FromMilliseconds(ms);
            Metadata = BuildTrack(_entry.Id, info.Title, info.Uploader, info.Track, info.Artist, info.Album, art, info.Year);
            return audio;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log($"the download of {_entry.Id} can't be read: {ex.Message}");
            return null;
        }
    }

    private async Task ResolveAsync(CancellationToken ct)
    {
        try
        {
            var resolved = await YtDlpClient.ResolveAsync(_ytDlpPath, _entry.WebpageUrl, ct)
                .ConfigureAwait(false);

            var art = resolved.ThumbnailUrl != null
                ? await TryDownloadThumbnailAsync(resolved.ThumbnailUrl, ct).ConfigureAwait(false)
                : null;

            _resolved = resolved;
            Duration = resolved.Duration;
            Metadata = BuildTrack(_entry.Id, resolved.Title, resolved.Uploader,
                resolved.Track, resolved.Artist, resolved.Album, art, resolved.ReleaseYear);
        }
        catch (OperationCanceledException)
        {
            throw; // not a failure: the next PrepareAsync tries again
        }
        catch (Exception ex)
        {
            // Leave _resolved null; PlayAsync treats that as "skip this item" so
            // one dead video doesn't break the whole mix.
            Log($"resolve {_entry.Id} failed: {ex.Message}");
        }
    }

    public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
    {
        var file = Downloaded();
        if (file == null)
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            file = Downloaded();
            // Prepared from a download that's gone since (the cache was cleared): resolve it now.
            if (file == null && _resolved is null) await ResolveAsync(ct).ConfigureAwait(false);
        }
        if (file == null && _resolved is null)
        {
            // Couldn't resolve (dead/region-blocked/transient). Throttle before
            // returning so a playlist of failing entries advances at a sane pace
            // instead of hammering yt-dlp back-to-back.
            Log($"skipping unresolved entry {_entry.Id}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            return; // treated as a (very short) natural end; engine advances
        }

        // Streaming: download it meanwhile, so a seek and the next play read the file.
        if (file == null && _cache != null) _ = _cache.FetchAsync(_entry.Id, DownloadAsync, CancellationToken.None);

        ctx.OnStarted?.Invoke(this);

        // Resuming a parked station, or joining a play under way elsewhere: start where asked.
        _startOffset = ctx.StartPosition?.Invoke(this) is { Ticks: > 0 } start ? start : TimeSpan.Zero;
        Interlocked.Exchange(ref _seekTo, -1);
        _playing = true;
        try
        {
            while (await RunFfmpegAsync(file, ctx, ct).ConfigureAwait(false) is { } seekTo)
            {
                _startOffset = seekTo;
                file ??= Downloaded(); // a seek reads the download once it has arrived
            }
        }
        finally
        {
            _playing = false;
        }

        // Completion returns on EOF (natural) or cancellation (skip/stop); the
        // token tells which, so surface a cancel as OperationCanceledException.
        ct.ThrowIfCancellationRequested();
    }

    // Decode from _startOffset (the file, else the stream) until it ends or is cancelled (null), or
    // until a seek asks to start again elsewhere (where).
    private async Task<TimeSpan?> RunFfmpegAsync(string? file, PumpContext ctx, CancellationToken ct)
    {
        var args = file != null
            ? Ffmpeg.BuildFileDecodeArgs(file, _normalise, _startOffset)
            : Ffmpeg.BuildUrlDecodeArgs(_resolved!.StreamUrl, _normalise, _startOffset);
        Log($"{_entry.Id}: playing from {(file != null ? "the download" : "the stream")} at {_startOffset.TotalSeconds:0.0} s");

        // Its own token, so a seek can stop it even while the sink holds it paused.
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pausing = new PausingSink(ctx.Sink, ctx.IsPaused, ctx.ResumeGate, run.Token);
        await using var subproc = new SubprocessPcmSource(new SubprocessPcmSource.Config
        {
            ExecutablePath = _ffmpegPath,
            Args = args,
            ToolName = "ffmpeg",
            OnStderrLine = line => Log($"ffmpeg: {line}"),
        });

        await subproc.StartAsync(pausing, run.Token).ConfigureAwait(false);
        _subproc = subproc;
        try
        {
            if (subproc.Completion is not { } completion) return null;
            while (true)
            {
                var seeked = _seeked.Task;
                if (await Task.WhenAny(completion, seeked).ConfigureAwait(false) == completion)
                {
                    await completion.ConfigureAwait(false);
                    return null;
                }
                _seeked = NewSignal();
                var to = Interlocked.Exchange(ref _seekTo, -1);
                if (to < 0) continue; // its seek was taken already
                await run.CancelAsync().ConfigureAwait(false);
                return new TimeSpan(to);
            }
        }
        finally
        {
            _subproc = null;
        }
    }

    public override async Task<Track?> TryGetMetadataAsync(CancellationToken ct)
    {
        // Metadata-only resolve (no stream URL warmed), so we can run it for upcoming
        // rows without coupling to the signed URL's expiry. Canonical track/artist
        // (when YouTube tagged it as music) give the metadata pipeline a strong query;
        // the thumbnail here is a non-square fallback the pipeline replaces with
        // square cover art when a provider matches. Does NOT touch _resolved or the prepare,
        // so playback still does its own fresh resolve.
        var meta = await YtDlpClient.ResolveMetadataAsync(_ytDlpPath, _entry.WebpageUrl, ct)
            .ConfigureAwait(false);
        if (meta is null) return null;

        var art = meta.ThumbnailUrl != null
            ? await TryDownloadThumbnailAsync(meta.ThumbnailUrl, ct).ConfigureAwait(false)
            : null;

        return BuildTrack(_entry.Id, meta.Title, meta.Uploader,
            meta.Track, meta.Artist, meta.Album, art, meta.ReleaseYear);
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
            Log($"thumbnail fetch failed: {ex.Message}");
            return null;
        }
    }
}

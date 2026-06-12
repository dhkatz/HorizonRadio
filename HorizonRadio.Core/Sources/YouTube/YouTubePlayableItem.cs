using System.Diagnostics;
using System.Globalization;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// One YouTube video as a <see cref="PlayableItem"/>. <see cref="PrepareAsync"/>
/// does the expensive bit — a yt-dlp resolve into a fresh signed stream URL plus
/// canonical metadata/art — so the mix engine can warm the next item ahead of
/// time and make the track transition gapless. <see cref="PlayAsync"/> then
/// decodes that stream with ffmpeg into the canonical PCM, exactly as
/// <see cref="YouTubeSource"/> does per track.
/// </summary>
public sealed class YouTubePlayableItem : PlayableItem
{
    private readonly YtDlpClient.Entry _entry;
    private readonly string _ytDlpPath;
    private readonly string _ffmpegPath;
    private readonly bool _normalise;

    private bool _prepared;
    private YtDlpClient.Resolved? _resolved;
    private volatile SubprocessPcmSource? _subproc;

    public YouTubePlayableItem(YtDlpClient.Entry entry, string ytDlpPath, string ffmpegPath, bool normalise)
    {
        _entry = entry;
        _ytDlpPath = ytDlpPath;
        _ffmpegPath = ffmpegPath;
        _normalise = normalise;

        // Preliminary metadata from the flat-playlist entry; PrepareAsync
        // upgrades it to the resolved canonical title/uploader/art.
        Metadata = new Track(
            Title: entry.Title,
            Artist: entry.Uploader,
            Album: null,
            AlbumArt: null,
            SourceId: "youtube",
            SourceDisplay: "YouTube",
            ExternalId: $"youtube:{entry.Id}");
    }

    public override TimeSpan Position => _subproc?.Elapsed ?? TimeSpan.Zero;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-yt-item] {msg}");

    public override async Task PrepareAsync(CancellationToken ct)
    {
        if (_prepared) return;
        _prepared = true;

        try
        {
            var resolved = await YtDlpClient.ResolveAsync(_ytDlpPath, _entry.WebpageUrl, ct)
                .ConfigureAwait(false);

            var art = resolved.ThumbnailUrl != null
                ? await TryDownloadThumbnailAsync(resolved.ThumbnailUrl, ct).ConfigureAwait(false)
                : null;

            _resolved = resolved;
            Duration = resolved.Duration;
            Metadata = new Track(
                Title: resolved.Title,
                Artist: resolved.Uploader,
                Album: resolved.Album,
                AlbumArt: art,
                SourceId: "youtube",
                SourceDisplay: "YouTube",
                ExternalId: $"youtube:{_entry.Id}");
        }
        catch (OperationCanceledException)
        {
            _prepared = false; // let a later attempt retry rather than skip silently
            throw;
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
        await PrepareAsync(ct).ConfigureAwait(false);
        if (_resolved is null)
        {
            // Couldn't resolve (dead/region-blocked/transient). Throttle before
            // returning so a playlist of failing entries advances at a sane pace
            // instead of hammering yt-dlp back-to-back.
            Log($"skipping unresolved entry {_entry.Id}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            return; // treated as a (very short) natural end; engine advances
        }

        ctx.OnStarted?.Invoke(this);

        var args = BuildFfmpegArgs(_resolved.StreamUrl, _normalise);
        var pausing = new PausingSink(ctx.Sink, ctx.IsPaused, ctx.ResumeGate, ct);

        await using var subproc = new SubprocessPcmSource(new SubprocessPcmSource.Config
        {
            ExecutablePath = _ffmpegPath,
            Args = args,
            ToolName = "ffmpeg",
            OnStderrLine = line => Log($"ffmpeg: {line}"),
        });

        await subproc.StartAsync(pausing, ct).ConfigureAwait(false);
        _subproc = subproc;
        try
        {
            if (subproc.Completion is { } completion)
                await completion.ConfigureAwait(false);
        }
        finally
        {
            _subproc = null;
        }

        // Completion returns on EOF (natural) or cancellation (skip/stop); the
        // token tells which, so surface a cancel as OperationCanceledException.
        ct.ThrowIfCancellationRequested();
    }

    private static string[] BuildFfmpegArgs(string streamUrl, bool normalise)
    {
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
        if (normalise)
        {
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
            Log($"thumbnail fetch failed: {ex.Message}");
            return null;
        }
    }
}

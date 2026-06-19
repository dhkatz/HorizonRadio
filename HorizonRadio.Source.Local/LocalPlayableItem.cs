using System.Diagnostics;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using NAudio.Wave;

namespace HorizonRadio.Core.Sources.Local;

/// <summary>
/// One local audio file as a <see cref="PlayableItem"/>. Decodes via NAudio
/// (MP3/WAV/FLAC built-in; OGG via NAudio.Vorbis), resamples to the canonical
/// 44.1 kHz s16 stereo, and paces the PCM pump to wall-clock so the DLL ring
/// buffer doesn't get stuffed — the same decode path <see cref="LocalFileSource"/>
/// uses, here as a self-contained leaf the mix engine can sequence.
/// </summary>
public sealed class LocalPlayableItem : PlayableItem
{
    /// <summary>Namespace prefix for a local file's <see cref="Track.ExternalId"/>, so a path
    /// id is distinguishable from a remote source's id and round-trips to a re-playable locator
    /// (see <see cref="ReplayLocator"/>). Public so play-history replay can strip it.</summary>
    public const string ExternalIdPrefix = "local:";

    /// <summary>Build the <see cref="Track.ExternalId"/> for a local file path.</summary>
    public static string LocalExternalId(string path) => ExternalIdPrefix + path;

    private readonly string _path;
    private bool _prepared;
    private long _positionTicks;
    private long _pendingSeekTicks = -1; // -1 = none; the pump applies it next chunk

    public LocalPlayableItem(string path)
    {
        _path = path;
        // Preliminary metadata: parse the filename ("Artist - Title") so lists show
        // something sensible before tags are read in PrepareAsync.
        var parsed = TitleArtistParser.Parse(Path.GetFileNameWithoutExtension(path));
        Metadata = new Track(
            Title: parsed.Title,
            Artist: parsed.Artist ?? "",
            Album: null,
            AlbumArt: null,
            SourceId: "local",
            SourceDisplay: "Local Files",
            // The file path, namespaced like other sources' ids ("youtube:…", "radio:…"), so
            // play history can replay the same file. Providers only special-case "spotify:track:"
            // ids, so a "local:" id is inert to enrichment (the cache key stays text-based).
            ExternalId: LocalExternalId(path));
    }

    public override TimeSpan Position => new(Interlocked.Read(ref _positionTicks));

    public override bool CanSeek => true;

    public override void Seek(TimeSpan position)
    {
        var ticks = position.Ticks < 0 ? 0 : position.Ticks;
        // The pump thread owns the reader; hand it the target and reflect it in
        // Position immediately so the bar tracks the drag without lag.
        Interlocked.Exchange(ref _pendingSeekTicks, ticks);
        Interlocked.Exchange(ref _positionTicks, ticks);
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-local-item] {msg}");

    public override Task PrepareAsync(CancellationToken ct)
    {
        if (_prepared) return Task.CompletedTask;
        _prepared = true;

        // Tags are cheap local reads — fold metadata + duration in here so an
        // engine that warms the next item also pre-fetches its HUD info.
        try
        {
            using var tag = TagLib.File.Create(_path);

            // Fall back to parsing the filename only where a tag is missing, so a
            // tagless "Artist - Title.mp3" still splits sensibly.
            var hasTagTitle = !string.IsNullOrWhiteSpace(tag.Tag.Title);
            var hasTagArtist = tag.Tag.Performers is { Length: > 0 };
            var parsed = hasTagTitle && hasTagArtist
                ? null
                : TitleArtistParser.Parse(Path.GetFileNameWithoutExtension(_path));

            var title = hasTagTitle ? tag.Tag.Title! : parsed!.Title;
            var artist = hasTagArtist
                ? string.Join(", ", tag.Tag.Performers)
                : parsed!.Artist ?? "";
            var album = !string.IsNullOrWhiteSpace(tag.Tag.Album) ? tag.Tag.Album : null;
            var art = tag.Tag.Pictures is { Length: > 0 } pics ? pics[0].Data.Data : null;
            var year = tag.Tag.Year is > 0 and <= 9999 ? (int?)tag.Tag.Year : null;
            var trackNo = tag.Tag.Track is > 0 ? (int?)tag.Tag.Track : null;

            Metadata = new Track(title, artist, album, art, "local", "Local Files",
                ExternalId: LocalExternalId(_path), Year: year, TrackNumber: trackNo);
            if (tag.Properties?.Duration is { Ticks: > 0 } dur) Duration = dur;
        }
        catch (Exception ex)
        {
            Log($"tag read failed for {_path}: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public override async Task<Track?> TryGetMetadataAsync(CancellationToken ct)
    {
        // Cheap: a tag read is the same work PrepareAsync does, with no audio decode.
        await PrepareAsync(ct).ConfigureAwait(false);
        return Metadata;
    }

    public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
    {
        await PrepareAsync(ct).ConfigureAwait(false);
        ctx.OnStarted?.Invoke(this);

        const int chunkFrames = 2048;
        var chunkPeriod = TimeSpan.FromMicroseconds(
            (long)chunkFrames * 1_000_000 / AudioFormat.SampleRate);

        await using var reader = new AudioFileReader(_path);
        ISampleProvider samples = reader;

        if (samples.WaveFormat.Channels == 1)
            samples = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(samples);
        if (samples.WaveFormat.SampleRate != AudioFormat.SampleRate)
            samples = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(samples, AudioFormat.SampleRate);

        if (Duration is null && reader.TotalTime.Ticks > 0) Duration = reader.TotalTime;
        Interlocked.Exchange(ref _positionTicks, 0);
        Interlocked.Exchange(ref _pendingSeekTicks, -1);

        var floatBuf = new float[chunkFrames * AudioFormat.Channels];
        var shortBuf = new short[chunkFrames * AudioFormat.Channels];

        var stopwatch = Stopwatch.StartNew();
        var nextChunk = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            if (ctx.IsPaused())
            {
                ctx.ResumeGate.Wait(ct);
                stopwatch.Restart();
                nextChunk = TimeSpan.Zero;
                if (ct.IsCancellationRequested) break;
            }

            // Apply a pending seek before reading the next chunk — only the pump
            // thread touches the reader, so this never races the decode.
            var seek = Interlocked.Exchange(ref _pendingSeekTicks, -1);
            if (seek >= 0)
            {
                try { reader.CurrentTime = new TimeSpan(seek); }
                catch (Exception ex) { Log($"seek failed: {ex.Message}"); }
                stopwatch.Restart();
                nextChunk = TimeSpan.Zero;
            }

            int read = samples.Read(floatBuf, 0, floatBuf.Length);
            if (read == 0) return; // natural end of file

            Interlocked.Exchange(ref _positionTicks, reader.CurrentTime.Ticks);

            for (int i = 0; i < read; ++i) shortBuf[i] = ToInt16(floatBuf[i]);
            for (int i = read; i < shortBuf.Length; ++i) shortBuf[i] = 0;

            ctx.Sink.Send(shortBuf);

            nextChunk += chunkPeriod;
            var now = stopwatch.Elapsed;
            if (nextChunk > now)
                await Task.Delay(nextChunk - now, ct).ConfigureAwait(false);
            else
                nextChunk = now;
        }

        ct.ThrowIfCancellationRequested();
    }

    private static short ToInt16(float f)
    {
        if (f > 1f) f = 1f;
        if (f < -1f) f = -1f;
        return (short)(f * short.MaxValue);
    }
}

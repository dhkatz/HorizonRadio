using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Radio;

/// <summary>
/// One internet-radio station as a <see cref="PlayableItem"/>. Unlike a fixed-track
/// item, a station is a single, infinite live stream whose song changes underneath it:
/// we open the stream with <see cref="IcyStreamReader"/> (to see in-band ICY metadata),
/// pipe the clean audio into ffmpeg's stdin for decode to canonical PCM, and republish
/// the now-playing track via <see cref="PumpContext.OnMetadataUpdated"/> on every song
/// change — which lets the metadata pipeline fetch real cover art for the live song.
///
/// On a stream drop it reconnects with backoff; the station only ends when the queue
/// skips/stops it (cancellation).
/// </summary>
public sealed class RadioPlayableItem : PlayableItem
{
    private RadioStation _station;
    private readonly string _ffmpegPath;

    private bool _prepared;
    private byte[]? _stationArt;
    private volatile SubprocessPcmSource? _subproc;

    public RadioPlayableItem(RadioStation station, string ffmpegPath)
    {
        _station = station;
        _ffmpegPath = ffmpegPath;
        Metadata = BuildTrack(null, null);
    }

    // A live stream has no fixed length (base Duration stays null → no scrub bar) and
    // can't be seeked; report wall-clock elapsed from the decoder as the position so
    // the HUD can show time-on-station.
    public override TimeSpan Position => _subproc?.Elapsed ?? TimeSpan.Zero;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-radio] {msg}");

    private Track BuildTrack(string? songTitle, string? songArtist, IReadOnlyList<TitleCandidate>? candidates = null)
    {
        // Before a song is known, show the station name as the title (radio-player
        // convention). Once an ICY StreamTitle arrives, the song takes the foreground.
        bool haveSong = !string.IsNullOrWhiteSpace(songTitle);
        return new Track(
            Title: haveSong ? songTitle!.Trim() : _station.Name,
            Artist: songArtist?.Trim() ?? (haveSong ? "" : _station.Name),
            // Leave album to the metadata pipeline (the real album); don't seed the
            // station name, which — being source-priority — would beat the matched album.
            Album: null,
            // While only the station is known, show its logo as the art. Once a song
            // plays, leave AlbumArt empty so a provider's cover art wins, but offer the
            // logo as FallbackArt — the resolver uses it only when no cover is found
            // (e.g. a Niconico-only Vocaloid track absent from every catalog), so the
            // tile shows the station brand instead of a blank icon.
            AlbumArt: haveSong ? null : _stationArt,
            FallbackArt: _stationArt,
            SourceId: RadioSourceFactory.SourceId,
            SourceDisplay: "Internet Radio",
            // Per-song cache key for enrichment; null while only the station is known so
            // the station itself isn't cached as a song.
            ExternalId: haveSong ? $"radio:{songArtist?.Trim()} - {songTitle!.Trim()}" : null,
            // Alternative parses for the resolver to catalog-validate (only once a song is known).
            Candidates: haveSong ? candidates : null);
    }

    public override async Task PrepareAsync(CancellationToken ct)
    {
        if (_prepared) return;
        _prepared = true;
        if (_station.FaviconUrl is { } favicon)
        {
            _stationArt = await ImageDownload.TryGetAsync(ImageDownload.Shared, favicon, ct).ConfigureAwait(false);
            if (_stationArt != null) Metadata = BuildTrack(null, null); // attach station logo
        }
    }

    public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
    {
        await PrepareAsync(ct).ConfigureAwait(false);

        var pausing = new PausingSink(ctx.Sink, ctx.IsPaused, ctx.ResumeGate, ct);

        void OnTitle(string raw)
        {
            // Best-guess (artist, title) plus alternative interpretations the resolver validates
            // against the catalogs (channel-prefix, reversed order, fullwidth separators).
            var (primary, alts) = RadioStreamTitle.ParseCandidates(raw);
            // Strip "[Vocalist]…[Circle]" tags common on Vocaloid/doujin stations so the
            // displayed title reads as the song ("Sacred Secret"), not the tagged blob.
            var title = SearchTerms.StripBracketTags(primary.Title);
            var candidates = alts.Count == 0
                ? null
                : alts.Select(c => new TitleCandidate(c.Artist, SearchTerms.StripBracketTags(c.Title))).ToList();
            Metadata = BuildTrack(title, primary.Artist, candidates);
            ctx.OnMetadataUpdated?.Invoke(this);
        }

        bool announced = false;
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var icy = new IcyStreamReader(_station.StreamUrl);
            icy.StreamTitleChanged += OnTitle;
            SubprocessPcmSource? subproc = null;
            try
            {
                await icy.ConnectAsync(ct).ConfigureAwait(false);
                AdoptIcyNameIfBetter(icy.IcyName);

                subproc = new SubprocessPcmSource(new SubprocessPcmSource.Config
                {
                    ExecutablePath = _ffmpegPath,
                    Args = BuildFfmpegArgs(),
                    ToolName = "ffmpeg",
                    RedirectStdin = true,
                    OnStderrLine = line => Log($"ffmpeg: {line}"),
                });
                await subproc.StartAsync(pausing, ct).ConfigureAwait(false);
                _subproc = subproc;

                var stdin = subproc.StandardInput
                    ?? throw new InvalidOperationException("ffmpeg stdin unavailable");

                if (!announced)
                {
                    announced = true;
                    ctx.OnStarted?.Invoke(this);
                }
                attempt = 0; // a clean connect resets the backoff

                // Feed HTTP→ffmpeg until the stream ends or the decoder dies, then close
                // stdin so ffmpeg flushes and its read loop completes.
                try
                {
                    await icy.PumpToAsync(stdin, ct).ConfigureAwait(false);
                }
                finally
                {
                    try { stdin.Close(); } catch { }
                }
                if (subproc.Completion is { } completion)
                {
                    try { await completion.ConfigureAwait(false); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw; // per-track skip/stop
            }
            catch (Exception ex)
            {
                Log($"stream error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                icy.StreamTitleChanged -= OnTitle;
                await icy.DisposeAsync().ConfigureAwait(false);
                if (subproc != null)
                {
                    _subproc = null;
                    await subproc.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested) break;

            // Reconnect with capped exponential backoff (1,2,4,…,30s).
            attempt++;
            var secs = Math.Min(30, Math.Pow(2, Math.Min(attempt, 5)));
            Log($"reconnecting in {secs:0}s (attempt {attempt})");
            try { await Task.Delay(TimeSpan.FromSeconds(secs), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        ct.ThrowIfCancellationRequested();
    }

    // For the paste-a-URL path we start with no real station name; if the server sends
    // a nicer icy-name, adopt it (only when we don't already have a directory name).
    private void AdoptIcyNameIfBetter(string? icyName)
    {
        if (string.IsNullOrWhiteSpace(icyName)) return;
        if (!string.IsNullOrWhiteSpace(_station.Name) && _station.Name != _station.StreamUrl) return;
        _station = _station with { Name = icyName.Trim() };
        if (Metadata.ExternalId is null) Metadata = BuildTrack(null, null);
    }

    private static string[] BuildFfmpegArgs() =>
    [
        "-hide_banner",
        "-loglevel", "error",
        "-i", "pipe:0",
        "-vn",
        "-f", "s16le",
        "-ac", AudioFormat.Channels.ToString(CultureInfo.InvariantCulture),
        "-ar", AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture),
        "pipe:1",
    ];
}

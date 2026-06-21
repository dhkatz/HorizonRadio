using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Diagnostics;
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

    // Per-song cancellation for the optional title-extraction model: each new ICY title
    // cancels the previous (still-running) extraction, and _currentRaw guards a stale
    // result from overwriting a newer song's metadata.
    private readonly object _modelGate = new();
    private CancellationTokenSource? _modelCts;
    private volatile string? _currentRaw;

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
            Candidates: haveSong ? candidates : null,
            // The station placeholder isn't a song — don't let the providers search the station
            // name (it false-matches unrelated tracks and hijacks the logo). Only resolve real songs.
            Resolvable: haveSong);
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
            // Claim this as the current song under the gate before anyone reads _currentRaw, so a
            // still-running model task for the previous title sees the change and skips its publish.
            lock (_modelGate) { _currentRaw = raw; }

            // Best-guess (artist, title) plus alternative interpretations the resolver validates
            // against the catalogs (channel-prefix, reversed order, fullwidth separators).
            var (primary, alts, confidence) = RadioStreamTitle.ParseCandidates(raw);
            // Strip "[Vocalist]…[Circle]" tags common on Vocaloid/doujin stations so the
            // displayed title reads as the song ("Sacred Secret"), not the tagged blob.
            var title = SearchTerms.StripBracketTags(primary.Title);
            var candidates = alts.Count == 0
                ? null
                : alts.Select(c => new TitleCandidate(c.Artist, SearchTerms.StripBracketTags(c.Title))).ToList();
            PublishIfCurrent(raw, BuildTrack(title, primary.Artist, candidates), ctx);

            // Optional title-extraction model: it can split formats the deterministic parser
            // can't (no separators, reversed order, mixed-language). The deterministic result is
            // already on screen; the model refines in the background, and its hypotheses are
            // still catalog-validated, so a wrong extraction can't surface. Escalate only on a
            // shaky parse; Always runs on every title and promotes the model to the primary seed.
            var extractor = TitleExtractorRuntime.Current;
            var mode = TitleExtractorRuntime.Mode;
            bool runModel = ShouldRunModel(mode, extractor is not null, confidence);
            MetadataTrace.Song(_station.Name, raw, primary.Artist, title, confidence.ToString(), candidates,
                mode.ToString(), runModel);
            if (!runModel) return;

            CancellationToken modelCt;
            lock (_modelGate)
            {
                _modelCts?.Cancel();
                _modelCts?.Dispose();
                _modelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                modelCt = _modelCts.Token;
            }
            _ = EnhanceWithModelAsync(extractor!, mode, raw, ctx, modelCt);
        }

        bool announced = false;
        int attempt = 0;

        try
        {
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
        finally
        {
            lock (_modelGate)
            {
                try { _modelCts?.Cancel(); } catch { }
                _modelCts?.Dispose();
                _modelCts = null;
            }
        }
    }

    /// <summary>Runs the optional title model for one ICY title, in the background, and merges its
    /// extraction into the published metadata — appending fallback candidates (Escalate) or
    /// promoting the model's split to the primary seed (Always). Guards against the song changing
    /// while the model thinks; never throws for ordinary model failures.</summary>
    private async Task EnhanceWithModelAsync(
        ITitleExtractor extractor, TitleModelMode mode, string raw, PumpContext ctx, CancellationToken ct)
    {
        var swModel = Stopwatch.StartNew();
        IReadOnlyList<TitleCandidate> extracted;
        try
        {
            extracted = await extractor.ExtractAsync(raw, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log($"title model failed: {ex.GetType().Name}: {ex.Message}");
            MetadataTrace.Model(raw, swModel.ElapsedMilliseconds, [], applied: false);
            return;
        }
        swModel.Stop();

        var model = extracted
            .Select(c => new TitleCandidate(c.Artist, SearchTerms.StripBracketTags(c.Title)))
            .Where(c => !string.IsNullOrWhiteSpace(c.Title))
            .ToList();

        // The song moved on (or the stream stopped) while the model was thinking → drop it.
        var stale = ct.IsCancellationRequested || !string.Equals(_currentRaw, raw, StringComparison.Ordinal);
        var applied = !stale && model.Count > 0;
        MetadataTrace.Model(raw, swModel.ElapsedMilliseconds, model, applied);
        if (!applied) return;

        // What OnTitle already published deterministically (its primary on display + alternatives).
        var current = Metadata;
        var (title, artist, candidates) = ComposeWithModel(
            mode, model, new TitleCandidate(current.Artist, current.Title), current.Candidates ?? []);

        // Publish atomically and only while this is still the current song — the re-check and the
        // write happen under the same gate as OnTitle's publish, so a slow model result can never
        // clobber a newer title's metadata (the read of `current` above may be stale, but if so the
        // guard skips the write rather than persisting a frankenstein track).
        PublishIfCurrent(raw, BuildTrack(title, artist, candidates.Count == 0 ? null : candidates), ctx);
    }

    /// <summary>Set <see cref="PlayableItem.Metadata"/> and fire the update callback, but only while
    /// <paramref name="raw"/> is still the current ICY title. The check + write are serialized with
    /// OnTitle's own publish via <see cref="_modelGate"/> so the background model task and a freshly
    /// arrived title can't race each other into a stale or torn state.</summary>
    private void PublishIfCurrent(string raw, Track track, PumpContext ctx)
    {
        lock (_modelGate)
        {
            if (!string.Equals(_currentRaw, raw, StringComparison.Ordinal)) return;
            Metadata = track;
        }
        ctx.OnMetadataUpdated?.Invoke(this);
    }

    /// <summary>Whether the title model should run for this parse: a model must be present and not
    /// <see cref="TitleModelMode.Off"/>; <see cref="TitleModelMode.Always"/> runs on every title,
    /// <see cref="TitleModelMode.Escalate"/> only when the deterministic parse is below
    /// <see cref="ParseConfidence.High"/>.</summary>
    internal static bool ShouldRunModel(TitleModelMode mode, bool hasExtractor, ParseConfidence confidence) =>
        hasExtractor && mode != TitleModelMode.Off
        && (mode == TitleModelMode.Always || confidence != ParseConfidence.High);

    /// <summary>Combine the model's extraction with the deterministic interpretation into the
    /// (display title, display artist, fallback candidates) to publish. <see
    /// cref="TitleModelMode.Always"/> promotes the model's top hypothesis to the display and keeps
    /// the deterministic split as a catalog-validated fallback behind it; <see
    /// cref="TitleModelMode.Escalate"/> keeps the deterministic primary on display and appends the
    /// model's hypotheses as fallbacks. <paramref name="model"/> is assumed non-empty and already
    /// bracket-stripped.</summary>
    internal static (string Title, string? Artist, List<TitleCandidate> Candidates) ComposeWithModel(
        TitleModelMode mode, IReadOnlyList<TitleCandidate> model,
        TitleCandidate deterministicPrimary, IReadOnlyList<TitleCandidate> deterministicAlts)
    {
        if (mode == TitleModelMode.Always)
        {
            var top = model[0];
            return (top.Title, top.Artist,
                MergeCandidates(top, [.. model.Skip(1), deterministicPrimary, .. deterministicAlts]));
        }

        return (deterministicPrimary.Title, deterministicPrimary.Artist,
            MergeCandidates(deterministicPrimary, [.. deterministicAlts, .. model]));
    }

    /// <summary>De-duplicated candidate list excluding <paramref name="primary"/> (the displayed
    /// interpretation) — by case-insensitive (artist, title), reusing the parser's own dedup rule
    /// (<see cref="RadioStreamTitle.SameCandidate"/>) so the two paths can't diverge.</summary>
    private static List<TitleCandidate> MergeCandidates(TitleCandidate primary, IEnumerable<TitleCandidate> rest)
    {
        var result = new List<TitleCandidate>();
        foreach (var c in rest)
        {
            if (string.IsNullOrWhiteSpace(c.Title)) continue;
            if (RadioStreamTitle.SameCandidate(c, primary) || result.Any(a => RadioStreamTitle.SameCandidate(a, c))) continue;
            result.Add(c);
        }
        return result;
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

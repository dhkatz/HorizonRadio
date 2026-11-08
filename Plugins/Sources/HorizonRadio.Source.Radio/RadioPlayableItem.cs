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
using HorizonRadio.Core.Fingerprints;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Tools.FFmpeg;

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
public sealed class RadioPlayableItem : PlayableItem, ILiveSongAudio
{
    private RadioStation _station;
    private readonly string _ffmpegPath;

    private byte[]? _stationArt;
    private volatile SubprocessPcmSource? _subproc;

    // Per-song cancellation for the optional title-extraction model: each new ICY title
    // cancels the previous (still-running) extraction, and _currentRaw guards a stale
    // result from overwriting a newer song's metadata.
    private readonly object _modelGate = new();
    private CancellationTokenSource? _modelCts;
    private volatile string? _currentRaw;

    // Where the current song began in the decoded audio of this connection (ticks), or
    // UnknownStart when it isn't known: tuned in mid-song, or the stream's rate wasn't known yet.
    private const long UnknownStart = long.MinValue;
    private long _songStartTicks = UnknownStart;

    // Which song is playing (ILiveSongAudio.SongId): every song, and every reconnect, gets a new one.
    private static long s_nextSongId;
    private long _songId = Interlocked.Increment(ref s_nextSongId);
    private readonly Lock _songLock = new();

    // What's been heard of this connection, to line the song up against a recording of it; only while
    // the station plays (a queue can hold many stations).
    private volatile LiveAudioBuffer? _heard;

    /// <summary>A new title is held until its audio plays; one this far ahead (or behind) means the
    /// byte-to-time estimate is off, and the title is shown at once, with no song position.</summary>
    private static readonly TimeSpan MaxLead = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLag = TimeSpan.FromSeconds(2);

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

    /// <summary>How far into the current song playback is, from where its title change falls in
    /// the audio; null for the song that was playing when the station was tuned in.</summary>
    public override TimeSpan? SongPosition =>
        _subproc is { } decoder && Interlocked.Read(ref _songStartTicks) is var start && start != UnknownStart
            ? decoder.Elapsed - new TimeSpan(start)
            : null;

    // A new song (or the stream starting over): where it began, if that's known.
    private void SetSongStart(TimeSpan? start)
    {
        lock (_songLock)
        {
            Interlocked.Exchange(ref _songStartTicks, start?.Ticks ?? UnknownStart);
            Interlocked.Exchange(ref _songId, Interlocked.Increment(ref s_nextSongId));
        }
    }

    // -- ILiveSongAudio: the song's place, found from its audio --

    public long SongId => Interlocked.Read(ref _songId);

    public LiveAudio? RecentAudio(TimeSpan span)
    {
        long song;
        TimeSpan from;
        lock (_songLock)
        {
            song = _songId;
            var start = Interlocked.Read(ref _songStartTicks);
            // The song playing when the station was tuned in began before what's been heard.
            from = start == UnknownStart || start < 0 ? TimeSpan.Zero : new TimeSpan(start);
        }
        if (_subproc is null || _heard?.Read(from, span) is not { } heard) return null;
        return SongId == song ? new LiveAudio(song, heard.Samples, Chromaprint.SampleRate, heard.Start) : null;
    }

    public bool SetSongPosition(long songId, TimeSpan streamTime, TimeSpan songTime)
    {
        lock (_songLock)
        {
            if (songId != _songId) return false;
            Interlocked.Exchange(ref _songStartTicks, (streamTime - songTime).Ticks);
            return true;
        }
    }

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

    public override Task PrepareAsync(CancellationToken ct) => PrepareOnceAsync(FetchLogoAsync, ct);

    private async Task FetchLogoAsync(CancellationToken ct)
    {
        if (_station.FaviconUrl is { } favicon)
        {
            _stationArt = await ImageDownload.TryGetAsync(ImageDownload.Shared, favicon, ct).ConfigureAwait(false);
            if (_stationArt != null) Metadata = BuildTrack(null, null); // attach station logo
        }
    }

    public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
    {
        await PrepareAsync(ct).ConfigureAwait(false);

        // Keep what's heard (past the pause gate, so a paused stream adds nothing) for lining songs up.
        var heard = _heard = new LiveAudioBuffer(TimeSpan.FromSeconds(90));
        var pausing = new PausingSink(new HeardSink(ctx.Sink, heard), ctx.IsPaused, ctx.ResumeGate, ct);

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
                SubprocessPcmSource? subproc = null;
                StreamClock? clock = null;
                int titles = 0;
                CancellationTokenSource? pending = null;

                // The stream is read a few seconds ahead of what's heard. Hold a new title until
                // its audio plays, so the title changes with the song (in game too), and note where
                // in the audio the song began: the lyrics follow the song from there.
                void OnIcyTitle(string raw, long byteOffset)
                {
                    pending?.Cancel();
                    if (titles++ == 0 || subproc is not { } decoder || clock is null)
                    {
                        // The song that was already playing when we tuned in: its start isn't known.
                        SetSongStart(null);
                        OnTitle(raw);
                        return;
                    }

                    var now = decoder.Elapsed;
                    if (clock.PlaysAt(byteOffset) is not { } startsAt || startsAt < now - MaxLag || startsAt > now + MaxLead)
                    {
                        ProcessConsole.Append("radio", $"song start unknown (rate {clock.Rate?.ToString("0", CultureInfo.InvariantCulture) ?? "?"} B/s): {raw}");
                        SetSongStart(null);
                        OnTitle(raw);
                        return;
                    }

                    ProcessConsole.Append("radio",
                        $"next song starts in {(startsAt - now).TotalSeconds:0.0} s (at {startsAt.TotalSeconds:0.0} s, {clock.Rate:0} B/s): {raw}");
                    var cts = pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _ = PublishWhenPlayingAsync(decoder, startsAt, raw, cts.Token);
                }

                async Task PublishWhenPlayingAsync(SubprocessPcmSource decoder, TimeSpan startsAt, string raw, CancellationToken token)
                {
                    try
                    {
                        while (decoder.Elapsed < startsAt) await Task.Delay(40, token).ConfigureAwait(false);
                        SetSongStart(startsAt);
                        OnTitle(raw);
                    }
                    catch (OperationCanceledException) { /* a newer title, or the stream ended */ }
                    catch (Exception ex) { Log($"title publish failed: {ex.Message}"); }
                }

                icy.StreamTitleChanged += OnIcyTitle;
                icy.AudioRead += bytes => { if (subproc is { } d) clock?.Sample(bytes, d.Elapsed); };
                try
                {
                    await icy.ConnectAsync(ct).ConfigureAwait(false);
                    AdoptIcyNameIfBetter(icy.IcyName);
                    clock = new StreamClock(icy.Bitrate);
                    heard.Reset();
                    SetSongStart(null); // the decoded audio starts over on a new connection

                    subproc = new SubprocessPcmSource(new SubprocessPcmSource.Config
                    {
                        ExecutablePath = _ffmpegPath,
                        Args = Ffmpeg.BuildStdinDecodeArgs(),
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
                    icy.StreamTitleChanged -= OnIcyTitle;
                    pending?.Cancel();
                    pending?.Dispose();
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
            _heard = null;
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
    /// cref="TitleModelMode.Escalate"/> keeps the deterministic primary on display and tries the
    /// model's hypotheses next, ahead of the parser's other guesses: the model only runs when the
    /// parse was shaky, which is exactly when its reading should beat them. <paramref
    /// name="model"/> is assumed non-empty and already bracket-stripped.</summary>
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
            MergeCandidates(deterministicPrimary, [.. model, .. deterministicAlts]));
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


    // Passes the audio on, keeping a copy of what's heard.
    private sealed class HeardSink(IPcmSink inner, LiveAudioBuffer heard) : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples)
        {
            heard.Write(samples);
            return inner.Send(samples);
        }
    }
}

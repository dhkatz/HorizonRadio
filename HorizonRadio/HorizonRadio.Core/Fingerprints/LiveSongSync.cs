using System.Globalization;
using System.Runtime.CompilerServices;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Fingerprints;

/// <summary>The recording a station's song was lined up against.</summary>
/// <param name="Locator">The page the recording came from.</param>
/// <param name="SongLength">How long the song lasts in it: lyrics timed to a recording of this
/// length fit.</param>
public sealed record SongSync(string Locator, TimeSpan SongLength);

/// <summary>
/// Finds where a radio station is in the song it's playing by lining its audio up against a recording
/// of the song, and keeps the station's song position there. The recordings tried are ones the song's
/// catalog entry links (VocaDB's videos), then search hits. A recording that doesn't line up isn't the
/// song and is never used; another version of the song (its own mix or edit) lines up only roughly, so
/// it's used until, and unless, the very recording the station plays turns up. Each recording's
/// fingerprint is stored, so the next time the song plays nothing is downloaded.
/// </summary>
public sealed class LiveSongSync
{
    /// <summary>The first try waits for this much of the song.</summary>
    private static readonly TimeSpan FirstWindow = TimeSpan.FromSeconds(12);

    /// <summary>The longest stretch lined up at once (one that fits more than one place grows to it).</summary>
    private static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(4);

    /// <summary>Once in place, check this often that the station still plays the recording in step
    /// (a radio edit cuts a section; a reconnect starts the clock over).</summary>
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);

    /// <summary>A recheck that finds the station more than this far from where it should be moves it.</summary>
    private static readonly TimeSpan Drift = TimeSpan.FromSeconds(0.2);

    /// <summary>Recordings longer than this aren't a song.</summary>
    private static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(15);

    /// <summary>To see where the station's version of a song starts in a recording, look this far
    /// either side of its title change (stations change titles seconds early or late).</summary>
    private static readonly TimeSpan BeforeTitle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AfterTitle = TimeSpan.FromSeconds(25);

    /// <summary>A station settling on a recording this long after its first sound has skipped a prelude:
    /// longer than any crossfade (0.7–3.3 s measured on a real station).</summary>
    private static readonly TimeSpan Prelude = TimeSpan.FromSeconds(6);

    /// <summary>The fingerprint finds where the station settles on a recording to within this, early.</summary>
    private static readonly TimeSpan SettleSlack = TimeSpan.FromSeconds(3);

    /// <summary>A recording that's neither clearly in nor out after this many tries is given up on.</summary>
    private const int MaxTries = 12;

    /// <summary>A recording that's still unclear this many times over a long enough stretch is another
    /// arrangement or a cover, not the song as the station plays it.</summary>
    private const int MaxUnsure = 3;

    private const int MaxDownloads = 3;
    private const int SearchHits = 2;

    private readonly Func<IReadOnlyList<IAudioSourceFactory>> _sources;
    private readonly ReferenceStore _store;
    private readonly MetadataResolver? _resolver;

    /// <summary>Waits between tries (a test seam: tests don't wait for real).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <param name="sources">The source factories; the ones that can fetch recordings
    /// (<see cref="IReferenceAudioSource"/>) and search for them (<see cref="ISearchSource"/>) are used.</param>
    /// <param name="resolver">For the recordings the song's catalog entries link.</param>
    public LiveSongSync(Func<IReadOnlyList<IAudioSourceFactory>> sources, ReferenceStore store, MetadataResolver? resolver)
    {
        _sources = sources;
        _store = store;
        _resolver = resolver;
    }

    /// <summary>
    /// Line the song playing on <paramref name="live"/> (described by <paramref name="track"/>) up
    /// against a recording of it, calling <paramref name="onSynced"/> whenever the song's place is
    /// taken from a recording (again if a closer one turns up), and keep it in step until
    /// <paramref name="ct"/> (the song changed) or the station moves on to another song.
    /// </summary>
    public async Task RunAsync(Track track, ILiveSongAudio live, Action<SongSync> onSynced, CancellationToken ct)
    {
        var run = new Run(live, live.SongId, ReferenceStore.SongKey(track.Artist, track.Title),
            $"{track.Artist} – {track.Title}", onSynced);
        try
        {
            // The closest version found so far, when it isn't the very recording the station plays.
            (StoredReference Reference, RecordingFit Fit)? close = null;
            (StoredReference Reference, RecordingFit Fit)? chosen = null;

            await foreach (var candidate in ReferencesAsync(track, run.Key, ct).ConfigureAwait(false))
            {
                var (outcome, fit) = await AlignAsync(run, candidate, ct).ConfigureAwait(false);
                if (outcome == Outcome.SongChanged) return;
                if (outcome == Outcome.Exact)
                {
                    chosen = (candidate, fit);
                    break;
                }
                if (outcome == Outcome.Close && (close is null || fit.ErrorRate < close.Value.Fit.ErrorRate))
                    close = (candidate, fit);
            }

            chosen ??= close;
            if (chosen is not { } best)
            {
                if (live.SongId == run.Song) ProcessConsole.Append("sync", $"no recording matched {run.Name}");
                return;
            }
            var reference = best.Reference with { MatchRate = best.Fit.ErrorRate };
            if (!run.Apply(reference, best.Fit)) return;
            _store.Save(run.Key, reference);

            // Heard from its start, the song shows whether the recording opens with a prelude the station
            // doesn't play (a music video's scene): then count the song's time from where the station
            // starts it, now and next time.
            if (reference.StationStart is null && await StationStartAsync(run, reference, best.Fit, ct).ConfigureAwait(false) is { } start)
            {
                reference = reference with { StationStart = start };
                if (!run.Apply(reference, best.Fit)) return;
                _store.Save(run.Key, reference);
            }
            await KeepInStepAsync(run, reference, best.Fit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The song changed.
        }
    }

    private enum Outcome { Exact, Close, Different, SongChanged }

    // One song being synced: where its place was last taken from.
    private sealed class Run(ILiveSongAudio live, long song, string key, string name, Action<SongSync> onSynced)
    {
        public long Song { get; } = song;
        private TimeSpan _songStart;
        public string Key { get; } = key;
        public string Name { get; } = name;
        public ILiveSongAudio Live { get; } = live;
        public StoredReference? Applied { get; private set; }

        // Take the song's place from this fit, counting from where the song starts in the recording
        // (uploads pad it differently); false when the station has moved on.
        public bool Apply(StoredReference reference, RecordingFit fit)
        {
            var songTime = fit.SongTime - reference.SongStart;
            if (!Live.SetSongPosition(Song, fit.StreamTime, songTime)) return false;
            if (Applied?.Locator != reference.Locator || _songStart != reference.SongStart)
            {
                Applied = reference;
                _songStart = reference.SongStart;
                ProcessConsole.Append("sync", string.Create(CultureInfo.InvariantCulture,
                    $"{Name}: {songTime.TotalSeconds:0.00} s into the song, which starts {reference.SongStart.TotalSeconds:0.00} s into {reference.Locator} ({fit.ErrorRate:P0} of bits differ)"));
                onSynced(new SongSync(reference.Locator, reference.SongLength));
            }
            return true;
        }
    }

    // The recordings to try, best first: stored ones that matched before (closest first), other stored
    // ones, the ones the song's catalog entries link, then search hits; downloading and storing the new
    // ones as they're needed.
    private async IAsyncEnumerable<StoredReference> ReferencesAsync(
        Track track, string key, [EnumeratorCancellation] CancellationToken ct)
    {
        var stored = _store.For(key);
        foreach (var reference in stored) yield return reference;

        var seen = new HashSet<string>(stored.Select(r => r.Locator), StringComparer.Ordinal);
        int downloads = 0;
        await foreach (var locator in LocatorsAsync(track, ct).ConfigureAwait(false))
        {
            if (downloads >= MaxDownloads) yield break;
            if (!seen.Add(locator)) continue;
            var fetcher = _sources().OfType<IReferenceAudioSource>().FirstOrDefault(s => s.CanFetch(locator));
            if (fetcher is null) continue;

            downloads++;
            var reference = await FetchAsync(fetcher, locator, ct).ConfigureAwait(false);
            if (reference is null) continue;
            _store.Save(key, reference);
            yield return reference;
        }
    }

    private async IAsyncEnumerable<string> LocatorsAsync(Track track, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_resolver is not null)
        {
            IReadOnlyList<PlayableRef> playables = [];
            try
            {
                playables = (await _resolver.ResolveDetailedAsync(track, ct, ResolvePriority.Background).ConfigureAwait(false)).Playables;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProcessConsole.Append("sync", $"couldn't look up the song's recordings: {ex.Message}");
            }
            // YouTube first: the same recording downloads in ~3 s there, against 20–40 s from Niconico.
            foreach (var p in playables.OrderBy(p => p.Service.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                yield return p.Url;
        }

        var query = $"{track.Artist} {track.Title}".Trim();
        foreach (var search in _sources().Where(s => s is IReferenceAudioSource).OfType<ISearchSource>())
        {
            IReadOnlyList<SearchResult> hits = [];
            try
            {
                hits = await search.SearchAsync(query, SearchHits, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProcessConsole.Append("sync", $"search for a recording failed: {ex.Message}");
            }
            foreach (var hit in hits.Where(h => h.Kind == SearchResultKind.Track && !(h.Duration > MaxLength)).Take(SearchHits))
                yield return hit.Locator;
        }
    }

    private static async Task<StoredReference?> FetchAsync(IReferenceAudioSource fetcher, string locator, CancellationToken ct)
    {
        try
        {
            var audio = await fetcher.FetchAsync(locator, Chromaprint.SampleRate, MaxLength, ct).ConfigureAwait(false);
            if (audio is null || audio.SampleRate != Chromaprint.SampleRate) return null;
            var fingerprint = await Task.Run(() => Chromaprint.Compute(audio.Samples), ct).ConfigureAwait(false);
            return new StoredReference(locator, audio.Duration, Silence.Before(audio.Samples, audio.SampleRate),
                Silence.After(audio.Samples, audio.SampleRate), fingerprint, MatchRate: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProcessConsole.Append("sync", $"couldn't fetch {locator}: {ex.Message}");
            return null;
        }
    }

    // Line the song up against one recording, as its audio comes in. The first fit is used right away
    // when nothing better is in place; whether this is the very recording the station plays (Exact) or
    // another version of the song (Close) is decided over a longer stretch.
    private async Task<(Outcome, RecordingFit)> AlignAsync(Run run, StoredReference reference, CancellationToken ct)
    {
        var window = FirstWindow;
        var none = new RecordingFit(MatchVerdict.Different, TimeSpan.Zero, TimeSpan.Zero, 1, 0);
        int unsure = 0;
        for (int tries = 0; tries < MaxTries;)
        {
            if (run.Live.SongId != run.Song) return (Outcome.SongChanged, none);
            var audio = run.Live.RecentAudio(window);
            if (audio is null || audio.SongId != run.Song || audio.Length < FirstWindow)
            {
                await Delay(Retry, ct).ConfigureAwait(false);
                continue;
            }

            tries++;
            var fit = await Task.Run(() => RecordingMatch.Locate(reference.Fingerprint, audio.Samples, audio.Start), ct)
                .ConfigureAwait(false);
            switch (fit.Verdict)
            {
                case MatchVerdict.Matched:
                    if (run.Applied is null && !run.Apply(reference, fit)) return (Outcome.SongChanged, none);
                    if (audio.Length >= RecordingMatch.Conclusive)
                        return (fit.ErrorRate <= RecordingMatch.ExactErrorRate ? Outcome.Exact : Outcome.Close, fit);
                    window = RecordingMatch.Conclusive;
                    break;
                case MatchVerdict.Different:
                    ProcessConsole.Append("sync", string.Create(CultureInfo.InvariantCulture,
                        $"{run.Name}: not {reference.Locator} ({fit.ErrorRate:P0} of bits differ)"));
                    return (Outcome.Different, fit);
                case MatchVerdict.Ambiguous:
                    // A repeated section: a longer stretch reaches past it.
                    window = Min(MaxWindow, window + TimeSpan.FromSeconds(10));
                    break;
                case MatchVerdict.Unsure when audio.Length >= RecordingMatch.Conclusive && ++unsure >= MaxUnsure:
                    ProcessConsole.Append("sync", string.Create(CultureInfo.InvariantCulture,
                        $"{run.Name}: not {reference.Locator} (still {fit.ErrorRate:P0} of bits differ)"));
                    return (Outcome.Different, fit);
                case MatchVerdict.Unsure:
                    // Too little to tell, or the station's audio from before the song came in.
                    window = Max(window, RecordingMatch.Conclusive);
                    break;
            }
            await Delay(Retry, ct).ConfigureAwait(false);
        }
        ProcessConsole.Append("sync", $"{run.Name}: gave up on {reference.Locator}");
        return (Outcome.Different, none);
    }

    // Where the station's version of the song starts in the recording, when the recording opens with a
    // prelude the station skips; null when it doesn't, or that can't be told (tuned in mid-song, or the
    // audio from before the song is gone). The station settles on the recording a crossfade after the
    // song starts, so only settling far past the recording's first sound means a prelude; the song then
    // starts about where the station changed its title.
    private async Task<TimeSpan?> StationStartAsync(Run run, StoredReference reference, RecordingFit placed, CancellationToken ct)
    {
        if (run.Live.TitleChange is not { } title) return null;
        for (int tries = 0; tries < MaxTries; tries++)
        {
            if (run.Live.SongId != run.Song) return null;
            var audio = run.Live.AudioBetween(title - BeforeTitle, title + AfterTitle);
            if (audio is null || audio.SongId != run.Song) return null;
            if (audio.Start + audio.Length < title + AfterTitle)
            {
                await Delay(Retry, ct).ConfigureAwait(false);
                continue;
            }
            // Nothing from before the title change kept (a reconnect): it can't show where the song began.
            if (audio.Start > title - BeforeTitle / 2) return null;

            var settles = await Task.Run(() => RecordingMatch.StationSettles(reference.Fingerprint, audio.Samples, audio.Start, placed), ct)
                .ConfigureAwait(false);
            if (settles is not { } s || s - reference.LeadIn <= Prelude || s >= reference.Duration) return null;

            var atTitle = placed.SongTime + (title - placed.StreamTime);
            var start = atTitle < s - Prelude ? s - Prelude : atTitle > s + SettleSlack ? s + SettleSlack : atTitle;
            ProcessConsole.Append("sync", string.Create(CultureInfo.InvariantCulture,
                $"{run.Name}: {reference.Locator} opens with a {(start - reference.LeadIn).TotalSeconds:0.0} s prelude the station doesn't play"));
            return start;
        }
        return null;
    }

    // Recheck now and then that the station still plays in step with the recording, and move it when it
    // doesn't (a section was cut, or the station skipped).
    private async Task KeepInStepAsync(Run run, StoredReference reference, RecordingFit placed, CancellationToken ct)
    {
        var (streamTime, songTime) = (placed.StreamTime, placed.SongTime);
        while (true)
        {
            await Delay(Recheck, ct).ConfigureAwait(false);
            if (run.Live.SongId != run.Song) return;
            var audio = run.Live.RecentAudio(RecordingMatch.Conclusive);
            if (audio is null || audio.SongId != run.Song) return;
            if (songTime + (audio.Start - streamTime) > reference.Duration) return; // past the recording's end

            var fit = await Task.Run(() => RecordingMatch.Locate(reference.Fingerprint, audio.Samples, audio.Start), ct)
                .ConfigureAwait(false);
            if (fit.Verdict != MatchVerdict.Matched) continue; // talk over it, a crossfade: nothing to go on

            var expected = songTime + (fit.StreamTime - streamTime);
            if ((fit.SongTime - expected).Duration() <= Drift) continue;
            if (!run.Apply(reference, fit)) return;
            ProcessConsole.Append("sync", string.Create(CultureInfo.InvariantCulture,
                $"{run.Name}: moved {(fit.SongTime - expected).TotalSeconds:+0.00;-0.00} s to stay in step"));
            (streamTime, songTime) = (fit.StreamTime, fit.SongTime);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

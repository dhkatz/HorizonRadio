using System;
using System.Linq;
using System.Threading;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.History;

/// <summary>
/// Records every song the app plays into a <see cref="PlayHistoryStore"/>. Subscribes to the one
/// signal that fires for every source — <see cref="SourceRunner.TrackChanged"/> — and:
///
///   • Collapses the radio mid-stream re-fires (same song, repeated metadata) by signature.
///   • Treats a song superseded within <see cref="SkipWindow"/> as a skip (not really heard) and
///     drops it — which also folds a freshly model-refined radio title back onto one entry.
///   • Captures a replay handle for a song played from a re-addressable source (its origin
///     locator). A freeform (radio) song has no playable origin; its sources and identification
///     are resolved lazily by the History view (a catalog-canonical search), not here.
///
/// Saving is debounced (a burst of track changes writes once) and also flushed on dispose.
/// </summary>
public sealed class PlayHistoryService : IDisposable
{
    /// <summary>A song superseded within this window of starting is treated as skipped (not really
    /// heard) and dropped — "a few seconds", deliberately short so two genuinely distinct songs a
    /// little apart are both kept. Deterministic radio re-fires are collapsed by signature, not this
    /// window; only a title-model parse that promotes a different primary mid-window relies on it.</summary>
    public static readonly TimeSpan SkipWindow = TimeSpan.FromSeconds(5);

    private const int SaveDebounceMs = 2000;

    private readonly PlayHistoryStore _store;
    private readonly SourceRunner? _runner;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _persist;

    private readonly object _gate = new();
    private PlayHistoryEntry? _current;
    private string? _currentSig;
    private DateTimeOffset _currentAt;

    private readonly Timer? _saveTimer;

    public PlayHistoryService(
        PlayHistoryStore store,
        SourceRunner runner,
        Func<DateTimeOffset>? clock = null,
        bool persist = true)
        : this(store, clock, persist)
    {
        _runner = runner;
        _runner.TrackChanged += OnTrackChanged;
    }

    // Core ctor without the runner subscription — the seam unit tests drive via Record().
    internal PlayHistoryService(PlayHistoryStore store, Func<DateTimeOffset>? clock, bool persist)
    {
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _persist = persist;
        if (_persist)
        {
            _saveTimer = new Timer(_ => _store.SaveToDisk(), null, Timeout.Infinite, Timeout.Infinite);
            _store.Changed += ScheduleSave;
        }
    }

    private void OnTrackChanged(Track t) => Record(t);

    /// <summary>Record one track change. Internal so tests can drive recording without a live
    /// source. Idempotent for repeated identical track changes (radio metadata re-fires).</summary>
    internal void Record(Track t)
    {
        // Skip non-songs: a radio station card before the first ICY title (Resolvable == false),
        // and the empty placeholder track.
        if (!t.Resolvable || string.IsNullOrWhiteSpace(t.Title)) return;

        var sig = Signature(t);
        lock (_gate)
        {
            if (sig == _currentSig) return; // same song re-fired
            var now = _clock();
            if (_current != null && now - _currentAt < SkipWindow)
                _store.Remove(_current.Id); // previous song was skipped/refined — don't keep it

            var entry = BuildEntry(t, now);
            _store.Add(entry);
            _current = entry;
            _currentSig = sig;
            _currentAt = now;
        }
    }

    private static PlayHistoryEntry BuildEntry(Track t, DateTimeOffset now)
    {
        var (replaySourceId, locator) = HistoryReplay.DeriveOrigin(t.ExternalId);
        var directlyReplayable = replaySourceId != null && locator != null;

        // A song played from a re-addressable source keeps that origin as its single replay source
        // and is "matched" by definition; a freeform song starts with no sources and an unknown
        // verdict, both filled in when the History view resolves it.
        IReadOnlyList<ReplaySource> sources = directlyReplayable
            ? [new ReplaySource(replaySourceId!, SourceCatalog.Find(replaySourceId!)?.DisplayName ?? t.SourceDisplay, locator!)]
            : [];

        return new PlayHistoryEntry
        {
            Id = Guid.NewGuid().ToString("n"),
            PlayedAt = now,
            Title = t.Title.Trim(),
            Artist = t.Artist.Trim(),
            Album = string.IsNullOrWhiteSpace(t.Album) ? null : t.Album,
            Year = t.Year,
            SourceId = t.SourceId,
            SourceDisplay = string.IsNullOrWhiteSpace(t.SourceDisplay) ? t.SourceId : t.SourceDisplay,
            MatchState = directlyReplayable ? HistoryMatchState.Matched : HistoryMatchState.Unknown,
            Sources = sources,
            Candidates = t.Candidates is { Count: > 0 } c
                ? c.Select(x => new HistoryCandidate(x.Artist, x.Title)).ToList()
                : [],
        };
    }

    private static string Signature(Track t) =>
        $"{t.SourceId}|{t.ExternalId}|{t.Title}|{t.Artist}";

    private void ScheduleSave() => _saveTimer?.Change(SaveDebounceMs, Timeout.Infinite);

    public void Dispose()
    {
        if (_runner != null) _runner.TrackChanged -= OnTrackChanged;
        if (_persist)
        {
            _store.Changed -= ScheduleSave;
            // Drain any in-flight debounce callback before the final save so the timer thread and
            // this thread don't write the file concurrently. Dispose(WaitHandle) signals when all
            // callbacks have completed; bound the wait so shutdown can't hang.
            if (_saveTimer != null)
            {
                using var drained = new ManualResetEvent(false);
                if (_saveTimer.Dispose(drained)) drained.WaitOne(TimeSpan.FromSeconds(5));
            }
            _store.SaveToDisk(); // flush the final state synchronously
        }
    }
}

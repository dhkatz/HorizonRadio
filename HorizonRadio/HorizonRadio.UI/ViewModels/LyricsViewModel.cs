using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Fingerprints;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.UI.ViewModels;

/// <summary>Where a lyric line is relative to the singer.</summary>
public enum LyricLineState { Upcoming, Active, Past }

/// <summary>One word of a karaoke line, and how much of it has been sung.</summary>
public sealed partial class KaraokeWordViewModel(KaraokeWord word) : ObservableObject
{
    public KaraokeWord Word { get; } = word;
    public string Text => Word.Text;
    public bool SpaceAfter => Word.SpaceAfter;

    /// <summary>0–1. Lines not being sung are drawn whole (1); their dimming is the line's.</summary>
    [ObservableProperty] private double fill = 1;
}

/// <summary>One line in the lyrics panel.</summary>
public sealed partial class LyricLineViewModel : ObservableObject
{
    public LyricLineViewModel(LyricLine line, bool following, IReadOnlyList<KaraokeWord>? words = null)
    {
        Line = line;
        IsFollowing = following;
        Words = words is { Count: > 0 } ? [.. words.Select(w => new KaraokeWordViewModel(w))] : [];
        state = following ? LyricLineState.Upcoming : LyricLineState.Active;
    }

    public LyricLine Line { get; }

    /// <summary>Synced and following the song: drawn as words that can fill as they're sung.</summary>
    public bool IsFollowing { get; }

    public IReadOnlyList<KaraokeWordViewModel> Words { get; }
    public bool HasWords => Words.Count > 0;

    // A timed empty line is a break in the singing; an empty line in a sheet separates verses.
    public string Text => Line.Text.Length > 0 ? Line.Text : IsFollowing ? "♪" : "";
    public bool IsBackground => Line.Background;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    private LyricLineState state;

    public double Opacity => State switch
    {
        LyricLineState.Active => 1.0,
        LyricLineState.Past => 0.4,
        _ => 0.6,
    } * (IsBackground ? 0.75 : 1.0);

    /// <summary>Fill the words as of <paramref name="time"/>, or whole when <paramref name="karaoke"/>
    /// is off or the line isn't being sung.</summary>
    public void UpdateFill(TimeSpan time, bool karaoke)
    {
        bool filling = karaoke && State == LyricLineState.Active;
        foreach (var w in Words) w.Fill = filling ? w.Word.FillAt(time) : 1;
    }
}

/// <summary>A version of the lyrics the listener can switch to (Original, Romaji, English).</summary>
public sealed partial class LyricsVersionViewModel(string label) : ObservableObject
{
    public string Label { get; } = label;
    [ObservableProperty] private bool isSelected;
}

/// <summary>
/// The lyrics panel on the Now Playing page: looks up the playing song's lyrics and, when they're
/// synced and the source reports where in the song it is, follows along, filling each line
/// karaoke-style as it's sung (word by word where the lyrics are word-timed). Otherwise (unsynced
/// lyrics, or radio before its place in the song is known) the lyrics show as a sheet. On radio,
/// synced lyrics have the station's audio lined up against a recording of the song (see
/// <see cref="LiveSongSync"/>), which tells where in the song it is even when it was tuned in mid-song.
/// </summary>
public sealed partial class LyricsViewModel : ViewModelBase, IDisposable
{
    private const string OriginalLabel = "Original";

    // Lets the enriched title (usually ~1 s after the raw one) arrive before the first lookup.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    // Sources report the position as each chunk of audio goes out (every ~40–100 ms). Between
    // reports the clock runs on by itself, but no further than this: a paused source stops.
    private static readonly TimeSpan MaxRunOn = TimeSpan.FromMilliseconds(250);

    private readonly LyricsService? _service;
    private readonly Func<IPlaybackProgress?> _progress;
    private readonly SourceConfigStore? _prefs;
    private readonly LiveSongSync? _sync;
    private readonly DispatcherTimer _timer;

    // Lining the radio station's audio up against a recording of the song, once per song.
    private CancellationTokenSource? _syncCts;
    private bool _syncStarted;

    private CancellationTokenSource? _cts;
    private string? _songKey;
    private Track? _track;
    private SongLyrics? _lyrics;
    private TimeSpan? _queriedDuration;
    private bool _lookedUp;
    private bool _following;

    private TimeSpan _reported = TimeSpan.MinValue;
    private long _reportedAt;
    private TimeSpan _shown;

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    /// <summary>The versions of the lyrics to switch between; empty when there's only one.</summary>
    public ObservableCollection<LyricsVersionViewModel> Versions { get; } = new();
    public bool HasVersions => Versions.Count > 1;

    /// <summary>What the panel says instead of lyrics ("Looking for lyrics…"), or null.</summary>
    [ObservableProperty] private string? status;
    [ObservableProperty] private string? credit;
    [ObservableProperty] private bool isVisible;

    /// <summary>The line being sung, or -1. The view keeps it in the middle of the panel.</summary>
    [ObservableProperty] private int activeIndex = -1;

    /// <summary>Whether clicking a line jumps there.</summary>
    [ObservableProperty] private bool canSeek;

    /// <summary>Whether the lyrics are following the song (and so can fill karaoke-style).</summary>
    [ObservableProperty] private bool isFollowing;

    /// <summary>Fill lines as they're sung, rather than lighting the whole line at once.</summary>
    [ObservableProperty] private bool isKaraoke;

    /// <summary>Following a live stream (radio), whose song position comes from where the title
    /// changed: close, but stations time that differently, so the listener can nudge it.</summary>
    [ObservableProperty] private bool isLive;

    /// <summary>How much earlier to show radio lyrics, in seconds (negative: later).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetText))]
    private double radioOffset;

    public string OffsetText => RadioOffset == 0 ? "In sync" : $"{RadioOffset:+0.0;-0.0} s";

    /// <summary>The radio song's place was found from its audio: the lyrics are on time, with no
    /// nudging needed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNudge))]
    private bool isAudioSynced;

    /// <summary>The nudge is for when the place comes from the station's title change.</summary>
    public bool ShowNudge => IsLive && !IsAudioSynced;

    /// <summary>What the audio matching is doing ("Matching the song's audio…"), or null.</summary>
    [ObservableProperty] private string? audioSyncStatus;

    /// <summary>Where the matched recording came from, for the status's tooltip.</summary>
    [ObservableProperty] private string? audioSyncSource;

    /// <summary>The song is on a live stream whose place can be found from its audio (radio).</summary>
    [ObservableProperty] private bool canAudioSync;

    /// <summary>Find radio songs' places from their audio (downloads a recording of each song once).</summary>
    [ObservableProperty] private bool isAudioSyncEnabled;

    public bool HasLines => Lines.Count > 0;

    public LyricsViewModel(LyricsService? service, Func<IPlaybackProgress?> progress, SourceConfigStore? prefs = null,
        LiveSongSync? sync = null)
    {
        _service = service;
        _progress = progress;
        _prefs = prefs;
        _sync = sync;
        isKaraoke = prefs?.LyricsKaraoke ?? true;
        isAudioSyncEnabled = prefs?.LyricsAudioSync ?? true;
        radioOffset = prefs?.LyricsRadioOffset ?? 0;
        // Fast enough for a smooth fill; it only runs while a song's lyrics are up.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        Lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLines));
        Versions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVersions));
    }

    [RelayCommand]
    private void Nudge(string? seconds)
    {
        if (!double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var by))
            return;
        RadioOffset = Math.Round(Math.Clamp(RadioOffset + by, -10, 10), 1);
        if (_prefs != null)
        {
            _prefs.LyricsRadioOffset = RadioOffset;
            _prefs.SaveToDisk();
        }
        Tick();
    }

    partial void OnIsLiveChanged(bool value) => OnPropertyChanged(nameof(ShowNudge));

    partial void OnIsAudioSyncEnabledChanged(bool value)
    {
        if (_prefs != null && _prefs.LyricsAudioSync != value)
        {
            _prefs.LyricsAudioSync = value;
            _prefs.SaveToDisk();
        }
        if (value)
        {
            if (_lyrics != null) StartAudioSync(_lyrics);
        }
        else
        {
            // The place found so far stays: the station has no better one for this song.
            StopAudioSync();
        }
    }

    partial void OnIsKaraokeChanged(bool value)
    {
        if (_prefs != null && _prefs.LyricsKaraoke != value)
        {
            _prefs.LyricsKaraoke = value;
            _prefs.SaveToDisk();
        }
        Tick();
    }

    /// <summary>The playing track changed or was enriched. On the UI thread.</summary>
    public void OnTrack(Track track)
    {
        if (_service is not { HasProviders: true }) return;
        if (!track.Resolvable || string.IsNullOrWhiteSpace(track.Title))
        {
            Clear();
            return;
        }

        var key = $"{track.Title.Trim()}\u001f{track.Artist.Trim()}";
        _track = track;
        if (key == _songKey) return;
        _songKey = key;

        Reset();
        IsVisible = true;
        Status = "Looking for lyrics…";
        var cts = _cts = new CancellationTokenSource();
        _timer.Start();
        _ = LoadAsync(track, settle: true, cts.Token);
    }

    /// <summary>Nothing is playing.</summary>
    public void Clear()
    {
        _songKey = null;
        _track = null;
        Reset();
        IsVisible = false;
    }

    private void Reset()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        StopAudioSync();
        _syncStarted = false;
        IsAudioSynced = false;
        AudioSyncStatus = null;
        AudioSyncSource = null;
        CanAudioSync = false;
        _timer.Stop();
        _following = false;
        IsFollowing = false;
        IsLive = false;
        _lookedUp = false;
        _queriedDuration = null;
        _lyrics = null;
        _reported = TimeSpan.MinValue;
        _shown = TimeSpan.Zero;
        Lines.Clear();
        Versions.Clear();
        ActiveIndex = -1;
        Status = null;
        Credit = null;
    }

    private async Task LoadAsync(Track track, bool settle, CancellationToken ct)
    {
        try
        {
            if (settle) await Task.Delay(Settle, ct);
            var duration = _progress()?.Duration;
            _queriedDuration = duration;
            var query = new LyricsQuery(track.Title, track.Artist, track.Album, duration, YouTubeId(track));
            var lyrics = await _service!.FindAsync(query, ct);
            ct.ThrowIfCancellationRequested();
            _lookedUp = true;
            Show(lyrics, _progress()?.SongPosition != null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer song took over.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-lyrics-vm] {ex.Message}");
            if (!ct.IsCancellationRequested)
            {
                _lookedUp = true;
                Status = "Couldn't load lyrics";
            }
        }
    }

    private void Show(SongLyrics? lyrics, bool knowsPosition)
    {
        _lyrics = lyrics;
        Versions.Clear();
        if (lyrics == null)
        {
            Lines.Clear();
            ActiveIndex = -1;
            Status = "No lyrics found";
            Credit = null;
            return;
        }

        // Follow along only when the lines are timed and the source says where in this song it
        // is. Live radio doesn't (yet), so its lyrics are a sheet.
        _following = lyrics.Synced && knowsPosition;
        IsFollowing = _following;
        IsLive = _following && _progress()?.Duration == null;

        if (lyrics.Variants is { Count: > 0 } variants)
        {
            Versions.Add(new LyricsVersionViewModel(OriginalLabel));
            foreach (var v in variants) Versions.Add(new LyricsVersionViewModel(v.Label));
        }
        var preferred = _prefs?.LyricsVersion;
        BuildLines(Versions.Any(v => v.Label == preferred) ? preferred! : OriginalLabel);
        Status = null;
        Credit = lyrics.Credit;
        CanAudioSync = _sync != null && lyrics.Synced && _progress()?.LiveAudio != null;
        if (IsAudioSyncEnabled) StartAudioSync(lyrics);
        Tick();
    }

    // Find the radio song's place from its audio, so its synced lyrics can follow along: from the
    // start for the song that was playing when the station was tuned in, and on time for the rest
    // (stations change titles seconds before or after the song does).
    private void StartAudioSync(SongLyrics lyrics)
    {
        if (_syncStarted || _sync == null || !lyrics.Synced || _track is not { } track) return;
        if (_progress()?.LiveAudio is not { } live) return;
        _syncStarted = true;

        var cts = _syncCts = new CancellationTokenSource();
        AudioSyncStatus = "Matching the song's audio…";
        _ = RunAudioSyncAsync(_sync, track, live, cts.Token);
    }

    private async Task RunAudioSyncAsync(LiveSongSync sync, Track track, ILiveSongAudio live, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => sync.RunAsync(track, live,
                found => Dispatcher.UIThread.Post(() => OnAudioSynced(found, ct)), ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-lyrics-vm] audio sync: {ex.Message}");
        }
        // Done without a match (none of the recordings was this song): say nothing more.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ct.IsCancellationRequested && !IsAudioSynced) AudioSyncStatus = null;
        });
    }

    private void OnAudioSynced(SongSync found, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _lyrics is not { } lyrics) return;
        IsAudioSynced = true;
        AudioSyncStatus = "Synced to the song's audio";
        AudioSyncSource = found.Locator;
        // Tuned in mid-song, the lyrics were a sheet: now they can follow.
        if (!_following) Show(lyrics, knowsPosition: true);
        _ = RefitLyricsAsync(found, ct);
    }

    // The station plays the recording that was matched; lyrics timed to a recording of the song's length
    // in it fit best. Keep what's showing when there aren't any.
    private async Task RefitLyricsAsync(SongSync found, CancellationToken ct)
    {
        if (_service == null || _track is not { } track) return;
        try
        {
            var query = new LyricsQuery(track.Title, track.Artist, track.Album, found.SongLength);
            var lyrics = await _service.FindAsync(query, ct);
            if (ct.IsCancellationRequested || lyrics is not { Synced: true } || SameLyrics(lyrics, _lyrics)) return;
            Show(lyrics, knowsPosition: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-lyrics-vm] refit: {ex.Message}");
        }
    }

    private static bool SameLyrics(SongLyrics a, SongLyrics? b) =>
        b != null && a.ProviderId == b.ProviderId && a.Lines.Count == b.Lines.Count
        && a.Lines.Zip(b.Lines).All(p => p.First.Time == p.Second.Time && p.First.Text == p.Second.Text);

    private void StopAudioSync()
    {
        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = null;
        if (!IsAudioSynced) AudioSyncStatus = null;
    }

    [RelayCommand]
    private void SelectVersion(LyricsVersionViewModel? version)
    {
        if (version == null || _lyrics == null) return;
        BuildLines(version.Label);
        if (_prefs != null)
        {
            _prefs.LyricsVersion = version.Label == OriginalLabel ? null : version.Label;
            _prefs.SaveToDisk();
        }
        Tick();
    }

    private void BuildLines(string label)
    {
        foreach (var v in Versions) v.IsSelected = v.Label == label;
        var lines = _lyrics?.Variants?.FirstOrDefault(v => v.Label == label)?.Lines ?? _lyrics?.Lines ?? [];

        Lines.Clear();
        ActiveIndex = -1;
        var duration = _progress()?.Duration;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!_following)
            {
                if (line.Background) continue; // ad-libs read as noise without timing
                Lines.Add(new LyricLineViewModel(line, following: false));
                continue;
            }
            var words = Karaoke.Words(line, LineEnd(lines, i, duration));
            Lines.Add(new LyricLineViewModel(line, following: true, words));
        }
    }

    // When a line stops being sung, if it doesn't say: when the next main line starts.
    private static TimeSpan LineEnd(IReadOnlyList<LyricLine> lines, int index, TimeSpan? duration)
    {
        var line = lines[index];
        for (int j = index + 1; j < lines.Count; j++)
            if (!lines[j].Background && lines[j].Time > line.Time) return lines[j].Time;
        return duration is { } d && d > line.Time ? d : line.Time + TimeSpan.FromSeconds(5);
    }

    private void Tick()
    {
        var progress = _progress();
        CanSeek = _following && progress is { CanSeek: true };

        // The length became known after the lookup (a YouTube video resolving): look again, now
        // that synced lyrics can be checked against it.
        if (_lookedUp && _queriedDuration == null && progress?.Duration != null && _track is { } track && _cts is { } cts)
        {
            _lookedUp = false;
            _ = LoadAsync(track, settle: false, cts.Token);
            return;
        }

        if (!_following || progress == null) return;
        // A live stream that lost its place (it reconnected) has no song position until the next song.
        if (progress.SongPosition is not { } songPosition) return;
        var position = Clock(songPosition);
        // The nudge corrects the station's title timing; a place found from the audio needs none.
        if (ShowNudge) position += TimeSpan.FromSeconds(RadioOffset);

        int active = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Line.Time > position) break;
            if (!Lines[i].IsBackground) active = i;
        }
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var state = i == active ? LyricLineState.Active
                : line.IsBackground ? BackgroundState(line.Line, position)
                : i < active ? LyricLineState.Past
                : LyricLineState.Upcoming;
            bool changed = line.State != state;
            line.State = state;
            // Only the lines being sung change from tick to tick.
            if (changed || state == LyricLineState.Active) line.UpdateFill(position, IsKaraoke);
        }
        ActiveIndex = active;
    }

    // The song position, running smoothly between the source's reports and never stepping back a
    // little when a report lands just behind it (a real jump back is a seek, and is followed).
    private TimeSpan Clock(TimeSpan reported)
    {
        var now = Stopwatch.GetTimestamp();
        if (reported != _reported)
        {
            _reported = reported;
            _reportedAt = now;
        }
        var runOn = Stopwatch.GetElapsedTime(_reportedAt, now);
        var t = _reported + (runOn < MaxRunOn ? runOn : MaxRunOn);
        if (t < _shown && _shown - t < MaxRunOn) return _shown;
        return _shown = t;
    }

    private static LyricLineState BackgroundState(LyricLine line, TimeSpan position) =>
        position < line.Time ? LyricLineState.Upcoming
        : line.End is { } end && position >= end ? LyricLineState.Past
        : LyricLineState.Active;

    [RelayCommand]
    private void SeekTo(LyricLineViewModel? line)
    {
        if (line == null || !CanSeek || _progress() is not { CanSeek: true } progress) return;
        try { _ = progress.SeekAsync(line.Line.Time); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-lyrics-vm] seek: {ex.Message}"); }
    }

    // "youtube:<id>" is how the YouTube source identifies its videos.
    private static string? YouTubeId(Track track) =>
        track.ExternalId is { } id && id.StartsWith("youtube:", StringComparison.Ordinal) ? id["youtube:".Length..] : null;

    public void Dispose()
    {
        _timer.Stop();
        StopAudioSync();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

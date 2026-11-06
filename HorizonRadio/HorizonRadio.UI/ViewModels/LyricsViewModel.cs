using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.UI.ViewModels;

/// <summary>Where a lyric line is relative to the singer.</summary>
public enum LyricLineState { Upcoming, Active, Past }

/// <summary>One line in the lyrics panel.</summary>
public sealed partial class LyricLineViewModel(LyricLine line, bool synced) : ObservableObject
{
    public LyricLine Line { get; } = line;
    public string Text => Line.Text.Length > 0 ? Line.Text : "♪";
    public bool IsBackground => Line.Background;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    private LyricLineState state = synced ? LyricLineState.Upcoming : LyricLineState.Active;

    public double Opacity => State switch
    {
        LyricLineState.Active => 1.0,
        LyricLineState.Past => 0.4,
        _ => 0.6,
    } * (IsBackground ? 0.75 : 1.0);
}

/// <summary>
/// The lyrics panel on the Now Playing page: looks up the playing song's lyrics and, when they're
/// synced and the source reports where in the song it is, follows along line by line. Otherwise
/// (live radio, or unsynced lyrics) the lyrics show as a sheet.
/// </summary>
public sealed partial class LyricsViewModel : ViewModelBase, IDisposable
{
    // Lets the enriched title (usually ~1 s after the raw one) arrive before the first lookup.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    private readonly LyricsService? _service;
    private readonly Func<IPlaybackProgress?> _progress;
    private readonly DispatcherTimer _timer;

    private CancellationTokenSource? _cts;
    private string? _songKey;
    private Track? _track;
    private TimeSpan? _queriedDuration;
    private bool _lookedUp;
    private bool _following;

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    /// <summary>What the panel says instead of lyrics ("Looking for lyrics…"), or null.</summary>
    [ObservableProperty] private string? status;
    [ObservableProperty] private string? credit;
    [ObservableProperty] private bool isVisible;

    /// <summary>The line being sung, or -1. The view keeps it in the middle of the panel.</summary>
    [ObservableProperty] private int activeIndex = -1;

    /// <summary>Whether clicking a line jumps there.</summary>
    [ObservableProperty] private bool canSeek;

    public bool HasLines => Lines.Count > 0;

    public LyricsViewModel(LyricsService? service, Func<IPlaybackProgress?> progress)
    {
        _service = service;
        _progress = progress;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        Lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLines));
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
        _timer.Stop();
        _following = false;
        _lookedUp = false;
        _queriedDuration = null;
        Lines.Clear();
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
            Show(lyrics, duration != null);
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
        Lines.Clear();
        ActiveIndex = -1;
        if (lyrics == null)
        {
            Status = "No lyrics found";
            Credit = null;
            return;
        }

        // Follow along only when the lines are timed and the source says where in this song it
        // is. Live radio doesn't (yet), so its lyrics are a sheet.
        _following = lyrics.Synced && knowsPosition;
        foreach (var line in lyrics.Lines)
        {
            if (!_following && line.Background) continue; // ad-libs read as noise without timing
            Lines.Add(new LyricLineViewModel(line, _following));
        }
        Status = null;
        Credit = lyrics.Credit;
        Tick();
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
        var position = progress.Position;

        int active = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Line.Time > position) break;
            if (!Lines[i].IsBackground) active = i;
        }
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            line.State = i == active ? LyricLineState.Active
                : line.IsBackground ? BackgroundState(line.Line, position)
                : i < active ? LyricLineState.Past
                : LyricLineState.Upcoming;
        }
        ActiveIndex = active;
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
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

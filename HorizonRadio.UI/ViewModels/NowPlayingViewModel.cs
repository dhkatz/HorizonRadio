using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// State of the Now Playing tab. Populated by the IPC client from
/// DLL events (`{"event":"track",...}`) and by the local
/// <see cref="SourceRunner"/> when a C#-side source publishes metadata.
///
/// Also hosts an inline source switcher that drives the same
/// <see cref="SourceRunner"/> the Sources tab uses — picking from this
/// dropdown starts the chosen source with its persisted config. If the
/// source has never been configured (or its config no longer
/// validates), the switch fails silently here and the user can fix it
/// from the Sources tab; <see cref="SwitchStatus"/> carries the message.
/// </summary>
public sealed partial class NowPlayingViewModel : ViewModelBase
{
    [ObservableProperty] private string title = "Ready to Play";
    [ObservableProperty] private string artist = "Choose a source to start playing";
    [ObservableProperty] private string? album;
    [ObservableProperty] private string sourceDisplay = "—";
    [ObservableProperty] private string sourceId = "";
    [ObservableProperty] private Bitmap? albumArt;
    [ObservableProperty] private bool isConnected;

    /// <summary>True once a source has actually emitted a track. The
    /// "Source: …" pill in the Now Playing card binds to this so the
    /// pill stays hidden during the empty-state placeholder, where
    /// SourceDisplay is "—" and the pill would look orphan-y.</summary>
    public bool HasActiveSource => !string.IsNullOrEmpty(SourceId);
    partial void OnSourceIdChanged(string value) => OnPropertyChanged(nameof(HasActiveSource));

    [ObservableProperty] private IAudioSourceFactory? selectedFactory;
    [ObservableProperty] private string? switchStatus;

    /// <summary>Saved mixes for the quick-switch dropdown, kept in sync with the
    /// Mixes tab via the shared store's Changed event.</summary>
    public ObservableCollection<Mix> Mixes { get; } = new();
    [ObservableProperty] private Mix? selectedMix;
    public bool HasMixes => Mixes.Count > 0;

    [ObservableProperty] private bool canPause;
    [ObservableProperty] private bool canSkipNext;
    [ObservableProperty] private bool canSkipPrevious;
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private bool hasTransport;
    [ObservableProperty] private bool canShuffle;
    [ObservableProperty] private bool isShuffleEnabled;

    /// <summary>Progress/seek state for the player bar. <see cref="HasProgress"/>
    /// gates the whole seek row (only shown when the active source reports a
    /// duration); <see cref="CanSeek"/> gates dragging. Seconds-typed props back
    /// the slider; the *Text props are the m:ss labels.</summary>
    [ObservableProperty] private bool hasProgress;
    [ObservableProperty] private bool canSeek;
    [ObservableProperty] private double positionSeconds;
    [ObservableProperty] private double durationSeconds;
    [ObservableProperty] private string positionText = "0:00";
    [ObservableProperty] private string durationText = "0:00";

    /// <summary>Where the active source's audio goes: the in-game bridge
    /// (default) or a local speaker for testing without the game. Modeled as a
    /// single picker so it sits naturally beside the source dropdown — an
    /// output preference, not a per-source setting.</summary>
    public ObservableCollection<OutputTarget> OutputTargets { get; } = new();
    [ObservableProperty] private OutputTarget? selectedOutput;

    /// <summary>Global target-station picker, surfaced in the player bar
    /// alongside the source/output pickers. App-level state (not per-source);
    /// owned here only so the player bar — bound to this VM — can reach it.</summary>
    public StationTargetViewModel Station { get; }

    /// <summary>Master volume slider <em>position</em> (0..1). Governs both
    /// outputs: it's tapered to a perceptual gain and applied to the local
    /// monitor (<see cref="PreviewController"/>) and, as a pre-amp, to the
    /// in-game bridge (via <see cref="MasterVolumeChanged"/>).</summary>
    [ObservableProperty] private double previewVolume = 1.0;

    /// <summary>Raised when the master volume position changes, so the app can
    /// push the (tapered) pre-amp gain to the in-game bridge. The VM stays
    /// IPC-unaware — mirrors the station-target wiring.</summary>
    public event Action<double>? MasterVolumeChanged;

    /// <summary>Mute state for the master volume. Toggled by clicking the volume
    /// icon; remembers the pre-mute level to restore on unmute.</summary>
    [ObservableProperty] private bool isMuted;
    private double _volumeBeforeMute = 1.0;

    private readonly SourceRunner? _runner;
    private readonly SourceConfigStore? _store;
    private readonly MixStore? _mixes;
    private readonly MixSwitcher? _switcher;
    private readonly QueuePlayback? _queue;
    private readonly PreviewController? _preview;
    private readonly ToastManager? _toasts;
    private readonly DialogManager? _dialogs;
    private bool _suppressSwitch;
    private bool _suppressMix;
    // Guards IsShuffleEnabled while RebindTransport seeds it from the persisted
    // preference, so syncing the toggle doesn't re-fire a write/apply.
    private bool _suppressShuffle;
    private ITransportControls? _transport;

    // Progress polling: the active source's IPlaybackProgress (if any) is read
    // on a timer rather than evented. _isSeeking suppresses poll writes while
    // the user drags the seek bar so the thumb doesn't fight them.
    private IPlaybackProgress? _progress;
    private DispatcherTimer? _progressTimer;
    private bool _isSeeking;

    // Output-availability tracking, edge-triggered: a source playing into an
    // unreachable output (game closed / dead device) pauses + toasts once. We
    // deliberately do NOT auto-resume when it's reachable again — the user
    // presses play, so a burst of audio can't catch them off guard.
    private bool _outputAvailable = true;
    private bool _needsOutputPause;

    public IReadOnlyList<IAudioSourceFactory> AvailableSources { get; }

    public NowPlayingViewModel(SourceRunner runner, SourceConfigStore store,
        MixStore mixes, MixSwitcher switcher, QueuePlayback queue,
        StationTargetViewModel station,
        PreviewController? preview = null, ToastManager? toasts = null,
        DialogManager? dialogs = null)
    {
        _runner = runner;
        _store = store;
        _mixes = mixes;
        _switcher = switcher;
        _queue = queue;
        _preview = preview;
        _toasts = toasts;
        _dialogs = dialogs;
        Station = station;
        // Self-driven sources (Spotify Connect, the test tone) start directly;
        // content sources open a quick-play dialog when picked (see
        // OnSelectedFactoryChanged) — both live in this one picker.
        AvailableSources = SourceCatalog.All;

        _mixes.Changed += () => Dispatcher.UIThread.Post(RefreshMixes);
        RefreshMixes();

        // Drives the seek/progress bar; started only while a progress-capable
        // source is active (see RebindTransport).
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) => PollProgress();

        // Build the output picker: the in-game bridge first, then every local
        // render device. Assign the backing fields directly so seeding the saved
        // choice doesn't fire the OnChanged handlers back into the controller.
        if (_preview != null)
        {
            OutputTargets.Add(OutputTarget.Bridge);
            foreach (var d in PreviewController.Devices)
                OutputTargets.Add(new OutputTarget(false, d.Id, d.Name));

            // When enabled, reflect the controller's actual destination. If the
            // saved device is gone, the controller falls back to the default
            // speaker — so select the first local target (default), not the
            // bridge, otherwise the picker would lie about where audio is going.
            selectedOutput = _preview.Enabled
                ? OutputTargets.FirstOrDefault(t => !t.IsBridge && t.DeviceId == _preview.DeviceId)
                  ?? OutputTargets.FirstOrDefault(t => !t.IsBridge)
                  ?? OutputTargets[0]
                : OutputTargets[0];
            previewVolume = _preview.Volume;
        }

        // Keep the dropdown in sync when the runner is driven from the
        // Sources tab (or anywhere else). Suppress the resulting set so
        // we don't re-trigger a switch in a loop. Also re-bind transport
        // capability against the newly-active source.
        _runner.ActiveSourceChanged += factory =>
            Dispatcher.UIThread.Post(() =>
            {
                // Content sources aren't a persistent picker selection — they're
                // launched via the quick-play dialog — so don't pin the picker to
                // them, or re-picking the same one is a no-op and won't reopen the
                // dialog. Self-driven sources still mirror into the picker.
                _suppressSwitch = true;
                SelectedFactory = factory is IContentSourceFactory ? null : factory;
                _suppressSwitch = false;
                RebindTransport();

                // Key output handling off whether a source is actually playing,
                // not off a null factory — a mix plays factory-less but is live, so
                // it must still get output-reachability evaluation/pause.
                if (_runner?.ActiveSource is null)
                {
                    // Nothing playing: clear the output-pause state so the next
                    // start re-evaluates reachability from a clean slate.
                    _outputAvailable = true;
                    _needsOutputPause = false;
                }
                else
                {
                    ReevaluateOutput();
                }
            });
    }

    private void RebindTransport()
    {
        if (_transport != null) _transport.PausedChanged -= OnPausedChanged;
        _transport = _runner?.ActiveSource as ITransportControls;
        HasTransport = _transport != null;
        IsPaused = _transport?.IsPaused ?? false;
        RefreshCapabilities();

        // Reflect the persisted preference (the runner already applied it to the
        // source as it started). Suppress so this sync doesn't re-persist/apply.
        _suppressShuffle = true;
        IsShuffleEnabled = _store?.Shuffle ?? false;
        _suppressShuffle = false;

        if (_transport != null) _transport.PausedChanged += OnPausedChanged;

        // Rebind the progress capability (independent of transport — Spotify has
        // progress but no transport). Run the poll timer only while present.
        _progress = _runner?.ActiveSource as IPlaybackProgress;
        CanSeek = _progress?.CanSeek ?? false;
        if (_progress != null)
        {
            _progressTimer?.Start();
            PollProgress();
        }
        else
        {
            _progressTimer?.Stop();
            HasProgress = false;
            PositionSeconds = DurationSeconds = 0;
        }
    }

    /// <summary>Poll the active source's position/duration onto the bar. Skips
    /// the position write while the user is dragging the seek bar.</summary>
    private void PollProgress()
    {
        if (_progress == null) { HasProgress = false; return; }

        var dur = _progress.Duration;
        HasProgress = dur is { TotalSeconds: > 0 };
        if (!HasProgress) return;

        DurationSeconds = dur!.Value.TotalSeconds;
        if (!_isSeeking)
            PositionSeconds = Math.Min(_progress.Position.TotalSeconds, DurationSeconds);
    }

    partial void OnPositionSecondsChanged(double value) =>
        PositionText = FormatTime(value);

    partial void OnDurationSecondsChanged(double value)
    {
        DurationText = FormatTime(value);
        OnPropertyChanged(nameof(SeekMaximum));
    }

    partial void OnHasProgressChanged(bool value) =>
        OnPropertyChanged(nameof(SeekOpacity));

    /// <summary>Slider maximum: the real duration, or 1 when unknown so the
    /// always-present (but inactive) seek bar renders cleanly with Min &lt; Max
    /// and doesn't pop in/out and shift the bar's layout.</summary>
    public double SeekMaximum => DurationSeconds > 0 ? DurationSeconds : 1;

    /// <summary>Dim the seek bar when there's no real progress to show.</summary>
    public double SeekOpacity => HasProgress ? 1.0 : 0.35;

    private static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>Called from the seek slider's drag/press handlers: suppress poll
    /// writes during a drag, then commit the seek on release.</summary>
    public void BeginSeek()
    {
        if (CanSeek) _isSeeking = true;
    }

    public void EndSeek()
    {
        if (!_isSeeking) return;
        _isSeeking = false;
        if (_progress is { CanSeek: true })
        {
            var target = TimeSpan.FromSeconds(PositionSeconds);
            try { _ = _progress.SeekAsync(target); }
            catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] seek: {ex.Message}"); }
        }
    }

    /// <summary>Re-read the source's CanX capabilities. Called on (re)bind and
    /// again on each track update, because some sources only learn their track
    /// count after start — YouTube resolves its playlist asynchronously, so
    /// CanShuffle / CanSkipNext aren't known until the first track arrives.</summary>
    private void RefreshCapabilities()
    {
        CanPause = _transport?.CanPause ?? false;
        CanSkipNext = _transport?.CanSkipNext ?? false;
        CanSkipPrevious = _transport?.CanSkipPrevious ?? false;
        CanShuffle = _transport?.CanShuffle ?? false;
    }

    partial void OnIsShuffleEnabledChanged(bool value)
    {
        if (_suppressShuffle) return;

        // Persist as a global preference and keep the runner in sync so the
        // next source start honors it...
        if (_runner != null) _runner.Shuffle = value;
        if (_store != null)
        {
            _store.Shuffle = value;
            _store.SaveToDisk();
        }

        // ...and apply live to the running source (keeps current track, shuffles
        // the rest).
        if (_transport != null)
        {
            try { _ = _transport.SetShuffleAsync(value); }
            catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] shuffle: {ex.Message}"); }
        }
    }

    partial void OnSelectedOutputChanged(OutputTarget? value)
    {
        if (_preview == null || value == null) return;
        if (value.IsBridge)
        {
            _preview.SetEnabled(false);
        }
        else
        {
            _preview.SetDevice(value.DeviceId);
            _preview.SetEnabled(true);
        }
        // The destination changed under a (possibly running) source — re-check
        // reachability so we pause/resume + toast as appropriate.
        ReevaluateOutput();
    }

    partial void OnPreviewVolumeChanged(double value)
    {
        _preview?.SetVolume(value);
        // Also drive the in-game bridge pre-amp (App pushes it over IPC).
        MasterVolumeChanged?.Invoke(value);
        // Dragging the slider up unmutes; keeps the icon honest.
        if (value > 0 && IsMuted) IsMuted = false;
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted)
        {
            IsMuted = false;
            PreviewVolume = _volumeBeforeMute;
        }
        else
        {
            _volumeBeforeMute = PreviewVolume > 0 ? PreviewVolume : 1.0;
            IsMuted = true;
            PreviewVolume = 0;
        }
    }

    /// <summary>Output-availability guard. When the active output can't be
    /// played to, pause the source (keeping its metadata + position) and toast.
    /// We intentionally never auto-resume — the user presses play once they've
    /// picked a working output, so audio can't suddenly blare. Safe to call
    /// repeatedly: the toast fires only on the reachable→unreachable edge (so
    /// reconnect ticks don't spam it), and the pause fires at most once per
    /// episode (a source's CanPause may only flip true after its first track
    /// loads, so we keep the intent pending until it can act on it).</summary>
    private void ReevaluateOutput()
    {
        if (_runner?.IsRunning != true) return;

        var nowAvailable = IsCurrentOutputAvailable();

        if (nowAvailable != _outputAvailable)
        {
            _outputAvailable = nowAvailable;
            if (!nowAvailable)
            {
                _needsOutputPause = true;
                ShowOutputUnavailableToast();
            }
            else
            {
                // Reachable again — stop trying to auto-pause, but don't resume.
                _needsOutputPause = false;
            }
        }

        // Carry out a pending auto-pause once the source can actually pause.
        // Clearing the flag after means we pause at most once, so a manual
        // resume while still unreachable isn't overridden.
        if (_needsOutputPause && _transport is { CanPause: true, IsPaused: false })
        {
            _ = SafeTogglePauseAsync();
            _needsOutputPause = false;
        }
    }

    private bool IsCurrentOutputAvailable()
    {
        var target = SelectedOutput;
        if (target == null) return true;          // no picker (designer) — assume reachable
        return target.IsBridge
            ? IsConnected                          // bridge reachable iff the game/DLL is connected
            : _preview?.IsSpeakerActive == true;   // local device opened successfully
    }

    private void ShowOutputUnavailableToast()
    {
        if (_toasts == null) return;
        if (SelectedOutput is { IsBridge: false } device)
        {
            _toasts.CreateToast("Output unavailable")
                .WithContent($"Couldn't play to “{device.Name}”. Playback paused — pick another output device.")
                .WithDelay(6)
                .DismissOnClick()
                .ShowError();
        }
        else
        {
            _toasts.CreateToast($"{OutputTarget.Bridge.Name} isn't running")
                .WithContent("Playback paused. Launch the game with the mod installed, or choose a local output device to test.")
                .WithDelay(6)
                .DismissOnClick()
                .ShowError();
        }
    }

    private async Task SafeTogglePauseAsync()
    {
        if (_transport == null) return;
        try { await _transport.TogglePauseAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] output-pause: {ex.Message}"); }
    }

    private void OnPausedChanged(bool paused) =>
        Dispatcher.UIThread.Post(() => IsPaused = paused);

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (_transport == null) return;
        // A manual play/pause means the user is in control — cancel any pending
        // output auto-pause so we don't override their choice.
        _needsOutputPause = false;
        try { await _transport.TogglePauseAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] pause: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (_transport == null) return;
        try { await _transport.NextAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] next: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (_transport == null) return;
        try { await _transport.PreviousAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-now-vm] prev: {ex.Message}"); }
    }

    /// <summary>Designer ctor — no runner, dropdown is inert.</summary>
    public NowPlayingViewModel()
    {
        AvailableSources = SourceCatalog.All;
        Station = new StationTargetViewModel();
    }

    partial void OnSelectedFactoryChanged(IAudioSourceFactory? value)
    {
        if (_suppressSwitch || _runner == null || _store == null || value == null) return;

        // Content sources need a locator: don't leave the picker showing it as
        // selected (it isn't playing yet) — revert to the active source — and pop
        // a quick-play dialog for it. Self-driven sources start directly.
        if (value is IContentSourceFactory)
        {
            var picked = value;
            _suppressSwitch = true;
            SelectedFactory = _runner.ActiveFactory;
            _suppressSwitch = false;
            OpenQuickPlay(picked);
            return;
        }

        // Fire-and-forget: setter must stay synchronous, and the runner
        // handles its own serialization. Errors are captured into
        // SwitchStatus so the UI can show them.
        _ = SwitchAsync(value);
    }

    private void OpenQuickPlay(IAudioSourceFactory source)
    {
        if (_dialogs == null) return;
        var dialog = new QuickPlayDialogViewModel(_dialogs, source);
        _dialogs.CreateDialog(dialog)
            .Dismissible()
            .WithSuccessCallback(vm => _ = QuickPlayAsync(source, vm.Locator))
            .Show();
    }

    private async Task QuickPlayAsync(IAudioSourceFactory source, string locator)
    {
        if (_queue == null || source is not IContentSourceFactory) return;
        if (string.IsNullOrWhiteSpace(locator)) return;

        // Quick play now appends a one-off to the global queue (playing it before
        // the active mix context) rather than replacing the active source.
        SwitchStatus = $"Adding {source.DisplayName} to the queue...";
        try
        {
            await _queue.EnqueueLocatorAsync(source, locator);
            SwitchStatus = null;
        }
        catch (MissingToolException ex)
        {
            Debug.WriteLine($"[hzn-now-vm] quick play blocked: {ex.Message}");
            SwitchStatus = ex.Message;
            _toasts?.CreateToast("Tool required")
                .WithContent(ex.Message)
                .WithDelay(8)
                .DismissOnClick()
                .ShowWarning();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-now-vm] quick play failed: {ex.Message}");
            SwitchStatus = ex.Message;
            _toasts?.CreateToast($"Couldn't start {source.DisplayName}")
                .WithContent(ex.Message)
                .WithDelay(6)
                .DismissOnClick()
                .ShowError();
        }
    }

    private async Task SwitchAsync(IAudioSourceFactory factory)
    {
        if (_runner == null || _store == null) return;
        SwitchStatus = $"Starting {factory.DisplayName}...";
        try
        {
            var values = _store.Load(factory.Id, factory.Schema);
            await _runner.StartAsync(factory, values);
            SwitchStatus = null;
        }
        catch (MissingToolException ex)
        {
            // Not a failure so much as a setup prompt — surface it as a
            // warning that points the user at the Tools tab.
            Debug.WriteLine($"[hzn-now-vm] switch blocked: {ex.Message}");
            SwitchStatus = ex.Message;
            _toasts?.CreateToast("Tool required")
                .WithContent(ex.Message)
                .WithDelay(8)
                .DismissOnClick()
                .ShowWarning();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-now-vm] switch failed: {ex.Message}");
            SwitchStatus = $"{factory.DisplayName}: {ex.Message}";
            // The dashboard no longer shows SwitchStatus inline (controls moved
            // to the player bar), so surface the failure as a toast.
            _toasts?.CreateToast($"Couldn't start {factory.DisplayName}")
                .WithContent(ex.Message)
                .WithDelay(6)
                .DismissOnClick()
                .ShowError();
        }
    }

    // In-place sync (add/remove by id) rather than Clear()+Add, so editing one
    // mix elsewhere doesn't tear down and rebuild the whole bound dropdown.
    private void RefreshMixes()
    {
        if (_mixes == null) return;
        var desired = _mixes.All;

        for (int i = Mixes.Count - 1; i >= 0; i--)
            if (!desired.Any(m => m.Id == Mixes[i].Id)) Mixes.RemoveAt(i);

        foreach (var m in desired)
        {
            var idx = -1;
            for (int i = 0; i < Mixes.Count; i++)
                if (Mixes[i].Id == m.Id) { idx = i; break; }
            if (idx < 0) Mixes.Add(m);
            else if (!Equals(Mixes[idx], m)) Mixes[idx] = m; // name/entries changed
        }
        OnPropertyChanged(nameof(HasMixes));
    }

    // The dropdown is a "jump to mix" launcher, not a mirror of what's playing:
    // on pick we reset the selection to null (so re-picking the same mix
    // re-fires) and launch the captured choice. This avoids the dropdown going
    // stale when the source is switched elsewhere.
    partial void OnSelectedMixChanged(Mix? value)
    {
        if (_suppressMix || _switcher == null || value == null) return;

        var mix = value;
        _suppressMix = true;
        SelectedMix = null;
        _suppressMix = false;
        PromptAndStartMix(mix);
    }

    // Starting a mix while the queue already has content asks whether to replace the
    // context or add this mix's tracks to the queue; otherwise it just starts it.
    private void PromptAndStartMix(Mix mix)
    {
        if (_switcher == null) return;

        if (_switcher.QueueHasContent && _dialogs != null)
        {
            var dialog = new QueueAddModeDialogViewModel(_dialogs, mix.Name);
            _dialogs.CreateDialog(dialog)
                .Dismissible()
                .WithSuccessCallback(vm => _ = SwitchMixAsync(mix, vm.Mode))
                .Show();
        }
        else
        {
            _ = SwitchMixAsync(mix, QueueAddMode.Replace);
        }
    }

    private async Task SwitchMixAsync(Mix mix, QueueAddMode mode)
    {
        if (_switcher == null) return;

        SwitchStatus = mode == QueueAddMode.Add
            ? $"Adding {mix.Name} to the queue..."
            : $"Starting {mix.Name}...";
        try
        {
            if (mode == QueueAddMode.Add) await _switcher.AddToQueueAsync(mix.Id);
            else await _switcher.SwitchToAsync(mix.Id);
            SwitchStatus = null;
        }
        catch (MissingToolException ex)
        {
            Debug.WriteLine($"[hzn-now-vm] mix switch blocked: {ex.Message}");
            SwitchStatus = ex.Message;
            _toasts?.CreateToast("Tool required")
                .WithContent(ex.Message)
                .WithDelay(8)
                .DismissOnClick()
                .ShowWarning();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-now-vm] mix switch failed: {ex.Message}");
            SwitchStatus = ex.Message; // already includes the mix name
            _toasts?.CreateToast($"Couldn't start “{mix.Name}”")
                .WithContent(ex.Message)
                .WithDelay(6)
                .DismissOnClick()
                .ShowError();
        }
    }

    /// <summary>
    /// Apply a new Track from an IPC event. Stays on the UI thread —
    /// caller dispatches via Avalonia's Dispatcher.UIThread.
    /// </summary>
    public void Apply(Track track)
    {
        Title = string.IsNullOrWhiteSpace(track.Title) ? "Unknown track" : track.Title;
        Artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist;
        Album = track.Album;
        SourceId = track.SourceId;
        SourceDisplay = track.SourceDisplay;
        AlbumArt = DecodeArt(track.AlbumArt);

        // Capabilities may have only just become known (e.g. YouTube finished
        // resolving its playlist), so re-evaluate them as tracks come in.
        RefreshCapabilities();

        // Now that CanPause may have flipped true, enforce the output-pause if
        // the destination is unreachable (the start-time check can run before a
        // source knows it can pause).
        ReevaluateOutput();
    }

    public void SetConnectionState(bool connected)
    {
        IsConnected = connected;

        // Only fall back to the placeholder when nothing is actually playing. A
        // local source playing to a speaker keeps its metadata even while the
        // game (and its IPC pipe) is disconnected — otherwise the periodic
        // reconnect attempts would wipe Now Playing every few seconds.
        if (!connected && _runner?.IsRunning != true)
        {
            Title = "Ready to Play";
            Artist = "Choose a source to start playing";
            Album = null;
            SourceDisplay = "—";
            SourceId = "";
            AlbumArt = null;
        }

        // The bridge's reachability tracks the game connection, so a source
        // playing to the bridge pauses when the game quits and resumes when it
        // returns.
        ReevaluateOutput();
    }

    private static Bitmap? DecodeArt(byte[]? bytes) => Imaging.ImageBytes.ToBitmap(bytes);
}

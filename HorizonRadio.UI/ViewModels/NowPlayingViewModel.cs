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
using HorizonRadio.Core.Sources.Profiles;
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
    [ObservableProperty] private string title = "Nothing playing";
    [ObservableProperty] private string artist = "Launch Forza Horizon 6 with the mod installed";
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

    /// <summary>Saved profiles for the quick-switch dropdown, kept in sync with
    /// the Profiles tab via the shared store's Changed event.</summary>
    public ObservableCollection<SourceProfile> Profiles { get; } = new();
    [ObservableProperty] private SourceProfile? selectedProfile;
    public bool HasProfiles => Profiles.Count > 0;

    [ObservableProperty] private bool canPause;
    [ObservableProperty] private bool canSkipNext;
    [ObservableProperty] private bool canSkipPrevious;
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private bool hasTransport;
    [ObservableProperty] private bool canShuffle;
    [ObservableProperty] private bool isShuffleEnabled;

    /// <summary>Where the active source's audio goes: the in-game bridge
    /// (default) or a local speaker for testing without the game. Modeled as a
    /// single picker so it sits naturally beside the source dropdown — an
    /// output preference, not a per-source setting.</summary>
    public ObservableCollection<OutputTarget> OutputTargets { get; } = new();
    [ObservableProperty] private OutputTarget? selectedOutput;

    /// <summary>Local monitor volume (0..1). Only applies when a local output
    /// device is selected; the in-game bridge ignores it.</summary>
    [ObservableProperty] private double previewVolume = 1.0;

    /// <summary>True when a local device (not the in-game bridge) is the chosen
    /// output — gates the volume slider's enabled state.</summary>
    public bool IsLocalOutput => SelectedOutput is { IsBridge: false };

    private readonly SourceRunner? _runner;
    private readonly SourceConfigStore? _store;
    private readonly SourceProfileStore? _profiles;
    private readonly ProfileSwitcher? _switcher;
    private readonly PreviewController? _preview;
    private readonly ToastManager? _toasts;
    private bool _suppressSwitch;
    private bool _suppressProfile;
    // Guards IsShuffleEnabled while RebindTransport seeds it from the persisted
    // preference, so syncing the toggle doesn't re-fire a write/apply.
    private bool _suppressShuffle;
    private ITransportControls? _transport;

    // Output-availability tracking, edge-triggered: a source playing into an
    // unreachable output (game closed / dead device) pauses + toasts once. We
    // deliberately do NOT auto-resume when it's reachable again — the user
    // presses play, so a burst of audio can't catch them off guard.
    private bool _outputAvailable = true;
    private bool _needsOutputPause;

    public IReadOnlyList<IAudioSourceFactory> AvailableSources { get; }

    public NowPlayingViewModel(SourceRunner runner, SourceConfigStore store,
        SourceProfileStore profiles, ProfileSwitcher switcher,
        PreviewController? preview = null, ToastManager? toasts = null)
    {
        _runner = runner;
        _store = store;
        _profiles = profiles;
        _switcher = switcher;
        _preview = preview;
        _toasts = toasts;
        AvailableSources = SourceCatalog.All;

        _profiles.Changed += () => Dispatcher.UIThread.Post(RefreshProfiles);
        RefreshProfiles();

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
                _suppressSwitch = true;
                SelectedFactory = factory;
                _suppressSwitch = false;
                RebindTransport();

                if (factory == null)
                {
                    // Source stopped: clear the output-pause state so the next
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
        OnPropertyChanged(nameof(IsLocalOutput));
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

    partial void OnPreviewVolumeChanged(double value) => _preview?.SetVolume(value);

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
    }

    partial void OnSelectedFactoryChanged(IAudioSourceFactory? value)
    {
        if (_suppressSwitch || _runner == null || _store == null || value == null) return;

        // Fire-and-forget: setter must stay synchronous, and the runner
        // handles its own serialization. Errors are captured into
        // SwitchStatus so the UI can show them.
        _ = SwitchAsync(value);
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-now-vm] switch failed: {ex.Message}");
            SwitchStatus = $"{factory.DisplayName}: {ex.Message}";
        }
    }

    // In-place sync (add/remove by id) rather than Clear()+Add, so editing one
    // profile elsewhere doesn't tear down and rebuild the whole bound dropdown.
    private void RefreshProfiles()
    {
        if (_profiles == null) return;
        var desired = _profiles.All;

        for (int i = Profiles.Count - 1; i >= 0; i--)
            if (!desired.Any(p => p.Id == Profiles[i].Id)) Profiles.RemoveAt(i);

        foreach (var p in desired)
        {
            var idx = -1;
            for (int i = 0; i < Profiles.Count; i++)
                if (Profiles[i].Id == p.Id) { idx = i; break; }
            if (idx < 0) Profiles.Add(p);
            else if (!Equals(Profiles[idx], p)) Profiles[idx] = p; // name/content changed
        }
        OnPropertyChanged(nameof(HasProfiles));
    }

    // The dropdown is a "jump to profile" launcher, not a mirror of what's
    // playing: on pick we reset the selection to null (so re-picking the same
    // profile re-fires) and launch the captured choice. This avoids the
    // dropdown going stale when the source is switched elsewhere.
    partial void OnSelectedProfileChanged(SourceProfile? value)
    {
        if (_suppressProfile || _switcher == null || value == null) return;

        var profile = value;
        _suppressProfile = true;
        SelectedProfile = null;
        _suppressProfile = false;
        _ = SwitchProfileAsync(profile);
    }

    private async Task SwitchProfileAsync(SourceProfile profile)
    {
        if (_switcher == null) return;

        SwitchStatus = $"Starting {profile.Name}...";
        try
        {
            await _switcher.SwitchToAsync(profile.Id);
            SwitchStatus = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-now-vm] profile switch failed: {ex.Message}");
            SwitchStatus = ex.Message; // already includes the profile name
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
            Title = "Nothing playing";
            Artist = "Launch Forza Horizon 6 with the mod installed";
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

    private static Bitmap? DecodeArt(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            // Album art payload may be malformed if the ID3 frame had
            // a non-image MIME type or a partial download. Better a
            // blank tile than a crashed UI.
            return null;
        }
    }
}

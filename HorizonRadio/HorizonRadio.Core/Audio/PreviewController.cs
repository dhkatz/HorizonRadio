using System;
using System.Collections.Generic;
using System.Threading;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Audio;

/// <summary>Where local monitoring stands (see <see cref="PreviewController"/>).</summary>
public enum SpeakerState
{
    /// <summary>Local monitoring is off; audio goes to the game.</summary>
    Off,
    /// <summary>A speaker is open and is the only destination.</summary>
    On,
    /// <summary>Local monitoring is on but no speaker is playing (it failed to open,
    /// or was lost). Audio goes nowhere until a retry or the device returns.</summary>
    Faulted,
    /// <summary>Shut down; nothing changes any more.</summary>
    Disposed,
}

/// <summary>
/// Owns the local "test playback" lifecycle: an <see cref="ISpeakerOutput"/>
/// attached to the pipeline's <see cref="TeePcmSink"/>, plus the persisted
/// enable/device/volume preferences. The UI drives this; the audio sources
/// are unaware it exists.
///
/// A small state machine (<see cref="SpeakerState"/>), one transition at a time
/// under a lock: user calls act inline on the caller's thread, while device
/// callbacks (the speaker's <see cref="ISpeakerOutput.Lost"/>, OS endpoint
/// notifications) are handed to the thread pool, never acted on where they're
/// raised. It knows nothing of a window: <see cref="StateChanged"/> is raised on
/// whichever thread made the change, and a UI marshals it.
/// Each state has one tee route: Off → game, On → speaker, Faulted → nowhere
/// (so audio doesn't leak into the game while the picker says speakers; the
/// UI pauses and toasts instead).
/// </summary>
public sealed class PreviewController : IDisposable
{
    private enum Trigger
    {
        /// <summary>The user changed (or re-confirmed) Enabled/DeviceId: reconcile, retrying if faulted.</summary>
        User,
        /// <summary>The open speaker stopped on its own.</summary>
        Lost,
        /// <summary>The system default render device changed.</summary>
        DefaultChanged,
        /// <summary>A render device became available.</summary>
        DeviceAvailable,
        /// <summary>A render device became unavailable.</summary>
        DeviceUnavailable,
    }

    private readonly TeePcmSink _tee;
    private readonly SettingsStore _settings;
    private readonly ISpeakerOutput _speaker;
    private readonly IAudioDeviceWatcher? _watcher;
    private readonly Action<Action> _post;
    private readonly Lock _lock = new();

    // The DeviceId the open speaker was asked for (it may have fallen back to the
    // default if that device was gone).
    private string? _openedFor;

    // Settings save themselves a moment after the last change (SettingsStore), so a slider drag
    // that fires SetVolume dozens of times writes the file once.
    private bool _disposed;

    public PreviewController(TeePcmSink tee, SettingsStore settings)
        : this(tee, settings, a => ThreadPool.QueueUserWorkItem(_ => a()), new SpeakerPcmSink(),
            WasapiDeviceWatcher.TryCreate())
    {
    }

    /// <param name="post">Runs an action later, off the thread that raised a device
    /// callback (tests run it when they choose).</param>
    internal PreviewController(TeePcmSink tee, SettingsStore settings, Action<Action> post,
        ISpeakerOutput speaker, IAudioDeviceWatcher? watcher)
    {
        _tee = tee;
        _settings = settings;
        _post = post;
        _speaker = speaker;
        _watcher = watcher;
        Enabled = settings.Get(PreviewSettings.Enabled);
        DeviceId = settings.Get(PreviewSettings.Device);
        Volume = settings.Get(PreviewSettings.Volume);
        _speaker.Volume = VolumeTaper.ToGain(Volume);

        _speaker.Lost += OnSpeakerLost;
        if (_watcher != null)
        {
            _watcher.DefaultDeviceChanged += OnDefaultDeviceChanged;
            _watcher.DeviceAvailabilityChanged += OnDeviceAvailabilityChanged;
        }

        Fire(Trigger.User);
    }

    /// <summary>Where local monitoring stands. Changes raise <see cref="StateChanged"/>.</summary>
    public SpeakerState State { get; private set; } = SpeakerState.Off;

    /// <summary>Raised when <see cref="State"/> changes, on the thread that changed it.</summary>
    public event Action? StateChanged;

    /// <summary>Whether the user wants local monitoring (persisted). True in both
    /// <see cref="SpeakerState.On"/> and <see cref="SpeakerState.Faulted"/>.</summary>
    public bool Enabled { get; private set; }

    /// <summary>The chosen render device (persisted); null = follow the system default.</summary>
    public string? DeviceId { get; private set; }

    /// <summary>The master volume <em>slider position</em> (0..1), persisted as
    /// <c>previewVolume</c>. Converted to a linear gain via <see cref="VolumeTaper"/>
    /// before it reaches the speaker. This is the same position the in-game bridge
    /// uses as a pre-amp, so the one slider governs both outputs.</summary>
    public double Volume { get; private set; }

    /// <summary>True when local monitoring is on and a speaker is actually playing.
    /// False if the device failed to open or was lost (e.g. unplugged), so callers
    /// can tell the selected output isn't reachable.</summary>
    public bool IsSpeakerActive => State == SpeakerState.On;

    /// <summary>Active render endpoints for the UI's device picker (first entry
    /// is "system default", with a null id).</summary>
    public static IReadOnlyList<AudioDevice> Devices => SpeakerPcmSink.EnumerateRenderDevices();

    /// <summary>Turn local monitoring on or off. Turning it on while faulted retries.</summary>
    public void SetEnabled(bool on)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (Enabled != on)
            {
                Enabled = on;
                _settings.Set(PreviewSettings.Enabled, on);
            }
            Fire(Trigger.User);
        }
    }

    /// <summary>Make <paramref name="deviceId"/> (null = system default) the output:
    /// local monitoring on, on that device. Picking the device already chosen
    /// retries it if it's faulted.</summary>
    public void SelectDevice(string? deviceId)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (!Enabled || DeviceId != deviceId)
            {
                Enabled = true;
                DeviceId = deviceId;
                _settings.Set(PreviewSettings.Enabled, true);
                _settings.Set(PreviewSettings.Device, deviceId);
            }
            Fire(Trigger.User);
        }
    }

    public void SetVolume(double volume)
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volume = volume;
            // Volume is the raw slider *position*; the speaker takes a linear gain.
            // Run it through the perceptual taper so the fader eases down smoothly.
            _speaker.Volume = VolumeTaper.ToGain(volume);
            _settings.Set(PreviewSettings.Volume, volume);
        }
    }

    // Device callbacks: any thread. Hand off; never act here (the speaker may be
    // calling from its render thread, the watcher from a COM worker that mustn't block).
    private void OnSpeakerLost() => Post(Trigger.Lost);

    private void OnDefaultDeviceChanged(string? id) => Post(Trigger.DefaultChanged, id);

    private void OnDeviceAvailabilityChanged(string id, bool available) =>
        Post(available ? Trigger.DeviceAvailable : Trigger.DeviceUnavailable, id);

    private void Post(Trigger trigger, string? deviceId = null) => _post(() =>
    {
        lock (_lock) Fire(trigger, deviceId);
    });

    /// <summary>The state machine: every transition happens here, under the lock.</summary>
    private void Fire(Trigger trigger, string? deviceId = null)
    {
        switch (State, trigger)
        {
            case (SpeakerState.Disposed, _):
                return;

            // The user changed or re-confirmed the preferences.
            case (_, Trigger.User) when !Enabled:
                _speaker.Close();
                Enter(SpeakerState.Off);
                return;
            case (SpeakerState.On, Trigger.User) when DeviceId == _openedFor && _speaker.IsOpen:
                return; // already playing where asked
            case (_, Trigger.User):
                Open(); // first open, a different device, or a retry after a fault
                return;

            // The open speaker died on its own. Ignore a stale report from a
            // device we've since replaced (the speaker reopened, so it's open).
            case (SpeakerState.On, Trigger.Lost) when !_speaker.IsOpen:
            case (SpeakerState.On, Trigger.DeviceUnavailable) when deviceId == _speaker.OpenedDeviceId:
                _speaker.Close(); // release the dead device
                Enter(SpeakerState.Faulted);
                return;

            // Following the system default (chosen, or fallen back to): move with it.
            case (SpeakerState.On, Trigger.DefaultChanged)
                when FollowsDefault && deviceId != null && deviceId != _speaker.OpenedDeviceId:
            case (SpeakerState.Faulted, Trigger.DefaultChanged) when DeviceId == null && deviceId != null:
                Open();
                return;

            // A device came back: the chosen one (retry, or leave the fallback), or
            // any device while faulted on the system default.
            case (SpeakerState.On, Trigger.DeviceAvailable)
                when DeviceId != null && deviceId == DeviceId && _speaker.OpenedDeviceId != DeviceId:
            case (SpeakerState.Faulted, Trigger.DeviceAvailable) when DeviceId == null || deviceId == DeviceId:
                Open();
                return;

            default:
                return; // nothing to do in this state
        }
    }

    // True when the open speaker is on the system default: chosen as such, or the
    // chosen device was gone and it fell back.
    private bool FollowsDefault => _openedFor == null || _speaker.OpenedDeviceId != _openedFor;

    private void Open()
    {
        _openedFor = DeviceId;
        _speaker.Volume = VolumeTaper.ToGain(Volume);
        Enter(_speaker.Open(DeviceId) ? SpeakerState.On : SpeakerState.Faulted);
    }

    private void Enter(SpeakerState state)
    {
        _tee.Route = state switch
        {
            SpeakerState.On => TeeRoute.PreviewOnly(_speaker),
            SpeakerState.Faulted => TeeRoute.Nowhere,
            _ => TeeRoute.PrimaryOnly,
        };
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock) DisposeLocked();
    }

    private void DisposeLocked()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop listening before releasing the device, so no callback posts a
        // transition after this (and any already posted finds us Disposed).
        if (_watcher != null)
        {
            _watcher.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            _watcher.DeviceAvailabilityChanged -= OnDeviceAvailabilityChanged;
            _watcher.Dispose();
        }
        _speaker.Lost -= OnSpeakerLost;
        _tee.Route = TeeRoute.PrimaryOnly;
        _speaker.Dispose();
        State = SpeakerState.Disposed;
    }
}

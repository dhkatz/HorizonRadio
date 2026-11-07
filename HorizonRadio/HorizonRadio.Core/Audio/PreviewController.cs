using System;
using System.Collections.Generic;
using System.Threading;
using HorizonRadio.Core.Sources.Config;

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
/// A small state machine (<see cref="SpeakerState"/>) run on the UI thread:
/// user calls act inline, while device callbacks (the speaker's
/// <see cref="ISpeakerOutput.Lost"/>, OS endpoint notifications) arrive on
/// other threads and are posted to it, never acted on where they're raised.
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
    private readonly SourceConfigStore _store;
    private readonly ISpeakerOutput _speaker;
    private readonly IAudioDeviceWatcher? _watcher;
    private readonly Action<Action> _post;
    private readonly Action _save;

    // The DeviceId the open speaker was asked for (it may have fallen back to the
    // default if that device was gone).
    private string? _openedFor;

    // Volume persistence is debounced: a slider drag fires SetVolume dozens of
    // times, so we coalesce them and write the store once the slider goes quiet.
    // _persistLock serializes the disk write against the device/enable-change
    // paths (UI thread) and the debounce timer (thread pool). Without the
    // timer the position was only saved on Dispose, so a crash lost it.
    private readonly object _persistLock = new();
    private readonly Timer _persistTimer;
    private static readonly TimeSpan PersistDebounce = TimeSpan.FromMilliseconds(800);
    private bool _volumeDirty;
    private bool _disposed;

    /// <param name="post">Runs an action later on the UI thread (the thread every
    /// public member is called on). Device callbacks go through it.</param>
    public PreviewController(TeePcmSink tee, SourceConfigStore store, Action<Action> post)
        : this(tee, store, post, new SpeakerPcmSink(), WasapiDeviceWatcher.TryCreate(), () => store.SaveToDisk())
    {
    }

    internal PreviewController(TeePcmSink tee, SourceConfigStore store, Action<Action> post,
        ISpeakerOutput speaker, IAudioDeviceWatcher? watcher, Action save)
    {
        _tee = tee;
        _store = store;
        _post = post;
        _speaker = speaker;
        _watcher = watcher;
        _save = save;
        _persistTimer = new Timer(_ => FlushVolumeIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
        Enabled = store.PreviewEnabled;
        DeviceId = store.PreviewDeviceId;
        Volume = store.PreviewVolume;
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

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
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
        if (_disposed) return;
        if (Enabled != on)
        {
            Enabled = on;
            _store.PreviewEnabled = on;
            Persist();
        }
        Fire(Trigger.User);
    }

    /// <summary>Make <paramref name="deviceId"/> (null = system default) the output:
    /// local monitoring on, on that device. Picking the device already chosen
    /// retries it if it's faulted.</summary>
    public void SelectDevice(string? deviceId)
    {
        if (_disposed) return;
        if (!Enabled || DeviceId != deviceId)
        {
            Enabled = true;
            DeviceId = deviceId;
            _store.PreviewEnabled = true;
            _store.PreviewDeviceId = deviceId;
            Persist();
        }
        Fire(Trigger.User);
    }

    public void SetVolume(double volume)
    {
        if (_disposed) return;
        Volume = volume;
        // Volume is the raw slider *position*; the speaker takes a linear gain.
        // Run it through the perceptual taper so the fader eases down smoothly.
        _speaker.Volume = VolumeTaper.ToGain(volume);
        // Don't hit disk on every slider tick — a drag fires this dozens of
        // times. Update the in-memory pref and (re)arm the debounce timer so the
        // value lands on disk shortly after the slider goes quiet. Arming under
        // the lock with the _disposed check means we never call Change() on an
        // already-disposed timer (a slider tick during shutdown teardown).
        lock (_persistLock)
        {
            if (_disposed) return;
            _store.PreviewVolume = volume;
            _volumeDirty = true;
            _persistTimer.Change(PersistDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    // Device callbacks: any thread. Hand off to the UI thread; never act here
    // (the speaker may be calling from its render thread, the watcher from a COM
    // worker that mustn't block).
    private void OnSpeakerLost() => _post(() => Fire(Trigger.Lost));

    private void OnDefaultDeviceChanged(string? id) => _post(() => Fire(Trigger.DefaultChanged, id));

    private void OnDeviceAvailabilityChanged(string id, bool available) =>
        _post(() => Fire(available ? Trigger.DeviceAvailable : Trigger.DeviceUnavailable, id));

    /// <summary>The state machine: every transition happens here, on the UI thread.</summary>
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

    private void FlushVolumeIfDirty()
    {
        lock (_persistLock)
        {
            if (!_volumeDirty) return;
            _save();
            _volumeDirty = false;
        }
    }

    private void Persist()
    {
        lock (_persistLock)
        {
            _save();
            _volumeDirty = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Mark disposed under the lock first so any in-flight SetVolume returns
        // without re-arming the timer we're about to dispose.
        lock (_persistLock) _disposed = true;
        _persistTimer.Dispose();
        // Flush any volume change that hadn't hit its debounce window yet
        // (lock-guarded, same as every other access to _volumeDirty).
        FlushVolumeIfDirty();

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

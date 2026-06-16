using System;
using System.Collections.Generic;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// Owns the local "test playback" lifecycle: a <see cref="SpeakerPcmSink"/>
/// attached to the pipeline's <see cref="TeePcmSink"/>, plus the persisted
/// enable/device/volume preferences. The UI drives this; the audio sources
/// are unaware it exists.
/// </summary>
public sealed class PreviewController : IDisposable
{
    private readonly TeePcmSink _tee;
    private readonly SourceConfigStore _store;
    private SpeakerPcmSink? _speaker;
    private bool _volumeDirty;

    public PreviewController(TeePcmSink tee, SourceConfigStore store)
    {
        _tee = tee;
        _store = store;
        Enabled = store.PreviewEnabled;
        DeviceId = store.PreviewDeviceId;
        Volume = store.PreviewVolume;

        if (Enabled) StartSpeaker();
    }

    public bool Enabled { get; private set; }
    public string? DeviceId { get; private set; }

    /// <summary>The master volume <em>slider position</em> (0..1), persisted as
    /// <c>previewVolume</c>. Converted to a linear gain via <see cref="VolumeTaper"/>
    /// before it reaches the speaker. This is the same position the in-game bridge
    /// uses as a pre-amp, so the one slider governs both outputs.</summary>
    public double Volume { get; private set; }

    /// <summary>True when local monitoring is enabled and the speaker device
    /// actually opened. False if the device failed to open (e.g. unplugged),
    /// so callers can tell the selected output isn't reachable.</summary>
    public bool IsSpeakerActive => Enabled && _speaker?.IsPlaying == true;

    /// <summary>Active render endpoints for the UI's device picker (first entry
    /// is "system default", with a null id).</summary>
    public static IReadOnlyList<AudioDevice> Devices => SpeakerPcmSink.EnumerateRenderDevices();

    public void SetEnabled(bool on)
    {
        if (Enabled == on) return;
        Enabled = on;
        if (on) StartSpeaker(); else StopSpeaker();
        _store.PreviewEnabled = on;
        Persist();
    }

    public void SetDevice(string? deviceId)
    {
        if (DeviceId == deviceId) return;
        DeviceId = deviceId;
        // Re-open on the new device only if we're currently playing, and
        // re-assert routing in case the new device fails to open.
        if (Enabled) StartSpeaker();
        _store.PreviewDeviceId = deviceId;
        Persist();
    }

    public void SetVolume(double volume)
    {
        Volume = volume;
        // Volume is the raw slider *position*; the speaker takes a linear gain.
        // Run it through the perceptual taper so the fader eases down smoothly.
        if (_speaker != null) _speaker.Volume = VolumeTaper.ToGain(volume);
        // Update the in-memory pref but don't hit disk on every slider tick —
        // a drag fires this dozens of times. Flushed on Dispose, or sooner by
        // the next enable/device change (which persist the whole store).
        _store.PreviewVolume = volume;
        _volumeDirty = true;
    }

    private void StartSpeaker()
    {
        _speaker ??= new SpeakerPcmSink();
        _speaker.Volume = VolumeTaper.ToGain(Volume);
        _speaker.Start(DeviceId);
        if (_speaker.IsPlaying)
        {
            _tee.AttachPreview(_speaker);
            // Local monitoring is the single active destination — silence the
            // game bridge so the output picker behaves exclusively.
            _tee.SetPrimaryEnabled(false);
        }
        else
        {
            // The device didn't open: don't gate the bridge into silence. Leave
            // the game pipe live; the UI's reachability check pauses + toasts.
            _tee.DetachPreview();
            _tee.SetPrimaryEnabled(true);
        }
    }

    private void StopSpeaker()
    {
        _tee.SetPrimaryEnabled(true);
        _tee.DetachPreview();
        _speaker?.Stop();
    }

    private void Persist()
    {
        _store.SaveToDisk();
        _volumeDirty = false;
    }

    public void Dispose()
    {
        if (_volumeDirty) Persist();
        _tee.SetPrimaryEnabled(true);
        _tee.DetachPreview();
        _speaker?.Dispose();
        _speaker = null;
    }
}

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
        _store.SaveToDisk();
    }

    public void SetDevice(string? deviceId)
    {
        if (DeviceId == deviceId) return;
        DeviceId = deviceId;
        // Re-open on the new device only if we're currently playing.
        if (Enabled) _speaker?.Start(deviceId);
        _store.PreviewDeviceId = deviceId;
        _store.SaveToDisk();
    }

    public void SetVolume(double volume)
    {
        Volume = volume;
        if (_speaker != null) _speaker.Volume = (float)volume;
        _store.PreviewVolume = volume;
        _store.SaveToDisk();
    }

    private void StartSpeaker()
    {
        _speaker ??= new SpeakerPcmSink();
        _speaker.Volume = (float)Volume;
        _speaker.Start(DeviceId);
        _tee.AttachPreview(_speaker);
        // Local monitoring is the single active destination — silence the game
        // bridge so the output picker behaves exclusively.
        _tee.SetPrimaryEnabled(false);
    }

    private void StopSpeaker()
    {
        _tee.SetPrimaryEnabled(true);
        _tee.DetachPreview();
        _speaker?.Stop();
    }

    public void Dispose()
    {
        _tee.SetPrimaryEnabled(true);
        _tee.DetachPreview();
        _speaker?.Dispose();
        _speaker = null;
    }
}

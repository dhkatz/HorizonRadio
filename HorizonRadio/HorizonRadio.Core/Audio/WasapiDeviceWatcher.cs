using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// <see cref="IAudioDeviceWatcher"/> over WASAPI's <see cref="IMMNotificationClient"/>.
/// Windows calls it on a COM worker thread and forbids blocking or calling back into
/// the enumerator from there, so it only raises events; listeners post them on.
/// </summary>
public sealed class WasapiDeviceWatcher : IAudioDeviceWatcher, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator;
    private bool _disposed;

    private WasapiDeviceWatcher(MMDeviceEnumerator enumerator) => _enumerator = enumerator;

    private static void Log(string msg) => Trace.WriteLine($"[hzn-speaker] {msg}");

    public event Action<string?>? DefaultDeviceChanged;
    public event Action<string, bool>? DeviceAvailabilityChanged;

    /// <summary>A registered watcher, or null if the OS refused (no audio service).</summary>
    public static WasapiDeviceWatcher? TryCreate()
    {
        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            var watcher = new WasapiDeviceWatcher(enumerator);
            var hr = enumerator.RegisterEndpointNotificationCallback(watcher);
            if (hr != 0) throw new InvalidOperationException($"register failed (0x{hr:X8})");
            return watcher;
        }
        catch (Exception ex)
        {
            Log($"device notifications unavailable: {ex.Message}");
            enumerator?.Dispose();
            return null;
        }
    }

    // SpeakerPcmSink opens the Render/Multimedia default, so that's the role to follow.
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            DefaultDeviceChanged?.Invoke(string.IsNullOrEmpty(defaultDeviceId) ? null : defaultDeviceId);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        DeviceAvailabilityChanged?.Invoke(deviceId, newState == DeviceState.Active);

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) =>
        DeviceAvailabilityChanged?.Invoke(pwstrDeviceId, true);

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) =>
        DeviceAvailabilityChanged?.Invoke(deviceId, false);

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); }
        catch (Exception ex) { Log($"unregister: {ex.Message}"); }
        _enumerator.Dispose();
    }
}

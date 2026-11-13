using System.Runtime.Versioning;
using HorizonRadio.Core.Audio;

namespace HorizonRadio.Platform.Windows;

/// <summary>The speakers Windows lists (WASAPI render endpoints).</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiSpeakers : ISpeakers
{
    public IReadOnlyList<AudioDevice> Devices => SpeakerPcmSink.EnumerateRenderDevices();

    public ISpeakerOutput CreateOutput() => new SpeakerPcmSink();

    public IAudioDeviceWatcher? WatchDevices() => WasapiDeviceWatcher.TryCreate();
}

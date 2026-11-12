using System;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// A local render device that PCM can be pushed to: the seam between
/// <see cref="PreviewController"/> and WASAPI (<see cref="SpeakerPcmSink"/>), so the
/// controller's state machine can be tested without real hardware.
/// </summary>
public interface ISpeakerOutput : IPcmSink, IDisposable
{
    /// <summary>Close any open device, then open <paramref name="deviceId"/> (null = the
    /// system default; a saved device that's gone falls back to the default). False if
    /// nothing could be opened. Never throws.</summary>
    bool Open(string? deviceId);

    /// <summary>Release the device. Idempotent.</summary>
    void Close();

    /// <summary>True while a device is open and playing what <see cref="IPcmSink.Send"/> pushes.
    /// Goes false on its own when the device is lost (see <see cref="Lost"/>).</summary>
    bool IsOpen { get; }

    /// <summary>The endpoint id actually opened (after any fallback), or null when closed.</summary>
    string? OpenedDeviceId { get; }

    /// <summary>Linear gain in [0, 1].</summary>
    float Volume { get; set; }

    /// <summary>Raised when an open device stops on its own (unplugged, disabled, driver
    /// reset). <see cref="IsOpen"/> is already false. May be raised on any thread.</summary>
    event Action? Lost;
}

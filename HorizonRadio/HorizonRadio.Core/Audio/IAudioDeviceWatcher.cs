using System;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// Render-endpoint notifications from the OS. Events may be raised on any thread
/// (WASAPI raises them on a COM worker), so listeners must hand them off rather
/// than act inline.
/// </summary>
public interface IAudioDeviceWatcher : IDisposable
{
    /// <summary>The default render device changed; carries the new default's id
    /// (null if there's no render device left).</summary>
    event Action<string?>? DefaultDeviceChanged;

    /// <summary>A render device became available (true) or unavailable (false).</summary>
    event Action<string, bool>? DeviceAvailabilityChanged;
}

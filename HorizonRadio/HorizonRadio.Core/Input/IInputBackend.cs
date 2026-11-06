using System;

namespace HorizonRadio.Core.Input;

/// <summary>
/// A source of raw input events from one device family (global keyboard/mouse,
/// or controllers). Implementations encapsulate their native library entirely;
/// callers only ever see <see cref="InputBinding"/>s. A backend that can't run
/// on the current platform reports <see cref="IsAvailable"/> = false and emits
/// nothing rather than throwing, so the rest of the app stays cross-platform.
/// </summary>
public interface IInputBackend : IDisposable
{
    /// <summary>Short, user-facing name (shown in the Controls tab).</summary>
    string Name { get; }

    /// <summary>False when the underlying native hook/library couldn't be
    /// initialized on this platform (e.g. Wayland, missing permission, or a
    /// missing native lib). The backend then no-ops.</summary>
    bool IsAvailable { get; }

    /// <summary>Raised on a backend-owned thread for every discrete input
    /// (key/button press, axis trigger). Handlers must marshal to their own
    /// thread before touching UI state.</summary>
    event Action<InputBinding>? InputReceived;

    /// <summary>Begin listening. Idempotent; safe to call when unavailable.</summary>
    void Start();
}

/// <summary>Optional capability on a backend that owns enumerable physical
/// devices (controllers / wheels / joysticks), so the UI can let the user pick
/// which one to bind. Names are display names; <see cref="DevicesChanged"/>
/// fires on a backend thread when devices connect or disconnect.</summary>
public interface IControllerDeviceSource
{
    IReadOnlyList<string> Devices { get; }
    event Action? DevicesChanged;
}

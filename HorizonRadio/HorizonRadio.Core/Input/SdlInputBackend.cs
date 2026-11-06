using System;
using System.Collections.Generic;
using HorizonRadio.Core.Diagnostics;
using SDL3;
using Thread = System.Threading.Thread;

namespace HorizonRadio.Core.Input;

/// <summary>
/// Controller / wheel / joystick backend over SDL3 (SDL3-CS). All SDL calls run
/// on one dedicated pump thread with background events enabled, so bindings fire
/// even while the game has focus. SDL3 is used (over SDL2) for the widest device
/// coverage — modern gamepads, the new Steam Controller, etc.
///
/// Two paths:
/// <list type="bullet">
///   <item>Devices SDL recognizes as <b>gamepads</b> go through the gamepad API,
///   giving semantic buttons (face / d-pad / bumpers / triggers) and a
///   <see cref="ControllerStyle"/> for brand-matched glyphs.</item>
///   <item>Everything else — racing wheels, HOTAS — goes through the raw
///   <b>joystick</b> API as indexed buttons/axes (no glyph).</item>
/// </list>
/// A recognized gamepad is also a joystick to SDL; we ignore its joystick events
/// to avoid double-emitting.
/// </summary>
public sealed class SdlInputBackend : IInputBackend, IControllerDeviceSource
{
    public string Name => "Controllers (SDL)";
    public bool IsAvailable { get; private set; } = true;
    public event Action<InputBinding>? InputReceived;

    // Snapshot of connected controller-category device names, for the UI's
    // device picker. Rebuilt on the pump thread; read from the UI thread.
    private volatile IReadOnlyList<string> _deviceSnapshot = Array.Empty<string>();
    public IReadOnlyList<string> Devices => _deviceSnapshot;
    public event Action? DevicesChanged;

    private Thread? _thread;
    private volatile bool _running;

    // instance id -> (gamepad handle, name, style)
    private readonly Dictionary<uint, (IntPtr Ptr, string Name, ControllerStyle Style)> _gamepads = new();
    // instance id -> (joystick handle, name) for raw devices (wheels/HOTAS)
    private readonly Dictionary<uint, (IntPtr Ptr, string Name)> _joysticks = new();
    // (instance id, axis) -> already past the trigger threshold? (edge detect)
    private readonly Dictionary<(uint, int), bool> _axisLatched = new();

    private const short AxisTrigger = 24000; // ~73% deflection

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(PumpLoop) { IsBackground = true, Name = "sdl-input" };
        _thread.Start();
    }

    private void PumpLoop()
    {
        try
        {
            // Init + poll must happen on the same thread; do it here.
            SDL.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            if (!SDL.Init(SDL.InitFlags.Joystick | SDL.InitFlags.Gamepad | SDL.InitFlags.Events))
            {
                ProcessConsole.Append("controls", $"SDL init failed: {SDL.GetError()}");
                IsAvailable = false;
                return;
            }
            ProcessConsole.Append("controls", "SDL3 initialized");

            // Open whatever is already connected.
            var gamepads = SDL.GetGamepads(out _);
            if (gamepads != null)
                foreach (var id in gamepads) OpenGamepad(id);
            var joysticks = SDL.GetJoysticks(out _);
            if (joysticks != null)
                foreach (var id in joysticks)
                    if (!SDL.IsGamepad(id)) OpenJoystick(id);

            while (_running)
            {
                while (SDL.PollEvent(out var e))
                {
                    switch ((SDL.EventType)e.Type)
                    {
                        case SDL.EventType.GamepadAdded: OpenGamepad(e.GDevice.Which); break;
                        case SDL.EventType.GamepadRemoved: CloseGamepad(e.GDevice.Which); break;
                        case SDL.EventType.GamepadButtonDown:
                            if (e.GButton.Down) EmitGamepadButton(e.GButton.Which, (SDL.GamepadButton)e.GButton.Button);
                            break;
                        case SDL.EventType.GamepadAxisMotion:
                            EmitGamepadAxis(e.GAxis.Which, (SDL.GamepadAxis)e.GAxis.Axis, e.GAxis.Value);
                            break;

                        // Joystick events: only for devices NOT handled as gamepads.
                        case SDL.EventType.JoystickAdded:
                            if (!SDL.IsGamepad(e.JDevice.Which)) OpenJoystick(e.JDevice.Which);
                            break;
                        case SDL.EventType.JoystickRemoved:
                            if (_joysticks.ContainsKey(e.JDevice.Which)) CloseJoystick(e.JDevice.Which);
                            break;
                        case SDL.EventType.JoystickButtonDown:
                            if (e.JButton.Down && _joysticks.ContainsKey(e.JButton.Which))
                                EmitJoystickButton(e.JButton.Which, e.JButton.Button);
                            break;
                        case SDL.EventType.JoystickAxisMotion:
                            if (_joysticks.ContainsKey(e.JAxis.Which))
                                EmitJoystickAxis(e.JAxis.Which, e.JAxis.Axis, e.JAxis.Value);
                            break;
                    }
                }
                Thread.Sleep(8); // ~120 Hz; cheap and plenty for discrete binds
            }
        }
        catch (Exception ex)
        {
            ProcessConsole.Append("controls", $"SDL pump stopped: {ex.GetType().Name}: {ex.Message}");
            IsAvailable = false;
        }
        finally
        {
            foreach (var g in _gamepads.Values) SDL.CloseGamepad(g.Ptr);
            foreach (var d in _joysticks.Values) SDL.CloseJoystick(d.Ptr);
            _gamepads.Clear();
            _joysticks.Clear();
            try { SDL.Quit(); } catch { /* native may be gone */ }
        }
    }

    // -- device open/close --

    private void OpenGamepad(uint instanceId)
    {
        // Idempotent: SDL also delivers an "added" event for devices already
        // connected at init, so startup enumeration + the event stream would
        // otherwise open (and leak) the same device twice.
        if (_gamepads.ContainsKey(instanceId)) return;
        var gp = SDL.OpenGamepad(instanceId);
        if (gp == IntPtr.Zero) return;
        string name = SDL.GetGamepadName(gp) ?? "";
        if (string.IsNullOrEmpty(name)) name = $"Controller {instanceId}";
        var style = ClassifyStyle(SDL.GetGamepadType(gp), name);
        _gamepads[instanceId] = (gp, name, style);
        ProcessConsole.Append("controls", $"controller connected: {name} ({style})");
        RefreshDevices();
    }

    private void CloseGamepad(uint instanceId)
    {
        if (!_gamepads.Remove(instanceId, out var g)) return;
        SDL.CloseGamepad(g.Ptr);
        ForgetAxisLatches(instanceId);
        RefreshDevices();
    }

    private void OpenJoystick(uint instanceId)
    {
        if (_joysticks.ContainsKey(instanceId)) return; // see OpenGamepad
        var js = SDL.OpenJoystick(instanceId);
        if (js == IntPtr.Zero) return;
        string name = SDL.GetJoystickName(js) ?? "";
        if (string.IsNullOrEmpty(name)) name = $"Joystick {instanceId}";
        _joysticks[instanceId] = (js, name);
        ProcessConsole.Append("controls", $"joystick connected: {name}");
        RefreshDevices();
    }

    private void CloseJoystick(uint instanceId)
    {
        if (!_joysticks.Remove(instanceId, out var d)) return;
        SDL.CloseJoystick(d.Ptr);
        ForgetAxisLatches(instanceId);
        RefreshDevices();
    }

    // Drop a disconnected device's axis edge-latches so they don't accumulate
    // and can't suppress the first edge if SDL ever reuses the instance id.
    private void ForgetAxisLatches(uint instanceId)
    {
        List<(uint, int)>? stale = null;
        foreach (var key in _axisLatched.Keys)
            if (key.Item1 == instanceId) (stale ??= new()).Add(key);
        if (stale != null)
            foreach (var key in stale) _axisLatched.Remove(key);
    }

    // Rebuild the device-name snapshot and notify the UI. Runs on the pump thread.
    private void RefreshDevices()
    {
        var list = new List<string>(_gamepads.Count + _joysticks.Count);
        foreach (var g in _gamepads.Values) list.Add(g.Name);
        foreach (var d in _joysticks.Values) list.Add(d.Name);
        _deviceSnapshot = list;
        DevicesChanged?.Invoke();
    }

    private static ControllerStyle ClassifyStyle(SDL.GamepadType type, string name)
    {
        // Name heuristic first: Steam Deck / Steam Controller don't always report
        // a distinct type (and the new Steam Controller's gamepad mapping is still
        // landing upstream), so detect by name.
        if (name.Contains("Steam Deck", StringComparison.OrdinalIgnoreCase)) return ControllerStyle.SteamDeck;
        if (name.Contains("Steam Controller", StringComparison.OrdinalIgnoreCase)) return ControllerStyle.SteamController;

        return type switch
        {
            SDL.GamepadType.Xbox360 or SDL.GamepadType.XboxOne => ControllerStyle.Xbox,
            SDL.GamepadType.PS3 or SDL.GamepadType.PS4 or SDL.GamepadType.PS5 => ControllerStyle.PlayStation,
            SDL.GamepadType.NintendoSwitchPro
                or SDL.GamepadType.NintendoSwitchJoyconLeft
                or SDL.GamepadType.NintendoSwitchJoyconRight
                or SDL.GamepadType.NintendoSwitchJoyconPair => ControllerStyle.Switch,
            _ => ControllerStyle.Generic,
        };
    }

    // -- emit (gamepad) --

    private void EmitGamepadButton(uint instanceId, SDL.GamepadButton button)
    {
        if (!_gamepads.TryGetValue(instanceId, out var g)) return;
        var (token, label) = ButtonTokenLabel(button);
        InputReceived?.Invoke(new InputBinding(
            InputKind.GamepadButton, g.Name, (int)button, label,
            GlyphId: "pad." + token, Style: g.Style));
    }

    private void EmitGamepadAxis(uint instanceId, SDL.GamepadAxis axis, short value)
    {
        if (!_gamepads.TryGetValue(instanceId, out var g)) return;
        var key = (instanceId, 100 + (int)axis); // +100 so it can't collide with joystick axes
        bool past = value >= AxisTrigger || value <= -AxisTrigger;
        bool wasLatched = _axisLatched.TryGetValue(key, out var l) && l;
        _axisLatched[key] = past;
        if (!past || wasLatched) return; // rising edge only

        bool positive = value > 0;
        var (token, label) = AxisTokenLabel(axis);
        int code = 1000 + (int)axis * 2 + (positive ? 0 : 1);
        InputReceived?.Invoke(new InputBinding(
            InputKind.GamepadAxis, g.Name, code, label,
            GlyphId: "pad." + token, Style: g.Style));
    }

    // SDL3 names face buttons positionally (South/East/West/North); map to the
    // familiar Xbox-style tokens our glyphs use.
    private static (string Token, string Label) ButtonTokenLabel(SDL.GamepadButton b) => b switch
    {
        SDL.GamepadButton.South => ("a", "A"),
        SDL.GamepadButton.East => ("b", "B"),
        SDL.GamepadButton.West => ("x", "X"),
        SDL.GamepadButton.North => ("y", "Y"),
        SDL.GamepadButton.DPadUp => ("dpad_up", "D-Pad Up"),
        SDL.GamepadButton.DPadDown => ("dpad_down", "D-Pad Down"),
        SDL.GamepadButton.DPadLeft => ("dpad_left", "D-Pad Left"),
        SDL.GamepadButton.DPadRight => ("dpad_right", "D-Pad Right"),
        SDL.GamepadButton.LeftShoulder => ("lb", "Left Bumper"),
        SDL.GamepadButton.RightShoulder => ("rb", "Right Bumper"),
        SDL.GamepadButton.Start => ("start", "Start"),
        SDL.GamepadButton.Back => ("back", "Back"),
        SDL.GamepadButton.Guide => ("guide", "Guide"),
        SDL.GamepadButton.LeftStick => ("ls", "Left Stick"),
        SDL.GamepadButton.RightStick => ("rs", "Right Stick"),
        _ => (b.ToString().ToLowerInvariant(), b.ToString()),
    };

    private static (string Token, string Label) AxisTokenLabel(SDL.GamepadAxis a) => a switch
    {
        SDL.GamepadAxis.LeftTrigger => ("lt", "Left Trigger"),
        SDL.GamepadAxis.RightTrigger => ("rt", "Right Trigger"),
        SDL.GamepadAxis.LeftX or SDL.GamepadAxis.LeftY => ("ls", "Left Stick"),
        SDL.GamepadAxis.RightX or SDL.GamepadAxis.RightY => ("rs", "Right Stick"),
        _ => (a.ToString().ToLowerInvariant(), a.ToString()),
    };

    // -- emit (raw joystick: wheels / HOTAS) --

    private void EmitJoystickButton(uint instanceId, byte button)
    {
        var name = _joysticks.TryGetValue(instanceId, out var d) ? d.Name : $"Joystick {instanceId}";
        InputReceived?.Invoke(new InputBinding(
            InputKind.JoystickButton, name, button, $"{name} · Button {button + 1}"));
    }

    private void EmitJoystickAxis(uint instanceId, byte axis, short value)
    {
        var key = (instanceId, (int)axis);
        bool past = value >= AxisTrigger || value <= -AxisTrigger;
        bool wasLatched = _axisLatched.TryGetValue(key, out var l) && l;
        _axisLatched[key] = past;
        if (!past || wasLatched) return; // rising edge only

        var name = _joysticks.TryGetValue(instanceId, out var d) ? d.Name : $"Joystick {instanceId}";
        bool positive = value > 0;
        int code = axis * 2 + (positive ? 0 : 1);
        InputReceived?.Invoke(new InputBinding(
            InputKind.JoystickAxis, name, code, $"{name} · Axis {axis + 1}{(positive ? "+" : "-")}"));
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(700); } catch { /* ignore */ }
        // Device cleanup + SDL.Quit happen in the pump thread's finally.
    }
}

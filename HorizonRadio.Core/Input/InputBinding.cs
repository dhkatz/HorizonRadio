namespace HorizonRadio.Core.Input;

/// <summary>The class of physical input a binding refers to.</summary>
public enum InputKind
{
    Keyboard,
    MouseButton,
    /// <summary>A button on a device SDL recognizes as a game controller —
    /// carries a semantic <c>Code</c> (A/B/X/Y/d-pad/…) and a <see cref="ControllerStyle"/>.</summary>
    GamepadButton,
    /// <summary>A trigger/stick axis on a recognized game controller.</summary>
    GamepadAxis,
    /// <summary>A raw joystick button (wheels, HOTAS, anything SDL doesn't map);
    /// just an index, no semantic meaning, no glyph.</summary>
    JoystickButton,
    /// <summary>A raw joystick axis crossing a threshold.</summary>
    JoystickAxis,
}

/// <summary>The brand/family of a game controller, used to pick matching glyph
/// art. Derived from SDL's controller type (+ a name heuristic for Steam).</summary>
public enum ControllerStyle
{
    Generic,
    Xbox,
    PlayStation,
    Switch,
    SteamDeck,
    SteamController,
}

/// <summary>
/// A serializable identity for one physical input — a key, a mouse button, or
/// a controller/wheel/joystick button or axis. <see cref="Label"/> is the
/// human-readable form for the UI and is deterministic per input, but is
/// deliberately excluded from lookup/equality via <see cref="Key"/> so that a
/// re-worded label never orphans a saved binding.
/// </summary>
/// <param name="Kind">Which input family this belongs to.</param>
/// <param name="Device">Device name for controllers/joysticks; <c>null</c> for
/// keyboard and mouse (which are singletons from our point of view).</param>
/// <param name="Code">Numeric code within the device (key code, mouse button,
/// joystick button index, or encoded axis+direction).</param>
/// <param name="Label">Display text, e.g. "Space", "Logitech G29 · Button 4".</param>
/// <param name="GlyphId">Library-agnostic art token the UI maps to a glyph image,
/// e.g. "key.space", "mouse.left", "pad.a". Null when there is no glyph (raw
/// joystick buttons). Display metadata only — not part of <see cref="Key"/>.</param>
/// <param name="Style">Controller family, for picking brand-matched glyph art.
/// Set only for gamepad bindings. Display metadata only.</param>
public sealed record InputBinding(
    InputKind Kind,
    string? Device,
    int Code,
    string Label,
    string? GlyphId = null,
    ControllerStyle? Style = null)
{
    /// <summary>Stable identity used for runtime matching and persistence —
    /// ignores the display-only fields (<see cref="Label"/>, <see cref="GlyphId"/>,
    /// <see cref="Style"/>).</summary>
    public string Key => $"{Kind}|{Device}|{Code}";
}

/// <summary>Well-known key codes the UI needs by name without taking a direct
/// dependency on the keyboard backend's library types.</summary>
public static class KeyboardKeys
{
    /// <summary>Escape — used by the Controls tab to cancel a capture.</summary>
    public static readonly int Escape = (int)SharpHook.Data.KeyCode.VcEscape;
}

/// <summary>Coarse device grouping — one binding slot per category per action,
/// matching the Controls tab's columns.</summary>
public enum InputCategory
{
    KeyboardMouse,
    Controller,
}

public static class InputCategories
{
    public static InputCategory Of(InputKind kind) => kind switch
    {
        InputKind.Keyboard or InputKind.MouseButton => InputCategory.KeyboardMouse,
        _ => InputCategory.Controller,
    };

    public static InputCategory Of(InputBinding binding) => Of(binding.Kind);
}

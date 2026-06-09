using System;
using HorizonRadio.Core.Diagnostics;
using SharpHook;
using SharpHook.Data;

namespace HorizonRadio.Core.Input;

/// <summary>
/// Global keyboard + mouse-button backend over libuiohook (SharpHook). The
/// hook is system-wide, so bindings fire while the game — not our window — has
/// focus. It is observe-only: the press is NOT suppressed, so the same key
/// still reaches the game (pick keys you don't use while driving). Reports
/// unavailable and no-ops where the OS hook can't be installed (e.g. Wayland,
/// or a missing macOS Accessibility grant).
/// </summary>
public sealed class SharpHookBackend : IInputBackend
{
    public string Name => "Keyboard & mouse";
    public bool IsAvailable { get; private set; } = true;
    public event Action<InputBinding>? InputReceived;

    private SimpleGlobalHook? _hook;

    public void Start()
    {
        if (_hook != null) return;
        try
        {
            // runAsyncOnBackgroundThread: the uiohook event loop runs on its
            // own thread so RunAsync returns immediately and we don't block.
            _hook = new SimpleGlobalHook(GlobalHookType.All, null, true);
            _hook.KeyPressed += OnKeyPressed;
            _hook.MousePressed += OnMousePressed;
            _ = _hook.RunAsync();
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            ProcessConsole.Append("controls", $"global hook unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var code = e.Data.KeyCode;
        if (code == KeyCode.VcUndefined) return;
        var name = KeyName(code); // "Vc"-stripped, e.g. "Space", "A", "LeftShift"
        InputReceived?.Invoke(new InputBinding(
            InputKind.Keyboard, null, (int)code, name, GlyphId: "key." + name.ToLowerInvariant()));
    }

    private void OnMousePressed(object? sender, MouseHookEventArgs e)
    {
        var btn = e.Data.Button;
        if (btn == MouseButton.NoButton) return;
        var (token, label) = MouseTokenLabel(btn);
        InputReceived?.Invoke(new InputBinding(
            InputKind.MouseButton, null, (int)btn, label, GlyphId: "mouse." + token));
    }

    // KeyCode names are "Vc"-prefixed virtual codes (VcSpace, VcA, …); strip
    // the prefix for a readable label / token base.
    private static string KeyName(KeyCode code)
    {
        var s = code.ToString();
        return s.StartsWith("Vc", StringComparison.Ordinal) ? s[2..] : s;
    }

    private static (string Token, string Label) MouseTokenLabel(MouseButton btn) => btn switch
    {
        MouseButton.Button1 => ("left", "Mouse Left"),
        MouseButton.Button2 => ("right", "Mouse Right"),
        MouseButton.Button3 => ("middle", "Mouse Middle"),
        _ => ($"button{(int)btn}", $"Mouse Button {(int)btn}"),
    };

    public void Dispose()
    {
        if (_hook == null) return;
        try
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.MousePressed -= OnMousePressed;
            _hook.Dispose();
        }
        catch { /* hook may already be torn down */ }
        _hook = null;
    }
}

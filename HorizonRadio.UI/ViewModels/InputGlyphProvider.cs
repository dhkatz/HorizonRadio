using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HorizonRadio.Core.Input;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Maps an <see cref="InputBinding"/>'s library-agnostic <c>GlyphId</c> token
/// (and <see cref="ControllerStyle"/>) to a Kenney Input Prompts glyph image
/// under <c>Assets/InputGlyphs/</c>. Returns null when there's no art (raw
/// joystick buttons, unmapped keys) so the UI falls back to the text label.
/// Loaded bitmaps are cached by relative path.
/// </summary>
public static class InputGlyphProvider
{
    private const string Base = "avares://HorizonRadio.UI/Assets/InputGlyphs/";
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();

    public static Bitmap? Resolve(InputBinding? binding)
    {
        if (binding?.GlyphId is not { } glyph) return null;
        foreach (var rel in Candidates(glyph, binding.Style))
        {
            var bmp = Cache.GetOrAdd(rel, Load);
            if (bmp != null) return bmp;
        }
        return null;
    }

    private static Bitmap? Load(string rel)
    {
        try
        {
            var uri = new Uri(Base + rel);
            if (!AssetLoader.Exists(uri)) return null;
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    // Ordered candidate relative paths; first that exists wins.
    private static IEnumerable<string> Candidates(string glyphId, ControllerStyle? style)
    {
        var dot = glyphId.IndexOf('.');
        if (dot < 0) yield break;
        var kind = glyphId[..dot];
        var token = glyphId[(dot + 1)..];

        switch (kind)
        {
            case "key":
                yield return $"keyboard/keyboard_{KeyAlias(token)}.png";
                break;
            case "mouse":
                if (MouseFiles.TryGetValue(token, out var mf)) yield return $"mouse/{mf}.png";
                break;
            case "pad":
                var effStyle = style ?? ControllerStyle.Generic;
                var (folder, map) = StyleTable(effStyle);
                if (map.TryGetValue(token, out var pf)) yield return $"{folder}/{pf}.png";
                // Fall back to the Xbox-styled glyph for tokens this style has no
                // art for (keeps a real button shape) before the generic blob.
                if (effStyle != ControllerStyle.Xbox && Xbox.TryGetValue(token, out var xf))
                    yield return $"xbox/{xf}.png";
                yield return "generic/generic_button.png";
                break;
        }
    }

    // SharpHook key names (lower-cased, Vc-stripped) -> Kenney keyboard token.
    // Anything not listed is tried verbatim (a-z, 0-9, f1-f12, space, escape,
    // tab, enter, backspace, delete, insert, home, end, capslock, numlock,
    // pause, printscreen, comma, period, minus, equals, semicolon, quote,
    // underscore, … all match Kenney filenames directly).
    private static string KeyAlias(string token) => token switch
    {
        "up" => "arrow_up",
        "down" => "arrow_down",
        "left" => "arrow_left",
        "right" => "arrow_right",
        "leftshift" or "rightshift" => "shift",
        "leftcontrol" or "rightcontrol" => "ctrl",
        "leftalt" or "rightalt" => "alt",
        "leftmeta" or "rightmeta" => "win",
        "pageup" => "page_up",
        "pagedown" => "page_down",
        "scrolllock" => "scroll_lock",
        "slash" => "slash_forward",
        "backslash" => "slash_back",
        "openbracket" => "bracket_open",
        "closebracket" => "bracket_close",
        "backquote" => "tilde",
        "numpadenter" => "numpad_enter",
        "numpadadd" => "numpad_plus",
        _ => token,
    };

    private static readonly Dictionary<string, string> MouseFiles = new()
    {
        ["left"] = "mouse_left",
        ["right"] = "mouse_right",
        ["middle"] = "mouse_scroll",
        ["button4"] = "mouse_side_back",
        ["button5"] = "mouse_side_forward",
    };

    private static (string Folder, Dictionary<string, string> Map) StyleTable(ControllerStyle style) => style switch
    {
        ControllerStyle.PlayStation => ("playstation", PlayStation),
        ControllerStyle.Switch => ("switch", Switch),
        ControllerStyle.SteamDeck => ("steamdeck", SteamDeck),
        ControllerStyle.SteamController => ("steam", SteamController),
        ControllerStyle.Xbox => ("xbox", Xbox),
        _ => ("xbox", Xbox), // Generic pads borrow the Xbox layout
    };

    // pad token -> filename (without folder/extension), built from the extracted pack.
    private static Dictionary<string, string> WithDpad(string prefix, Dictionary<string, string> extra)
    {
        var d = new Dictionary<string, string>
        {
            ["dpad_up"] = $"{prefix}_dpad_up",
            ["dpad_down"] = $"{prefix}_dpad_down",
            ["dpad_left"] = $"{prefix}_dpad_left",
            ["dpad_right"] = $"{prefix}_dpad_right",
        };
        foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    private static readonly Dictionary<string, string> Xbox = WithDpad("xbox", new()
    {
        ["a"] = "xbox_button_a", ["b"] = "xbox_button_b", ["x"] = "xbox_button_x", ["y"] = "xbox_button_y",
        ["lb"] = "xbox_lb", ["rb"] = "xbox_rb", ["lt"] = "xbox_lt", ["rt"] = "xbox_rt",
        ["start"] = "xbox_button_menu", ["back"] = "xbox_button_view", ["guide"] = "xbox_guide",
        ["ls"] = "xbox_stick_l", ["rs"] = "xbox_stick_r",
    });

    private static readonly Dictionary<string, string> PlayStation = WithDpad("playstation", new()
    {
        ["a"] = "playstation_button_cross", ["b"] = "playstation_button_circle",
        ["x"] = "playstation_button_square", ["y"] = "playstation_button_triangle",
        ["lb"] = "playstation_trigger_l1", ["rb"] = "playstation_trigger_r1",
        ["lt"] = "playstation_trigger_l2", ["rt"] = "playstation_trigger_r2",
        ["start"] = "playstation5_button_options", ["back"] = "playstation5_button_create",
        ["ls"] = "playstation_button_l3", ["rs"] = "playstation_button_r3",
    });

    private static readonly Dictionary<string, string> Switch = WithDpad("switch", new()
    {
        ["a"] = "switch_button_a", ["b"] = "switch_button_b", ["x"] = "switch_button_x", ["y"] = "switch_button_y",
        ["lb"] = "switch_button_l", ["rb"] = "switch_button_r", ["lt"] = "switch_button_zl", ["rt"] = "switch_button_zr",
        ["start"] = "switch_button_plus", ["back"] = "switch_button_minus", ["guide"] = "switch_button_home",
        ["ls"] = "switch_stick_l", ["rs"] = "switch_stick_r",
    });

    private static readonly Dictionary<string, string> SteamDeck = WithDpad("steamdeck", new()
    {
        ["a"] = "steamdeck_button_a", ["b"] = "steamdeck_button_b",
        ["x"] = "steamdeck_button_x", ["y"] = "steamdeck_button_y",
        ["lb"] = "steamdeck_button_l1", ["rb"] = "steamdeck_button_r1",
        ["lt"] = "steamdeck_button_l2", ["rt"] = "steamdeck_button_r2",
        ["start"] = "steamdeck_button_options", ["back"] = "steamdeck_button_view", ["guide"] = "steamdeck_button_guide",
        ["ls"] = "steamdeck_stick_l", ["rs"] = "steamdeck_stick_r",
    });

    private static readonly Dictionary<string, string> SteamController = WithDpad("steam", new()
    {
        ["a"] = "steam_button_a", ["b"] = "steam_button_b", ["x"] = "steam_button_x", ["y"] = "steam_button_y",
        ["lb"] = "steam_lb", ["rb"] = "steam_rb", ["lt"] = "steam_lt", ["rt"] = "steam_rt",
        ["start"] = "steam_button_start_icon", ["back"] = "steam_button_back_icon",
        ["ls"] = "steam_button_lp", ["rs"] = "steam_button_rp",
    });
}

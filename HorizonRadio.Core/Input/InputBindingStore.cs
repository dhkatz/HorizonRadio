using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HorizonRadio.Core.Events;

namespace HorizonRadio.Core.Input;

/// <summary>
/// Persists input→action bindings to
/// <c>%LOCALAPPDATA%\HorizonRadio\controls-config.json</c>. Same hand-editable
/// shape and threading contract as <see cref="EventRuleStore"/>: reads are
/// lock-free (the input service matches from backend threads), writes come
/// from the UI thread. Keyed by <see cref="InputBinding.Key"/> so a binding is
/// matched by physical identity, not by its display label.
///
/// File shape:
/// <code>
/// { "bindings": [
///     { "kind": "Keyboard", "code": 57, "label": "Space",
///       "action": { "type": "TogglePause" } } ] }
/// </code>
/// </summary>
public sealed class InputBindingStore
{
    private sealed record Entry(InputBinding Binding, EventAction Action);

    private readonly ConcurrentDictionary<string, Entry> _byKey = new();

    private static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio",
            "controls-config.json");

    private static void Log(string msg) => Debug.WriteLine($"[hzn-controls-cfg] {msg}");

    /// <summary>The action bound to <paramref name="binding"/>'s physical
    /// identity, or <see cref="EventAction.None"/> if unbound.</summary>
    public EventAction Match(InputBinding binding) =>
        _byKey.TryGetValue(binding.Key, out var e) ? e.Action : EventAction.None;

    /// <summary>A binding's "slot" — the UI keeps one binding per action per slot.
    /// Keyboard and mouse share a single slot; each controller device is its own
    /// slot (keyed by device name) so a gamepad and a wheel can both be bound to
    /// the same action.</summary>
    public static string SlotOf(InputBinding b) => SlotOf(InputCategories.Of(b), b.Device);

    /// <summary>The slot key for a (category, device) pair — used by the UI to
    /// look up/clear a slot before it has a captured binding in hand.</summary>
    public static string SlotOf(InputCategory category, string? device) =>
        category == InputCategory.KeyboardMouse ? KeyboardMouseSlot : (device ?? "");

    /// <summary>The slot key for the shared keyboard/mouse column.</summary>
    public const string KeyboardMouseSlot = "kbm";

    /// <summary>The binding mapped to <paramref name="action"/> in
    /// <paramref name="slot"/> (see <see cref="SlotOf"/>), or null.</summary>
    public InputBinding? GetBindingForSlot(EventAction action, string slot) =>
        _byKey.Values.FirstOrDefault(e => e.Action == action && SlotOf(e.Binding) == slot)?.Binding;

    /// <summary>Bind <paramref name="binding"/> to <paramref name="action"/>.
    /// Replaces any prior binding for that action in the SAME slot (keyboard/mouse,
    /// or the same controller device), and drops any prior action on that physical
    /// input so each input maps to one action.</summary>
    public void Bind(InputBinding binding, EventAction action)
    {
        var slot = SlotOf(binding);
        foreach (var stale in _byKey
                     .Where(kv => (kv.Value.Action == action && SlotOf(kv.Value.Binding) == slot)
                                  || kv.Key == binding.Key)
                     .Select(kv => kv.Key).ToList())
            _byKey.TryRemove(stale, out _);
        _byKey[binding.Key] = new Entry(binding, action);
    }

    /// <summary>Remove the binding mapped to <paramref name="action"/> in
    /// <paramref name="slot"/>.</summary>
    public void ClearSlot(EventAction action, string slot)
    {
        foreach (var stale in _byKey
                     .Where(kv => kv.Value.Action == action && SlotOf(kv.Value.Binding) == slot)
                     .Select(kv => kv.Key).ToList())
            _byKey.TryRemove(stale, out _);
    }

    /// <summary>Remove every binding mapped to <paramref name="action"/> (any slot).
    /// Reaps orphaned bindings when the thing they target — e.g. a profile — is
    /// deleted. Returns true if anything was removed.</summary>
    public bool ClearBindingsForAction(EventAction action)
    {
        var stale = _byKey.Where(kv => kv.Value.Action == action).Select(kv => kv.Key).ToList();
        foreach (var key in stale) _byKey.TryRemove(key, out _);
        return stale.Count > 0;
    }

    public static InputBindingStore LoadFromDisk(string? path = null)
    {
        path ??= DefaultPath;
        var store = new InputBindingStore();
        try
        {
            if (!File.Exists(path)) return store;
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("bindings", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return store;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("kind", out var k) ||
                    !Enum.TryParse<InputKind>(k.GetString(), out var kind)) continue;
                if (!item.TryGetProperty("code", out var c) || c.ValueKind != JsonValueKind.Number) continue;

                var device = item.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() : null;
                var label = item.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString() ?? "" : "";
                var glyphId = item.TryGetProperty("glyphId", out var g) && g.ValueKind == JsonValueKind.String
                    ? g.GetString() : null;
                ControllerStyle? style = item.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String
                    && Enum.TryParse<ControllerStyle>(s.GetString(), out var parsedStyle) ? parsedStyle : null;

                if (!item.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.Object) continue;
                var typeStr = a.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (typeStr == null || !Enum.TryParse<EventActionType>(typeStr, out var type)) continue;
                var param = a.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() : null;

                var binding = new InputBinding(kind, device, c.GetInt32(), label, glyphId, style);
                store._byKey[binding.Key] = new Entry(binding, new EventAction(type, param));
            }
        }
        catch (Exception ex) { Log($"load failed: {ex.Message}"); }
        return store;
    }

    public void SaveToDisk(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteStartArray("bindings");
            foreach (var e in _byKey.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", e.Binding.Kind.ToString());
                if (!string.IsNullOrEmpty(e.Binding.Device))
                    writer.WriteString("device", e.Binding.Device);
                writer.WriteNumber("code", e.Binding.Code);
                writer.WriteString("label", e.Binding.Label);
                if (!string.IsNullOrEmpty(e.Binding.GlyphId))
                    writer.WriteString("glyphId", e.Binding.GlyphId);
                if (e.Binding.Style is { } st)
                    writer.WriteString("style", st.ToString());
                writer.WriteStartObject("action");
                writer.WriteString("type", e.Action.Type.ToString());
                if (!string.IsNullOrEmpty(e.Action.Param))
                    writer.WriteString("param", e.Action.Param);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        catch (Exception ex) { Log($"save failed: {ex.Message}"); }
    }
}

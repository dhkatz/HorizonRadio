using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Input;

/// <summary>
/// The input→action bindings (the Controls tab), kept in the <see cref="SettingsStore"/> as one
/// synced list, <see cref="Bindings"/> (one setting rather than one per binding, since a binding
/// replaces others: one action per slot, one action per input). Reads are lock-free (the input
/// service matches from backend threads); writes come from the UI thread and are saved at once.
/// Keyed by <see cref="InputBinding.Key"/> so a binding is matched by physical identity, not by its
/// display label.
/// </summary>
public sealed class InputBindingStore
{
    /// <summary>Every binding: <c>{ "kind": "Keyboard", "code": 57, "label": "Space",
    /// "action": { "type": "TogglePause" } }</c>.</summary>
    internal static readonly Setting<StoredBinding[]> Bindings = new("controls.bindings", SettingScope.Synced, []);

    private sealed record Entry(InputBinding Binding, EventAction Action);

    private readonly SettingsStore _settings;
    private readonly Lock _writeLock = new();
    private volatile IReadOnlyDictionary<string, Entry> _byKey;

    /// <summary>A store kept in memory only (tests, the designer).</summary>
    public InputBindingStore() : this(SettingsStore.CreateInMemory())
    {
    }

    public InputBindingStore(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _byKey = Read();
        // A change made elsewhere (another device, later) replaces what's matched against.
        settings.Changed += key =>
        {
            if (key != Bindings.Key) return;
            _byKey = Read();
            Changed?.Invoke();
        };
    }

    /// <summary>The bindings changed, here or on another of your devices.</summary>
    public event Action? Changed;

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
        Update(all =>
        {
            foreach (var stale in all
                         .Where(kv => (kv.Value.Action == action && SlotOf(kv.Value.Binding) == slot)
                                      || kv.Key == binding.Key)
                         .Select(kv => kv.Key).ToList())
                all.Remove(stale);
            all[binding.Key] = new Entry(binding, action);
        });
    }

    /// <summary>Remove the binding mapped to <paramref name="action"/> in
    /// <paramref name="slot"/>.</summary>
    public void ClearSlot(EventAction action, string slot) => Update(all =>
    {
        foreach (var stale in all
                     .Where(kv => kv.Value.Action == action && SlotOf(kv.Value.Binding) == slot)
                     .Select(kv => kv.Key).ToList())
            all.Remove(stale);
    });

    /// <summary>Remove every binding mapped to <paramref name="action"/> (any slot).
    /// Reaps orphaned bindings when the thing they target — e.g. a profile — is
    /// deleted. Returns true if anything was removed.</summary>
    public bool ClearBindingsForAction(EventAction action)
    {
        var removed = false;
        Update(all =>
        {
            foreach (var key in all.Where(kv => kv.Value.Action == action).Select(kv => kv.Key).ToList())
                removed |= all.Remove(key);
        });
        return removed;
    }

    // Apply a change to a copy, publish it for the readers, and store it.
    private void Update(Action<Dictionary<string, Entry>> change)
    {
        lock (_writeLock)
        {
            var all = new Dictionary<string, Entry>(_byKey, StringComparer.Ordinal);
            change(all);
            _byKey = all;
            _settings.Set(Bindings, [.. all.Values.Select(e => StoredBinding.From(e.Binding, e.Action))]);
        }
    }

    private Dictionary<string, Entry> Read()
    {
        var all = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var stored in _settings.Get(Bindings))
        {
            if (stored.Action is null) continue;
            var binding = stored.ToBinding();
            all[binding.Key] = new Entry(binding, stored.Action);
        }
        return all;
    }
}

/// <summary>An <see cref="InputBinding"/> and its action as <see cref="InputBindingStore.Bindings"/>
/// stores them.</summary>
internal sealed record StoredBinding(
    InputKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Device,
    int Code,
    string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GlyphId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ControllerStyle? Style,
    EventAction Action)
{
    public static StoredBinding From(InputBinding b, EventAction action) =>
        new(b.Kind, string.IsNullOrEmpty(b.Device) ? null : b.Device, b.Code, b.Label ?? "",
            string.IsNullOrEmpty(b.GlyphId) ? null : b.GlyphId, b.Style, action);

    public InputBinding ToBinding() => new(Kind, Device, Code, Label ?? "", GlyphId, Style);
}

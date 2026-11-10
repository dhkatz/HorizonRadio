using System.Globalization;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Events;

/// <summary>
/// The event→action bindings (the Events tab), kept in the <see cref="SettingsStore"/> as one
/// synced setting per event kind: <c>event.&lt;kind&gt;</c> =
/// <c>{ "type": "SetVolume", "param": "0.3" }</c>. An unbound event has no setting. Safe to read
/// from the event sources' threads while the UI writes.
/// </summary>
public sealed class EventRuleStore
{
    private const string Prefix = "event.";

    private readonly SettingsStore _settings;

    /// <summary>A store kept in memory only (tests, the designer).</summary>
    public EventRuleStore() : this(SettingsStore.CreateInMemory())
    {
    }

    public EventRuleStore(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        settings.Changed += key =>
        {
            if (key.StartsWith(Prefix, StringComparison.Ordinal)) Changed?.Invoke(key[Prefix.Length..]);
        };
    }

    /// <summary>The action for an event kind changed, here or on another of your devices.</summary>
    public event Action<string>? Changed;

    /// <summary>The setting the action for <paramref name="kind"/> is kept under.</summary>
    public static Setting<EventAction?> RuleFor(string kind) => new(Prefix + kind, SettingScope.Synced, null);

    /// <summary>The action bound to <paramref name="kind"/>, or
    /// <see cref="EventAction.None"/> if unbound.</summary>
    public EventAction GetAction(string kind) => _settings.Get(RuleFor(kind)) ?? EventAction.None;

    public void SetAction(string kind, EventAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Type == EventActionType.None) _settings.Reset(RuleFor(kind));
        else _settings.Set(RuleFor(kind), action);
    }

    // -- helpers for the SetVolume param (invariant round-trip) --

    public static string FormatVolume(double level) =>
        level.ToString("0.###", CultureInfo.InvariantCulture);

    public static double ParseVolume(string? param, double fallback = 1.0) =>
        double.TryParse(param, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
}

using HorizonRadio.Core.Events;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Tests;

/// <summary>Event rules and input bindings in the settings store, and bringing them over from
/// <c>events-config.json</c> / <c>controls-config.json</c>.</summary>
public sealed class EventAndControlSettingsTests : IDisposable
{
    private static readonly InputBinding Space = new(InputKind.Keyboard, null, 57, "Space", "key.space");
    private static readonly InputBinding PadA = new(InputKind.GamepadButton, "Xbox Pad", 0, "A", "pad.a", ControllerStyle.Xbox);

    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    private string PathOf(string name) => Path.Combine(_dir.Path, name);

    private SettingsStore Open() => SettingsStore.Open(PathOf(SettingsStore.FileName));

    [Fact]
    public void An_event_rule_is_a_synced_setting_and_unbinding_removes_it()
    {
        var settings = SettingsStore.CreateInMemory();
        var rules = new EventRuleStore(settings);

        rules.SetAction("race_start", new EventAction(EventActionType.SetVolume, "0.3"));

        Assert.Equal(new EventAction(EventActionType.SetVolume, "0.3"), new EventRuleStore(settings).GetAction("race_start"));
        Assert.Equal(SettingScope.Synced, settings.ScopeOf("event.race_start"));
        Assert.Equal(EventAction.None, rules.GetAction("race_finish"));

        rules.SetAction("race_start", EventAction.None);
        Assert.False(settings.IsSet("event.race_start"));
    }

    [Fact]
    public void Bindings_are_saved_as_they_change()
    {
        var settings = SettingsStore.CreateInMemory();
        var store = new InputBindingStore(settings);

        store.Bind(Space, new EventAction(EventActionType.TogglePause));
        store.Bind(PadA, new EventAction(EventActionType.NextTrack));

        var again = new InputBindingStore(settings);
        Assert.Equal(new EventAction(EventActionType.TogglePause), again.Match(Space));
        Assert.Equal(PadA, again.GetBindingForSlot(new EventAction(EventActionType.NextTrack), "Xbox Pad"));

        store.ClearSlot(new EventAction(EventActionType.TogglePause), InputBindingStore.KeyboardMouseSlot);
        Assert.Equal(EventAction.None, new InputBindingStore(settings).Match(Space));
    }

    [Fact]
    public void Bindings_changed_in_the_settings_are_matched_at_once()
    {
        var settings = SettingsStore.CreateInMemory();
        var store = new InputBindingStore(settings);

        // Another instance over the same settings stands in for a change from elsewhere (sync).
        new InputBindingStore(settings).Bind(Space, new EventAction(EventActionType.Pause));

        Assert.Equal(new EventAction(EventActionType.Pause), store.Match(Space));
    }

    [Fact]
    public void Event_rules_and_bindings_are_brought_over_from_the_older_files()
    {
        File.WriteAllText(PathOf("events-config.json"), """
            { "rules": {
                "race_start": { "type": "SwitchProfile", "param": "drive" },
                "paused": { "type": "SetVolume", "param": "0.3" },
                "bogus": { "type": "Explode" } } }
            """);
        File.WriteAllText(PathOf("controls-config.json"), """
            { "bindings": [
                { "kind": "Keyboard", "code": 57, "label": "Space", "glyphId": "key.space",
                  "action": { "type": "TogglePause" } },
                { "kind": "GamepadButton", "device": "Xbox Pad", "code": 0, "label": "A", "glyphId": "pad.a",
                  "style": "Xbox", "action": { "type": "NextTrack" } },
                { "kind": "Telepathy", "code": 1, "label": "?", "action": { "type": "Pause" } },
                { "kind": "Keyboard", "label": "no code", "action": { "type": "Pause" } } ] }
            """);

        using var settings = Open();

        var rules = new EventRuleStore(settings);
        Assert.Equal(new EventAction(EventActionType.SwitchMix, "drive"), rules.GetAction("race_start"));
        Assert.Equal(new EventAction(EventActionType.SetVolume, "0.3"), rules.GetAction("paused"));
        Assert.Equal(EventAction.None, rules.GetAction("bogus"));

        var controls = new InputBindingStore(settings);
        Assert.Equal(new EventAction(EventActionType.TogglePause), controls.Match(Space));
        Assert.Equal(PadA, controls.GetBindingForSlot(new EventAction(EventActionType.NextTrack), "Xbox Pad"));
        Assert.Equal(2, settings.Get(InputBindingStore.Bindings).Length);
    }

    [Fact]
    public void A_settings_file_from_before_these_files_moved_still_gets_them_once()
    {
        // settings.json as the first version of the store wrote it: sources.json already brought
        // over, and no record of which files were.
        File.WriteAllText(PathOf(SettingsStore.FileName), """
            { "version": 1, "settings": {
                "playback.shuffle": { "value": true, "scope": "synced", "modified": "2026-11-01T00:00:00.000Z" } } }
            """);
        File.WriteAllText(PathOf("sources.json"), """{ "shuffle": false }""");
        File.WriteAllText(PathOf("events-config.json"), """{ "rules": { "paused": { "type": "Pause" } } }""");

        using (var settings = Open())
        {
            Assert.True(settings.Get(PlaybackSettings.Shuffle)); // not imported again
            Assert.Equal(new EventAction(EventActionType.Pause), new EventRuleStore(settings).GetAction("paused"));
        }

        // Once: a later edit to the old file stays there.
        File.WriteAllText(PathOf("events-config.json"), """{ "rules": { "paused": { "type": "Resume" } } }""");
        using var reopened = Open();
        Assert.Equal(new EventAction(EventActionType.Pause), new EventRuleStore(reopened).GetAction("paused"));
    }
}

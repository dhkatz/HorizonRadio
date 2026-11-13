using System.Text.Json;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Sdk.Sources.Config;
using Microsoft.Extensions.Time.Testing;

namespace HorizonRadio.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private static readonly Setting<bool> Flag = new("test.flag", SettingScope.Synced, true);
    private static readonly Setting<double> Level = new("test.level", SettingScope.Device, 0.5);
    private static readonly Setting<string?> Name = new("test.name", SettingScope.Synced, null);
    private static readonly Setting<string[]> List = new("test.list", SettingScope.Synced, []);
    private static readonly Setting<string?> Token = new("test.token", SettingScope.Secret, null);

    private readonly TempDir _dir = TempDir.Create();

    private string PathOf(string name) => Path.Combine(_dir.Path, name);

    private string SettingsPath => PathOf(SettingsStore.FileName);

    // A store that only saves when told to, so a test sees exactly when the file is written.
    private SettingsStore OpenManual(TimeProvider? time = null) =>
        SettingsStore.Open(SettingsPath, TimeSpan.FromHours(1), time ?? TimeProvider.System, TestSecrets.ThisOs);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void An_unset_setting_reads_as_its_default()
    {
        using var store = OpenManual();
        Assert.True(store.Get(Flag));
        Assert.Equal(0.5, store.Get(Level));
        Assert.Null(store.Get(Name));
        Assert.Empty(store.Get(List));
        Assert.False(store.IsSet(Flag.Key));
        Assert.False(File.Exists(SettingsPath)); // nothing to save yet
    }

    [Fact]
    public void Values_survive_a_restart()
    {
        using (var store = OpenManual())
        {
            store.Set(Flag, false);
            store.Set(Level, double.NaN);
            store.Set(Name, "Ada");
            store.Set(List, ["spotify", "youtube"]);
        }

        using var reopened = OpenManual();
        Assert.False(reopened.Get(Flag));
        Assert.True(double.IsNaN(reopened.Get(Level)));
        Assert.Equal("Ada", reopened.Get(Name));
        Assert.Equal(["spotify", "youtube"], reopened.Get(List));
    }

    [Fact]
    public void Each_value_records_its_scope_and_when_it_changed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        using (var store = OpenManual(time))
        {
            store.Set(Flag, false);
            time.Advance(TimeSpan.FromMinutes(5));
            store.Set(Level, 0.25);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var settings = doc.RootElement.GetProperty("settings");
        Assert.Equal("synced", settings.GetProperty("test.flag").GetProperty("scope").GetString());
        Assert.Equal("2026-11-10T09:00:00.000Z", settings.GetProperty("test.flag").GetProperty("modified").GetString());
        Assert.Equal("device", settings.GetProperty("test.level").GetProperty("scope").GetString());
        Assert.Equal("2026-11-10T09:05:00.000Z", settings.GetProperty("test.level").GetProperty("modified").GetString());

        using var reopened = OpenManual();
        Assert.Equal(SettingScope.Device, reopened.ScopeOf(Level.Key));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 5, 0, TimeSpan.Zero), reopened.ModifiedAt(Level.Key));
    }

    [Fact]
    public void A_change_raises_Changed_once_and_setting_the_same_value_raises_nothing()
    {
        using var store = SettingsStore.CreateInMemory();
        var changed = new List<string>();
        store.Changed += changed.Add;

        store.Set(Level, 0.8);
        store.Set(Level, 0.8);
        store.Set(List, ["a"]);
        store.Set(List, ["a"]);
        store.Reset(Level);
        store.Reset(Level); // already unset

        Assert.Equal([Level.Key, List.Key, Level.Key], changed);
    }

    [Fact]
    public void Watch_hands_over_the_new_value_until_disposed()
    {
        using var store = SettingsStore.CreateInMemory();
        var seen = new List<double>();
        var watch = store.Watch(Level, seen.Add);

        store.Set(Level, 0.1);
        store.Set(Flag, false); // another setting
        store.Reset(Level);
        watch.Dispose();
        store.Set(Level, 0.9);

        Assert.Equal([0.1, 0.5], seen); // reset reads as the default
    }

    [Fact]
    public void Changes_are_saved_shortly_after_without_a_flush()
    {
        using var store = SettingsStore.Open(SettingsPath, TimeSpan.FromMilliseconds(20), TimeProvider.System);
        store.Set(Name, "soon");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(SettingsPath) || !File.ReadAllText(SettingsPath).Contains("soon", StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, "the change was never saved");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void A_change_after_dispose_is_still_saved()
    {
        var store = OpenManual();
        store.Dispose();
        store.Set(Name, "late");

        using var reopened = OpenManual();
        Assert.Equal("late", reopened.Get(Name));
    }

    [Fact]
    public void A_value_of_the_wrong_type_reads_as_the_default()
    {
        File.WriteAllText(SettingsPath, """
            { "version": 1, "settings": { "test.level": { "value": "loud", "scope": "device" } } }
            """);

        using var store = OpenManual();
        Assert.Equal(0.5, store.Get(Level));
    }

    [Fact]
    public void An_unreadable_file_is_set_aside_and_the_app_starts_from_defaults()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        using (var store = OpenManual())
        {
            Assert.True(store.Get(Flag));
            store.Set(Flag, false);
        }

        Assert.Equal("{ not json", File.ReadAllText(SettingsPath + ".bad"));
        using var reopened = OpenManual();
        Assert.False(reopened.Get(Flag));
    }

    [Fact]
    public void A_secret_is_encrypted_on_disk()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var store = OpenManual()) store.Set(Token, "hunter2");

        var onDisk = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("hunter2", onDisk, StringComparison.Ordinal);
        Assert.Contains("dpapi:", onDisk, StringComparison.Ordinal);
        using var reopened = OpenManual();
        Assert.Equal("hunter2", reopened.Get(Token));
    }

    [Fact]
    public void Only_text_can_be_a_secret()
    {
        Assert.Throws<ArgumentException>(() => new Setting<int>("test.pin", SettingScope.Secret, 0));
        using var store = SettingsStore.CreateInMemory();
        Assert.Throws<ArgumentException>(() =>
            store.SetRaw("test.pin", JsonSerializer.SerializeToElement(1234), SettingScope.Secret));
    }

    [Fact]
    public void Keys_are_listed_by_prefix()
    {
        using var store = SettingsStore.CreateInMemory();
        store.SetRaw("plugin.fh6.b", JsonSerializer.SerializeToElement("2"), SettingScope.Device);
        store.SetRaw("plugin.fh6.a", JsonSerializer.SerializeToElement("1"), SettingScope.Device);
        store.SetRaw("plugin.fh60.a", JsonSerializer.SerializeToElement("x"), SettingScope.Device);

        Assert.Equal(["plugin.fh6.a", "plugin.fh6.b"], store.KeysWithPrefix("plugin.fh6."));
    }

    [Fact]
    public void Changes_from_many_threads_are_all_kept()
    {
        using (var store = OpenManual())
        {
            Parallel.For(0, 200, i => store.SetRaw($"test.n{i}", JsonSerializer.SerializeToElement(i), SettingScope.Device));
        }

        using var reopened = OpenManual();
        Assert.Equal(200, reopened.KeysWithPrefix("test.n").Count);
    }

    // The files settings were kept in before settings.json, as a user upgrading has them.
    private void WriteLegacyFiles(string? protectedKey = null)
    {
        File.WriteAllText(PathOf("sources.json"), $$"""
            {
              "lastSelected": "local",
              "game": "fh6",
              "targetStation": "Horizon XS",
              "shuffle": true,
              "previewEnabled": true,
              "previewDeviceId": "{0.0.0.00000000}.{speakers}",
              "previewVolume": 0.6,
              "lyricsKaraoke": false,
              "lyricsVersion": "Romaji",
              "lyricsRadioOffset": 1.5,
              "lyricsAudioSync": false,
              "searchSourcePriority": ["youtube", "spotify-driven"],
              "perSource": {
                "local": { "path": "C:/Music", "recursive": true, "gain": 3 },
                "spotify-driven": { "deviceName": "Horizon Radio"{{(protectedKey == null ? "" : $", \"soloistKey\": \"{protectedKey}\"")}} }
              }
            }
            """);
        File.WriteAllText(PathOf("plugin-settings.json"), """
            { "plugins": { "fh6": { "station:Horizon Pulse": "mix:drive", "switchMode": "Live" } } }
            """);
        Directory.CreateDirectory(PathOf("diagnostics"));
        File.WriteAllText(Path.Combine(PathOf("diagnostics"), "settings.json"), """{ "enabled": true }""");
    }

    [Fact]
    public void The_first_run_brings_over_the_older_settings_files()
    {
        WriteLegacyFiles();
        var sourcesBefore = File.ReadAllText(PathOf("sources.json"));

        using (var store = OpenManual())
        {
            Assert.Equal("local", store.Get(SourceSettings.LastSelected));
            Assert.Equal("fh6", store.Get(GameSelection.Chosen));
            Assert.Equal("Horizon XS", store.Get(LegacySettingsMigration.TargetStation));
            Assert.True(store.Get(PlaybackSettings.Shuffle));
            Assert.True(store.Get(PreviewSettings.Enabled));
            Assert.Equal("{0.0.0.00000000}.{speakers}", store.Get(PreviewSettings.Device));
            Assert.Equal(0.6, store.Get(PreviewSettings.Volume));
            Assert.False(store.Get(LyricsSettings.Karaoke));
            Assert.Equal("Romaji", store.Get(LyricsSettings.Version));
            Assert.Equal(1.5, store.Get(LyricsSettings.RadioOffset));
            Assert.False(store.Get(LyricsSettings.AudioSync));
            Assert.Equal(["youtube", "spotify-driven"], store.Get(SourceSettings.SearchPriority));
            Assert.True(store.Get(MetadataTrace.EnabledSetting));

            var local = new SourceConfigStore(store).Load("local", []);
            Assert.Equal("C:/Music", local.GetString("path"));
            Assert.True(local.GetBool("recursive"));
            Assert.Equal(3, local.GetDouble("gain"));

            var fh6 = PluginSettings.For(store, "fh6");
            Assert.Equal("mix:drive", fh6.GetValue("station:Horizon Pulse"));
            Assert.Equal("Live", fh6.GetValue("switchMode"));
        }

        // Saved in the new file at once; the old files are left for an older build.
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(sourcesBefore, File.ReadAllText(PathOf("sources.json")));

        // Only once: later edits to the old file don't come back over the new one.
        File.WriteAllText(PathOf("sources.json"), """{ "shuffle": false }""");
        using var reopened = OpenManual();
        Assert.True(reopened.Get(PlaybackSettings.Shuffle));
    }

    [Fact]
    public void Imported_settings_keep_their_scopes_and_secrets_stay_encrypted()
    {
        if (!OperatingSystem.IsWindows()) return;
        WriteLegacyFiles(protectedKey: new SecretText(TestSecrets.ThisOs).Protect("my-soloist-key"));

        using (var store = OpenManual())
        {
            Assert.Equal(SettingScope.Synced, store.ScopeOf(LyricsSettings.Karaoke.Key));
            Assert.Equal(SettingScope.Device, store.ScopeOf(GameSelection.Chosen.Key));
            Assert.Equal(SettingScope.Device, store.ScopeOf(SourceConfigStore.KeyFor("local", "path")));
            Assert.Equal(SettingScope.Device, store.ScopeOf(PluginSettings.KeyFor("fh6", "switchMode")));
            Assert.Equal(SettingScope.Secret, store.ScopeOf(SourceConfigStore.KeyFor("spotify-driven", "soloistKey")));

            var schema = new ConfigField[] { new TextField("soloistKey", "Key", IsSecret: true) };
            Assert.Equal("my-soloist-key", new SourceConfigStore(store).Load("spotify-driven", schema).GetString("soloistKey"));
        }

        Assert.DoesNotContain("my-soloist-key", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_older_files_nothing_is_written()
    {
        using var store = OpenManual();
        Assert.False(File.Exists(SettingsPath));
    }
}

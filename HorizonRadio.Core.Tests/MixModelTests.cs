using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The mix data layer: per-game mix settings, JSON persistence of the
/// cross-source entry list, and resolving a ref to the engine that plays it.
/// </summary>
public class MixModelTests
{
    [Fact]
    public void Game_settings_are_kept_per_game()
    {
        var mix = new Mix("1", "A", [])
            .WithSettingsFor("fh6", new Dictionary<string, string> { ["station"] = "Horizon Wave" })
            .WithSettingsFor("other", new Dictionary<string, string> { ["x"] = "y" });

        Assert.Equal("Horizon Wave", mix.SettingsFor("fh6")!["station"]);
        Assert.Equal("y", mix.SettingsFor("other")!["x"]);
        Assert.Null(mix.SettingsFor("missing"));

        // Clearing one game's settings leaves the others; clearing the last drops the bag.
        var cleared = mix.WithSettingsFor("fh6", null);
        Assert.Null(cleared.SettingsFor("fh6"));
        Assert.Equal("y", cleared.SettingsFor("other")!["x"]);
        Assert.Null(cleared.WithSettingsFor("other", new Dictionary<string, string>()).Games);
    }

    [Fact]
    public void MixStore_round_trips_entries_and_game_settings()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "mixes.json");

        var save = new MixStore();
        save.AddOrUpdate(new Mix("drive", "Drive",
        [
            new ContentRef("youtube", "https://youtu.be/abc", "Synthwave"),
            new ContentRef("local", @"C:\Music", null),
        ], Games: new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["fh6"] = new Dictionary<string, string> { ["station"] = "Horizon Pulse" },
        }));
        save.AddOrUpdate(new Mix("chill", "Chill", [new ContentRef("local", @"C:\Calm")]));
        save.SaveToDisk(path);

        var load = MixStore.LoadFromDisk(path);

        Assert.Equal(2, load.All.Count);
        var drive = load.Get("drive")!;
        Assert.Equal("Drive", drive.Name);
        Assert.Equal("Horizon Pulse", drive.SettingsFor("fh6")!["station"]);
        Assert.Equal(2, drive.Entries.Count);
        Assert.Equal("youtube", drive.Entries[0].SourceId);
        Assert.Equal("https://youtu.be/abc", drive.Entries[0].Locator);
        Assert.Equal("Synthwave", drive.Entries[0].DisplayName);
        Assert.Null(load.Get("chill")!.Games);
    }

    // Before games were plugins, a mix stored its (FH6-only) station override at the top level.
    [Fact]
    public void MixStore_migrates_legacy_station_to_fh6_settings()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "mixes.json");
        File.WriteAllText(path, """
            { "mixes": [
              { "id": "a", "name": "A", "station": "Horizon XS", "entries": [] },
              { "id": "b", "name": "B", "station": null, "entries": [] }
            ] }
            """);

        var load = MixStore.LoadFromDisk(path);

        Assert.Equal("Horizon XS", load.Get("a")!.SettingsFor("fh6")!["station"]);
        Assert.Null(load.Get("b")!.Games);

        // Saved back in the new shape, with no top-level "station".
        load.SaveToDisk(path);
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
            Assert.False(doc.RootElement.GetProperty("mixes")[0].TryGetProperty("station", out _));
        Assert.Equal("Horizon XS", MixStore.LoadFromDisk(path).Get("a")!.SettingsFor("fh6")!["station"]);
    }

    [Fact]
    public void MixStore_addOrUpdate_replaces_and_notifies()
    {
        var store = new MixStore();
        var fired = 0;
        store.Changed += () => fired++;

        store.AddOrUpdate(new Mix("x", "First", []));
        store.AddOrUpdate(new Mix("x", "Renamed", []));

        Assert.Single(store.All);
        Assert.Equal("Renamed", store.Get("x")!.Name);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Resolver_returns_player_for_content_source()
    {
        var resolver = new MixContentResolver(new SourceConfigStore());
        var player = resolver.ResolvePlayer(new ContentRef("local", @"C:\Music"));
        Assert.IsType<LocalContentPlayer>(player);
    }

    [Fact]
    public void Resolver_rejects_self_driven_and_unknown_sources()
    {
        var resolver = new MixContentResolver(new SourceConfigStore());
        // Spotify is self-driven (not IContentSourceFactory); "nope" is unknown.
        Assert.Throws<InvalidOperationException>(
            () => resolver.ResolvePlayer(new ContentRef("spotify", "whatever")));
        Assert.Throws<InvalidOperationException>(
            () => resolver.ResolvePlayer(new ContentRef("nope", "whatever")));
    }

    [Fact]
    public void Migration_converts_content_profiles_and_drops_self_driven()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "profiles.json");
        File.WriteAllText(path, """
        {
          "profiles": [
            { "id": "a", "name": "Tube",   "source": "youtube", "content": { "url": "https://youtu.be/x" } },
            { "id": "b", "name": "Spot",   "source": "spotify", "content": {} },
            { "id": "c", "name": "Folder", "source": "local",   "content": { "path": "C:\\Music" } }
          ]
        }
        """);

        var mixes = MixMigration.FromLegacyProfiles(path);

        Assert.Equal(2, mixes.Count); // self-driven Spotify profile dropped
        Assert.DoesNotContain(mixes, m => m.Id == "b");

        var tube = mixes.First(m => m.Id == "a");
        Assert.Single(tube.Entries);
        Assert.Equal("youtube", tube.Entries[0].SourceId);
        Assert.Equal("https://youtu.be/x", tube.Entries[0].Locator);

        var folder = mixes.First(m => m.Id == "c");
        Assert.Equal("local", folder.Entries[0].SourceId);
        Assert.Equal(@"C:\Music", folder.Entries[0].Locator);
    }
}

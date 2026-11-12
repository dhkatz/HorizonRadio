using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Source.Local;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The mix data layer: JSON persistence of the cross-source entry list (and reading the
/// legacy per-mix station field), and resolving a ref to the engine that plays it.
/// </summary>
public class MixModelTests
{
    [Fact]
    public void MixStore_round_trips_entries()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "mixes.json");

        var save = new MixStore();
        save.AddOrUpdate(new Mix("drive", "Drive",
        [
            new ContentRef("youtube", "https://youtu.be/abc", "Synthwave"),
            new ContentRef("local", @"C:\Music", null),
        ]));
        save.AddOrUpdate(new Mix("chill", "Chill", [new ContentRef("local", @"C:\Calm")]));
        save.SaveToDisk(path);

        var load = MixStore.LoadFromDisk(path);

        Assert.Equal(2, load.All.Count);
        var drive = load.Get("drive")!;
        Assert.Equal("Drive", drive.Name);
        Assert.Equal(2, drive.Entries.Count);
        Assert.Equal("youtube", drive.Entries[0].SourceId);
        Assert.Equal("https://youtu.be/abc", drive.Entries[0].Locator);
        Assert.Equal("Synthwave", drive.Entries[0].DisplayName);
        Assert.Empty(load.LegacyStations);
    }

    // Before games were plugins, a mix stored its (FH6-only) station override. The store only reads
    // it, for the migration into FH6's station assignments, and never writes it back.
    [Fact]
    public void MixStore_reads_but_drops_legacy_station_overrides()
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
        Assert.Equal(new Dictionary<string, string> { ["a"] = "Horizon XS" }, load.LegacyStations);

        load.SaveToDisk(path);
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
            Assert.False(doc.RootElement.GetProperty("mixes")[0].TryGetProperty("station", out _));
        Assert.Empty(MixStore.LoadFromDisk(path).LegacyStations);
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

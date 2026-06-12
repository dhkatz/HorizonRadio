using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The mix data layer: the per-mix station override, JSON persistence of the
/// cross-source entry list, and resolving a ref to the engine that plays it.
/// </summary>
public class MixModelTests
{
    [Fact]
    public void EffectiveStation_prefers_override_then_global()
    {
        var withOverride = new Mix("1", "A", [], Station: "Horizon Wave");
        var noOverride = new Mix("2", "B", [], Station: null);

        Assert.Equal("Horizon Wave", withOverride.EffectiveStation("Horizon XS"));
        Assert.Equal("Horizon XS", noOverride.EffectiveStation("Horizon XS"));
        Assert.Null(noOverride.EffectiveStation(null));
    }

    [Fact]
    public void MixStore_round_trips_entries_and_station()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "mixes.json");

        var save = new MixStore();
        save.AddOrUpdate(new Mix("drive", "Drive",
        [
            new ContentRef("youtube", "https://youtu.be/abc", "Synthwave"),
            new ContentRef("local", @"C:\Music", null),
        ], Station: "Horizon Pulse"));
        save.AddOrUpdate(new Mix("chill", "Chill", [new ContentRef("local", @"C:\Calm")]));
        save.SaveToDisk(path);

        var load = MixStore.LoadFromDisk(path);

        Assert.Equal(2, load.All.Count);
        var drive = load.Get("drive")!;
        Assert.Equal("Drive", drive.Name);
        Assert.Equal("Horizon Pulse", drive.Station);
        Assert.Equal(2, drive.Entries.Count);
        Assert.Equal("youtube", drive.Entries[0].SourceId);
        Assert.Equal("https://youtu.be/abc", drive.Entries[0].Locator);
        Assert.Equal("Synthwave", drive.Entries[0].DisplayName);
        Assert.Null(load.Get("chill")!.Station);
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
}

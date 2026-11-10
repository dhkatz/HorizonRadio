using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Mixes;
using Microsoft.Extensions.Time.Testing;

namespace HorizonRadio.Core.Tests;

/// <summary>Mixes going between two of your devices: the later change of each mix wins, a deletion
/// included, and what's kept survives a restart.</summary>
public sealed class MixSyncTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    private MixStore Device() => new() { Clock = _clock };

    private void Later() => _clock.Advance(TimeSpan.FromSeconds(1));

    private static Mix Mix(string id, string name) => new(id, name, [new ContentRef("local", "C:/Music/" + name)]);

    [Fact]
    public void Each_device_gets_the_others_mixes()
    {
        var desktop = Device();
        var laptop = Device();
        desktop.AddOrUpdate(Mix("a", "Drive"));
        laptop.AddOrUpdate(Mix("b", "Chill"));

        Assert.True(desktop.MergeSynced(laptop.SyncedMixes()));
        Assert.True(laptop.MergeSynced(desktop.SyncedMixes()));

        Assert.Equal(["Chill", "Drive"], desktop.All.Select(m => m.Name).Order());
        Assert.Equal(["Chill", "Drive"], laptop.All.Select(m => m.Name).Order());
        Assert.False(desktop.MergeSynced(laptop.SyncedMixes())); // nothing new
    }

    [Fact]
    public void The_later_edit_wins_and_a_later_deletion_removes_it()
    {
        var desktop = Device();
        var laptop = Device();
        desktop.AddOrUpdate(Mix("a", "Drive"));
        laptop.MergeSynced(desktop.SyncedMixes());

        Later();
        laptop.AddOrUpdate(Mix("a", "Night Drive"));
        Assert.False(laptop.MergeSynced(desktop.SyncedMixes())); // the older one doesn't come back
        desktop.MergeSynced(laptop.SyncedMixes());
        Assert.Equal("Night Drive", desktop.Get("a")!.Name);

        Later();
        desktop.Remove("a");
        laptop.MergeSynced(desktop.SyncedMixes());
        Assert.Null(laptop.Get("a"));

        // Edited again later somewhere: that beats the deletion.
        Later();
        desktop.AddOrUpdate(Mix("a", "Back Again"));
        laptop.MergeSynced(desktop.SyncedMixes());
        Assert.Equal("Back Again", laptop.Get("a")!.Name);
    }

    [Fact]
    public void Change_times_and_deletions_survive_a_restart()
    {
        var path = Path.Combine(_dir.Path, "mixes.json");
        var store = Device();
        store.AddOrUpdate(Mix("a", "Drive"));
        store.AddOrUpdate(Mix("b", "Chill"));
        Later();
        store.Remove("b");
        store.SaveToDisk(path);

        var reloaded = MixStore.LoadFromDisk(path);
        Assert.Equal(store.ModifiedAt("a"), reloaded.ModifiedAt("a"));
        var gone = Assert.Single(reloaded.SyncedMixes(), m => m.Id == "b");
        Assert.Null(gone.Mix);
        Assert.Equal(store.ModifiedAt("b"), gone.Modified);
    }

    [Fact]
    public void A_mix_from_before_change_times_were_kept_loses_to_any_edit()
    {
        var path = Path.Combine(_dir.Path, "mixes.json");
        File.WriteAllText(path, """{ "mixes": [ { "id": "a", "name": "Old", "entries": [] } ] }""");
        var old = MixStore.LoadFromDisk(path);
        Assert.Equal(DateTimeOffset.UnixEpoch, old.ModifiedAt("a"));

        var other = Device();
        other.AddOrUpdate(Mix("a", "New"));
        Assert.True(old.MergeSynced(other.SyncedMixes()));
        Assert.Equal("New", old.Get("a")!.Name);
    }
}

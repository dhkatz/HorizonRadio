using HorizonRadio.Core.Devices;
using HorizonRadio.Core.History;

namespace HorizonRadio.Core.Tests;

/// <summary>Play history going between two of your devices: songs played anywhere show everywhere,
/// in the order played, and a removal or a clearing goes everywhere.</summary>
public sealed class HistorySyncTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    private static PlayHistoryEntry Song(string id, int minutes, string title = "Song") => new()
    {
        Id = id,
        PlayedAt = Noon.AddMinutes(minutes),
        Title = title + " " + id,
        Artist = "Artist",
        SourceId = "radio",
        SourceDisplay = "Internet Radio",
    };

    private static PlayHistoryStore With(params PlayHistoryEntry[] songs)
    {
        var store = new PlayHistoryStore();
        foreach (var s in songs.OrderBy(s => s.PlayedAt)) store.Add(s);
        return store;
    }

    // Through the same JSON the devices send each other.
    private static async Task Send(PlayHistoryStore from, PlayHistoryStore to, bool full = true)
    {
        var sender = new HistorySync(from);
        var receiver = new HistorySync(to);
        foreach (var part in await sender.ExportAsync(full)) await receiver.MergeAsync(part);
    }

    [Fact]
    public async Task Songs_from_both_devices_end_up_on_both_in_the_order_played()
    {
        var desktop = With(Song("a", 1), Song("c", 3));
        var laptop = With(Song("b", 2), Song("d", 4));

        await Send(laptop, desktop);
        await Send(desktop, laptop);

        Assert.Equal(["d", "c", "b", "a"], desktop.All.Select(e => e.Id));
        Assert.Equal(["d", "c", "b", "a"], laptop.All.Select(e => e.Id));
        Assert.False(desktop.MergeSynced(laptop.SyncedHistory())); // nothing new
    }

    [Fact]
    public async Task A_removed_song_goes_and_doesnt_come_back()
    {
        var desktop = With(Song("a", 1), Song("b", 2));
        var laptop = new PlayHistoryStore();
        await Send(desktop, laptop);

        laptop.Remove("a");
        await Send(laptop, desktop);
        Assert.Equal(["b"], desktop.All.Select(e => e.Id));

        // The desktop's older copy, sent again, doesn't bring it back.
        var stale = With(Song("a", 1));
        await Send(stale, laptop);
        Assert.Equal(["b"], laptop.All.Select(e => e.Id));
    }

    [Fact]
    public async Task Clearing_drops_what_was_played_before_it_everywhere()
    {
        var desktop = With(Song("a", 1), Song("b", 2));
        var laptop = new PlayHistoryStore();
        await Send(desktop, laptop);

        desktop.Clear();
        await Send(desktop, laptop, full: false);
        Assert.Empty(laptop.All);
        await Send(With(Song("a", 1)), laptop); // an old copy elsewhere stays gone
        Assert.Empty(laptop.All);
    }

    [Fact]
    public async Task A_long_history_goes_in_parts_small_enough_for_a_relay()
    {
        var songs = Enumerable.Range(0, 250).Select(i => Song($"s{i:000}", i, new string('x', 100))).ToArray();
        var parts = await new HistorySync(With(songs)).ExportAsync(full: true);

        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.True(p.GetRawText().Length < 100_000, $"a part is {p.GetRawText().Length} bytes"));
        var recent = await new HistorySync(With(songs)).ExportAsync(full: false);
        Assert.Equal(HistorySync.Recent, Assert.Single(recent).GetProperty("entries").GetArrayLength());

        var other = new PlayHistoryStore();
        await Send(With(songs), other);
        Assert.Equal(250, other.All.Count);
    }

    [Fact]
    public void Removals_and_the_clearing_survive_a_restart()
    {
        var path = Path.Combine(_dir.Path, "history.json");
        var store = With(Song("a", 1), Song("b", 2));
        store.Remove("a");
        store.SaveToDisk(path);

        var reloaded = PlayHistoryStore.LoadFromDisk(path);
        Assert.Equal(["a"], reloaded.SyncedHistory().Removed);
        Assert.False(reloaded.MergeSynced(new SyncedHistory([Song("a", 1)], [], null)));

        reloaded.Clear();
        reloaded.SaveToDisk(path);
        Assert.NotNull(PlayHistoryStore.LoadFromDisk(path).SyncedHistory().ClearedBefore);
    }
}

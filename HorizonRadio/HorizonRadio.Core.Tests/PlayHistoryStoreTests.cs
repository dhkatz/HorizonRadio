using System;
using System.Globalization;
using System.IO;
using System.Linq;
using HorizonRadio.Core.History;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The on-disk play history: newest-first ordering, a hard cap with oldest-eviction, the
/// match-state update, and a faithful JSON round-trip (including a radio song's candidates).
/// </summary>
public class PlayHistoryStoreTests
{
    private static PlayHistoryEntry Entry(string id, DateTimeOffset at, HistoryMatchState state = HistoryMatchState.Matched) => new()
    {
        Id = id,
        PlayedAt = at,
        Title = "Song " + id,
        Artist = "Artist " + id,
        SourceId = "youtube",
        SourceDisplay = "YouTube",
        MatchState = state,
        Sources = [new ReplaySource("youtube", "YouTube", "https://www.youtube.com/watch?v=" + id)],
    };

    [Fact]
    public void Add_keeps_newest_first()
    {
        var store = new PlayHistoryStore();
        var t = DateTimeOffset.UtcNow;
        store.Add(Entry("a", t));
        store.Add(Entry("b", t.AddSeconds(1)));

        Assert.Equal(new[] { "b", "a" }, store.All.Select(e => e.Id));
    }

    [Fact]
    public void Add_evicts_the_oldest_past_the_cap()
    {
        var store = new PlayHistoryStore();
        var t = DateTimeOffset.UtcNow;
        for (var i = 0; i < PlayHistoryStore.MaxEntries + 5; i++)
            store.Add(Entry(i.ToString(CultureInfo.InvariantCulture), t.AddSeconds(i)));

        var all = store.All;
        Assert.Equal(PlayHistoryStore.MaxEntries, all.Count);
        Assert.Equal((PlayHistoryStore.MaxEntries + 4).ToString(CultureInfo.InvariantCulture), all[0].Id);  // newest survives
        Assert.DoesNotContain(all, e => e.Id == "0");                            // oldest evicted
    }

    [Fact]
    public void SetMatchState_updates_in_place_and_notifies()
    {
        var store = new PlayHistoryStore();
        store.Add(Entry("a", DateTimeOffset.UtcNow, HistoryMatchState.Unknown));
        var fired = 0;
        store.Changed += () => fired++;

        store.SetMatchState("a", HistoryMatchState.Unmatched);
        Assert.Equal(HistoryMatchState.Unmatched, store.All.Single().MatchState);
        Assert.Equal(1, fired);

        store.SetMatchState("a", HistoryMatchState.Unmatched); // unchanged → no event
        store.SetMatchState("missing", HistoryMatchState.Matched);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void SetSources_only_notifies_on_a_real_change()
    {
        var store = new PlayHistoryStore();
        store.Add(Entry("a", DateTimeOffset.UtcNow));
        var fired = 0;
        store.Changed += () => fired++;

        var sources = new[] { new ReplaySource("youtube", "YouTube", "yt1") };
        store.SetSources("a", sources);
        Assert.Equal(1, fired);

        store.SetSources("a", new[] { new ReplaySource("youtube", "YouTube", "yt1") }); // value-equal → no event
        Assert.Equal(1, fired);

        store.SetSources("a", []); // empty→empty also no-ops once already empty
        store.SetSources("missing", sources);
        Assert.Equal(2, fired); // only the clear-to-empty counted (a real change from the youtube source)
        Assert.Empty(store.All.Single().Sources);
    }

    [Fact]
    public void SaveToDisk_overwrites_atomically_without_leaving_temp_files()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "history.json");
        var store = new PlayHistoryStore();
        store.Add(Entry("a", DateTimeOffset.UtcNow));
        store.SaveToDisk(path);
        store.Add(Entry("b", DateTimeOffset.UtcNow.AddSeconds(1)));
        store.SaveToDisk(path); // overwrite an existing file

        Assert.Equal(2, PlayHistoryStore.LoadFromDisk(path).All.Count);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp")); // temp renamed away, none left behind
    }

    [Fact]
    public void Remove_and_Clear()
    {
        var store = new PlayHistoryStore();
        var t = DateTimeOffset.UtcNow;
        store.Add(Entry("a", t));
        store.Add(Entry("b", t));

        store.Remove("a");
        Assert.Equal(new[] { "b" }, store.All.Select(e => e.Id));

        store.Clear();
        Assert.Empty(store.All);
    }

    [Fact]
    public void Round_trips_through_disk()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "history.json");
        var at = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);

        var radio = new PlayHistoryEntry
        {
            Id = "r1",
            PlayedAt = at,
            Title = "テロメアの産声",
            Artist = "Heavenz",
            Album = "Some Album",
            Year = 2015,
            SourceId = "radio",
            SourceDisplay = "Internet Radio",
            MatchState = HistoryMatchState.Unmatched,
            Candidates = [new HistoryCandidate("Heavenz", "テロメアの産声"), new HistoryCandidate(null, "title only")],
        };

        var store = new PlayHistoryStore();
        store.Add(Entry("y1", at.AddSeconds(1)));
        store.Add(radio);
        store.SaveToDisk(path);

        var loaded = PlayHistoryStore.LoadFromDisk(path).All;
        Assert.Equal(2, loaded.Count);

        var r = loaded.Single(e => e.Id == "r1");
        Assert.Equal("テロメアの産声", r.Title);
        Assert.Equal("Heavenz", r.Artist);
        Assert.Equal("Some Album", r.Album);
        Assert.Equal(2015, r.Year);
        Assert.Equal(HistoryMatchState.Unmatched, r.MatchState);
        Assert.Equal(2, r.Candidates.Count);
        Assert.Equal("Heavenz", r.Candidates[0].Artist);
        Assert.Null(r.Candidates[1].Artist);

        var y = loaded.Single(e => e.Id == "y1");
        Assert.True(y.IsReplayable);
        Assert.Equal("youtube", y.Sources.Single().SourceId);
        Assert.Equal("YouTube", y.Sources.Single().SourceDisplay);
    }

    [Fact]
    public void Stores_multiple_sources_and_round_trips_them()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "history.json");
        var store = new PlayHistoryStore();
        store.Add(new PlayHistoryEntry
        {
            Id = "m1",
            PlayedAt = DateTimeOffset.UtcNow,
            Title = "Song",
            Artist = "Artist",
            SourceId = "radio",
            SourceDisplay = "Internet Radio",
            MatchState = HistoryMatchState.Matched,
            Sources =
            [
                new ReplaySource("youtube", "YouTube", "https://www.youtube.com/watch?v=abc"),
                new ReplaySource("spotify-driven", "Spotify", "spotify:track:xyz"),
            ],
        });
        store.SaveToDisk(path);

        var e = PlayHistoryStore.LoadFromDisk(path).All.Single();
        Assert.Equal(2, e.Sources.Count);
        Assert.Equal(new[] { "youtube", "spotify-driven" }, e.Sources.Select(s => s.SourceId));
        Assert.Equal("spotify:track:xyz", e.Sources[1].Locator);
    }

    [Fact]
    public void Loads_legacy_single_source_entries()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "history.json");
        // A file written by the pre-multi-source build: a single replaySourceId/replayLocator.
        File.WriteAllText(path,
            """
            { "entries": [ {
              "id": "leg1", "playedAt": "2026-06-16T12:00:00+00:00",
              "title": "Old Song", "artist": "Old Artist",
              "sourceId": "youtube", "sourceDisplay": "YouTube",
              "matchState": "Matched",
              "replaySourceId": "youtube", "replayLocator": "https://www.youtube.com/watch?v=leg"
            } ] }
            """);

        var e = PlayHistoryStore.LoadFromDisk(path).All.Single();
        var src = Assert.Single(e.Sources);
        Assert.Equal("youtube", src.SourceId);
        Assert.Equal("https://www.youtube.com/watch?v=leg", src.Locator);
        Assert.True(e.IsReplayable);
    }

    [Fact]
    public void Missing_file_loads_an_empty_store()
        => Assert.Empty(PlayHistoryStore.LoadFromDisk(Path.Combine(Path.GetTempPath(), "hzn-nope-" + Guid.NewGuid().ToString("N") + ".json")).All);
}

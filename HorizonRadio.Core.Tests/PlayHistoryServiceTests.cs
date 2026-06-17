using System;
using System.Linq;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources.Local;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The recorder: how track changes turn into history entries — dedup of re-fires, quick-skip
/// collapsing, replay-handle derivation, and skipping non-songs. Driven via the internal
/// Record() seam with a controllable clock, so no live source/timer is needed.
/// </summary>
public class PlayHistoryServiceTests
{
    // A mutable clock the test advances between records.
    private DateTimeOffset _now = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);

    private PlayHistoryService NewService(PlayHistoryStore store)
        => new(store, clock: () => _now, persist: false);

    private static Track YouTube(string id, string title = "Song", string artist = "Artist") =>
        new(title, artist, null, null, "youtube", "YouTube", ExternalId: $"youtube:{id}");

    private static Track Radio(string title, string artist) =>
        new(title, artist, null, null, "radio", "Internet Radio", ExternalId: $"radio:{artist} - {title}");

    [Fact]
    public void Records_a_youtube_song_with_a_direct_replay_handle()
    {
        var store = new PlayHistoryStore();
        var svc = NewService(store);

        svc.Record(YouTube("abc"));

        var e = store.All.Single();
        var src = Assert.Single(e.Sources);
        Assert.Equal("youtube", src.SourceId);
        Assert.Equal("https://www.youtube.com/watch?v=abc", src.Locator);
        Assert.True(e.IsReplayable);
        Assert.Equal(HistoryMatchState.Matched, e.MatchState); // a real source identity is "matched"
    }

    [Fact]
    public void Local_song_records_the_file_path_as_its_replay_handle()
    {
        var store = new PlayHistoryStore();
        var svc = NewService(store);
        var path = @"C:\Music\song.flac";

        svc.Record(new Track("Song", "Artist", null, null, "local", "Local Files",
            ExternalId: LocalPlayableItem.LocalExternalId(path)));

        var src = Assert.Single(store.All.Single().Sources);
        Assert.Equal("local", src.SourceId);
        Assert.Equal(path, src.Locator);
    }

    [Fact]
    public void Same_song_refired_is_recorded_once()
    {
        var store = new PlayHistoryStore();
        var svc = NewService(store);

        svc.Record(YouTube("abc"));
        _now = _now.AddSeconds(30);  // outside the skip window — still deduped by signature
        svc.Record(YouTube("abc"));

        Assert.Single(store.All);
    }

    [Fact]
    public void A_song_skipped_within_the_window_is_dropped()
    {
        var store = new PlayHistoryStore();
        var svc = NewService(store);

        svc.Record(YouTube("a", title: "First"));
        _now = _now.Add(PlayHistoryService.SkipWindow).AddSeconds(-2); // still inside the window
        svc.Record(YouTube("b", title: "Second"));

        var all = store.All;
        Assert.Single(all);
        Assert.Equal("Second", all[0].Title); // the skipped first song was removed
    }

    [Fact]
    public void Songs_each_heard_long_enough_are_both_kept()
    {
        var store = new PlayHistoryStore();
        var svc = NewService(store);

        svc.Record(YouTube("a", title: "First"));
        _now = _now.Add(PlayHistoryService.SkipWindow).AddSeconds(5); // past the window → "heard"
        svc.Record(YouTube("b", title: "Second"));

        Assert.Equal(new[] { "Second", "First" }, store.All.Select(e => e.Title));
    }

    [Fact]
    public void Radio_song_has_no_direct_handle_and_is_unknown_without_a_resolver()
    {
        var store = new PlayHistoryStore();
        var svc = NewService(store);

        svc.Record(Radio("Some Song", "Some Artist"));

        var e = store.All.Single();
        Assert.False(e.IsReplayable);                          // no playable origin; the view resolves sources lazily
        Assert.Empty(e.Sources);
        Assert.Equal(HistoryMatchState.Unknown, e.MatchState); // verdict resolved later, on view
    }

    [Fact]
    public void Non_song_placeholders_and_empty_titles_are_ignored()
    {
        var store = new PlayHistoryStore();
        var svc = NewService(store);

        // Radio station card before the first ICY title: not a song.
        svc.Record(new Track("Vocaloid Radio", "Vocaloid Radio", null, null, "radio", "Internet Radio", Resolvable: false));
        svc.Record(Track.Empty);
        svc.Record(new Track("   ", "Artist", null, null, "youtube", "YouTube"));

        Assert.Empty(store.All);
    }
}

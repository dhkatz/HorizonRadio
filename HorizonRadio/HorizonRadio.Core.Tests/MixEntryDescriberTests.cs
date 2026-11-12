using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Source.Local;
using HorizonRadio.Source.Spotify;
using HorizonRadio.Source.YouTube;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Mix entries shown as what they are: the describer (a source's own description of a collection,
/// else the entry's one track), the store that keeps descriptions between sessions, and the
/// sources recognising their own links so a pasted locator picks its source.
/// </summary>
public class MixEntryDescriberTests
{
    private static MixEntryDescriber Describer(MixEntryInfoStore? store = null) =>
        new(new MixContentResolver(new SourceConfigStore(), TestSources.Catalog), metadata: null, store);

    [Fact]
    public async Task A_folder_is_described_by_its_name_size_and_cover()
    {
        using var dir = TempDir.Create();
        var album = Path.Combine(dir.Path, "Discovery");
        Directory.CreateDirectory(album);
        File.WriteAllBytes(Path.Combine(album, "01 One More Time.mp3"), []);
        File.WriteAllBytes(Path.Combine(album, "02 Aerodynamic.flac"), []);
        File.WriteAllBytes(Path.Combine(album, "notes.txt"), []);
        File.WriteAllBytes(Path.Combine(album, "cover.jpg"), [1, 2, 3]);

        var info = await Describer().DescribeAsync(new ContentRef("local", album), CancellationToken.None);

        Assert.Equal("Discovery", info.Title);
        Assert.Equal(dir.Path, info.Subtitle);
        Assert.Equal(ContentKind.Folder, info.Kind);
        Assert.Equal(2, info.ItemCount);
        Assert.Equal([1, 2, 3], info.Art);
    }

    [Fact]
    public async Task A_single_file_is_described_as_its_track()
    {
        using var dir = TempDir.Create();
        var file = Path.Combine(dir.Path, "Daft Punk - One More Time.mp3");
        File.WriteAllBytes(file, []); // no tags: the file name is all there is

        var info = await Describer().DescribeAsync(new ContentRef("local", file), CancellationToken.None);

        Assert.Equal("One More Time", info.Title);
        Assert.Equal("Daft Punk", info.Subtitle);
        Assert.Equal(ContentKind.Track, info.Kind);
        Assert.Equal(1, info.ItemCount);
    }

    [Fact]
    public async Task An_entry_that_does_not_resolve_says_why()
    {
        using var dir = TempDir.Create();
        var missing = Path.Combine(dir.Path, "nope");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Describer().DescribeAsync(new ContentRef("local", missing), CancellationToken.None));
        Assert.Contains("doesn't exist", ex.Message);
    }

    [Fact]
    public async Task A_description_is_kept_for_next_time()
    {
        using var dir = TempDir.Create();
        var file = Path.Combine(dir.Path, "Artist - Song.mp3");
        File.WriteAllBytes(file, []);
        var entry = new ContentRef("local", file);
        var storeDir = Path.Combine(dir.Path, "store");

        Assert.Null(Describer(new MixEntryInfoStore(storeDir)).Cached(entry));
        await Describer(new MixEntryInfoStore(storeDir)).DescribeAsync(entry, CancellationToken.None);

        var kept = Describer(new MixEntryInfoStore(storeDir)).Cached(entry);
        Assert.NotNull(kept);
        Assert.Equal("Song", kept.Value.Info.Title);
        Assert.False(kept.Value.Stale);
    }

    [Fact]
    public void Store_round_trips_text_and_covers_and_prunes_what_no_mix_holds()
    {
        using var dir = TempDir.Create();
        var kept = new ContentRef("spotify", "spotify:playlist:abc");
        var dropped = new ContentRef("youtube", "https://youtu.be/xyz");

        var store = new MixEntryInfoStore(dir.Path);
        store.Put(kept, new MixEntryInfo("Road Trip", "dave", ContentKind.Playlist, 42, [9, 8, 7]));
        store.Put(dropped, new MixEntryInfo("Some Video", null, ContentKind.Track, 1, [5]));
        Assert.Equal(3, Directory.GetFiles(dir.Path).Length); // index + two covers

        var reloaded = new MixEntryInfoStore(dir.Path);
        var info = reloaded.Get(kept)?.Info;
        Assert.NotNull(info);
        Assert.Equal(("Road Trip", "dave", ContentKind.Playlist, 42), (info.Title, info.Subtitle, info.Kind, info.ItemCount));
        Assert.Equal([9, 8, 7], info.Art);

        reloaded.Prune([kept]);
        Assert.Null(reloaded.Get(dropped));
        Assert.NotNull(reloaded.Get(kept));
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length); // the dropped cover is gone too
        Assert.Null(new MixEntryInfoStore(dir.Path).Get(dropped));
    }

    [Fact]
    public void A_corrupt_store_starts_empty()
    {
        using var dir = TempDir.Create();
        File.WriteAllText(Path.Combine(dir.Path, "index.json"), "{ not json");

        Assert.Null(new MixEntryInfoStore(dir.Path).Get(new ContentRef("local", @"C:\Music")));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true, false)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true, false)]
    [InlineData("music.youtube.com/watch?v=abc", true, false)]
    [InlineData("https://www.youtube.com/playlist?list=PL123", true, true)]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PL123", true, true)]
    [InlineData("https://www.youtube.com/@SomeChannel", true, true)]
    [InlineData("https://www.youtube.com/watch?v=abc&list=", true, false)]
    [InlineData("https://notyoutube.com/watch?v=abc", false, false)]
    [InlineData("https://open.spotify.com/track/abc", false, false)]
    [InlineData("", false, false)]
    public void YouTube_links_are_recognised(string locator, bool isYouTube, bool isCollection)
    {
        Assert.Equal(isYouTube, YouTubeLinks.IsYouTube(locator));
        Assert.Equal(isCollection, YouTubeLinks.IsCollection(locator));
    }

    [Fact]
    public void A_pasted_locator_picks_its_source()
    {
        using var dir = TempDir.Create();

        Assert.True(TestSources.Spotify.Recognizes("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M"));
        Assert.True(TestSources.Spotify.Recognizes("spotify:album:4aawyAB9vmqN3uQ7FjRGTy"));
        Assert.False(TestSources.Spotify.Recognizes("https://youtu.be/abc"));

        Assert.True(TestSources.YouTube.Recognizes("https://youtu.be/abc"));

        Assert.True(new LocalFileSourceFactory().Recognizes(dir.Path));
        Assert.True(new LocalFileSourceFactory().Recognizes($"\"{dir.Path}\""));
        // A mistyped full path is still a path: it should fail as one, not go to another source.
        Assert.True(new LocalFileSourceFactory().Recognizes(Path.Combine(dir.Path, "missing")));
        Assert.False(new LocalFileSourceFactory().Recognizes("Music"));
        Assert.False(new LocalFileSourceFactory().Recognizes("https://youtu.be/abc"));

        Assert.True(TestSources.Radio.Recognizes("radio://9617a958-0601-11e8-ae97-52543be04c81"));
        // A bare stream URL could be anything, so no source claims it.
        Assert.False(TestSources.Radio.Recognizes("https://stream.example.com/live.mp3"));
    }
}

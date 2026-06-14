using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The cross-source merge folds the same track from different sources into one row, but
/// stays conservative: it won't merge different songs, covers, or different-length
/// versions, and it preserves the sources' own ordering.
/// </summary>
public class SearchMergeTests
{
    private static SearchResult Spotify(string title, string artist, TimeSpan? dur = null)
        => new("spotify-driven", SearchResultKind.Track, title, artist, null, "spotify:track:x", dur);

    private static SearchResult YouTube(string title, string channel, TimeSpan? dur = null)
        => new("youtube", SearchResultKind.Track, title, channel, null, "https://youtu.be/x", dur);

    [Theory]
    [InlineData("Get Lucky (Official Video) ft. Pharrell Williams", "get lucky")]
    [InlineData("Blinding Lights [Remastered]", "blinding lights")]
    [InlineData("Happy — feat. Someone", "happy")] // the "feat. …" run is stripped entirely
    [InlineData("Señorita!!!", "se orita")]         // non-ASCII letters fall away (consistent across sources)
    public void Normalize_strips_brackets_feat_and_punctuation(string input, string expected)
        => Assert.Equal(expected, SearchMerge.Normalize(input));

    [Fact]
    public void Merges_same_track_across_sources_when_artist_is_in_youtube_title()
    {
        // Spotify carries the artist in its subtitle; YouTube carries it in the title.
        var results = new[]
        {
            Spotify("Get Lucky", "Daft Punk, Pharrell Williams", TimeSpan.FromSeconds(248)),
            YouTube("Daft Punk - Get Lucky (Official Audio) ft. Pharrell Williams", "Daft Punk", TimeSpan.FromSeconds(249)),
        };

        var merged = SearchMerge.Merge(results);

        var row = Assert.Single(merged);
        Assert.Equal(2, row.Sources.Count);
        Assert.Equal("Get Lucky", row.Title); // display comes from the first (Spotify) source
    }

    [Fact]
    public void Does_not_merge_when_durations_conflict()
    {
        var results = new[]
        {
            Spotify("Get Lucky", "Daft Punk", TimeSpan.FromSeconds(248)),       // album cut
            YouTube("Daft Punk - Get Lucky", "Daft Punk", TimeSpan.FromSeconds(40)), // short snippet
        };

        Assert.Equal(2, SearchMerge.Merge(results).Count);
    }

    [Fact]
    public void Does_not_merge_when_only_one_side_has_a_duration()
    {
        // A hit reporting no duration (e.g. a YouTube livestream/premiere) must not fold
        // into a studio track on a token match alone — stay conservative.
        var results = new[]
        {
            Spotify("Get Lucky", "Daft Punk", TimeSpan.FromSeconds(248)),
            YouTube("Daft Punk - Get Lucky", "Daft Punk", dur: null),
        };

        Assert.Equal(2, SearchMerge.Merge(results).Count);
    }

    [Fact]
    public void Does_not_merge_different_songs()
    {
        var results = new[]
        {
            Spotify("Get Lucky", "Daft Punk"),
            YouTube("Adele - Hello", "Adele"),
        };

        Assert.Equal(2, SearchMerge.Merge(results).Count);
    }

    [Fact]
    public void Does_not_merge_on_a_single_shared_token()
    {
        // "Lucky" alone is too little signal to fold into "Get Lucky".
        var results = new[]
        {
            Spotify("Lucky", ""),
            YouTube("Get Lucky", "Daft Punk"),
        };

        Assert.Equal(2, SearchMerge.Merge(results).Count);
    }

    [Fact]
    public void Preserves_first_seen_order()
    {
        var results = new[]
        {
            Spotify("Alpha", "A"),
            YouTube("Beta - song", "B"),
            Spotify("Gamma", "C"),
        };

        var merged = SearchMerge.Merge(results);

        Assert.Collection(merged,
            r => Assert.Equal("Alpha", r.Title),
            r => Assert.Equal("Beta - song", r.Title),
            r => Assert.Equal("Gamma", r.Title));
    }

    [Fact]
    public void Single_source_results_pass_through_unmerged()
    {
        var results = new[]
        {
            Spotify("Song One", "Artist"),
            Spotify("Song Two", "Artist"),
        };

        var merged = SearchMerge.Merge(results);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, r => Assert.Single(r.Sources));
    }
}

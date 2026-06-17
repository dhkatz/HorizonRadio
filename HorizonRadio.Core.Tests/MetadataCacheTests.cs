using System;
using System.IO;
using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The cache's retry policy: art-bearing entries are permanent (art never changes for a
/// recording), while art-less ones (a miss or a text-only partial hit) are retried once they
/// go stale — by TTL or by a <see cref="MetadataCache.CurrentCacheVersion"/> bump — so a matching
/// or parsing fix can finally surface on a song that was already seen and cached as a miss.
/// </summary>
public sealed class MetadataCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hzn-cache-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static string Key => MetadataCache.Key("p", "q");
    private string PathFor => Path.Combine(_root, Key + ".json");

    private MetadataCache Cache(DateTimeOffset now, int version = 1) =>
        new(_root, retryTtl: TimeSpan.FromDays(14), cacheVersion: version, now: () => now);

    [Fact]
    public void Pvs_round_trip_through_disk()
    {
        var pvs = new[]
        {
            new PlayableRef("YouTube", "https://youtu.be/a"),
            new PlayableRef("Niconico", "https://www.nicovideo.jp/watch/sm1"),
        };
        Cache(T0).Put(Key, new MetadataCache.Entry("Song", "Artist", null, new byte[] { 1 }, Mbid: null, Year: 2014, Pvs: pvs));

        var hit = Cache(T0).TryGet(Key); // fresh instance → reads from disk

        Assert.NotNull(hit!.Pvs);
        Assert.Equal(2, hit.Pvs!.Count);
        Assert.Equal("YouTube", hit.Pvs[0].Service);
        Assert.Equal("https://www.nicovideo.jp/watch/sm1", hit.Pvs[1].Url);
    }

    [Fact]
    public void Art_bearing_entry_is_kept_even_when_old_and_a_version_behind()
    {
        Cache(T0, version: 1).Put(Key, new MetadataCache.Entry("Song", "Artist", "Album",
            new byte[] { 1, 2, 3 }, Mbid: null));

        // Far past the TTL and a newer logic version — art entries still never expire.
        var hit = Cache(T0.AddDays(999), version: 2).TryGet(Key);

        Assert.NotNull(hit);
        Assert.Equal("Song", hit!.Title);
        Assert.NotNull(hit.AlbumArt);
    }

    [Fact]
    public void Fresh_miss_is_returned_so_the_lookup_is_skipped()
    {
        Cache(T0).PutMiss(Key);

        var hit = Cache(T0.AddDays(1)).TryGet(Key);

        Assert.NotNull(hit);          // a cached (empty) entry, not absent → provider skips searching
        Assert.Null(hit!.AlbumArt);
        Assert.Null(hit.Title);
    }

    [Fact]
    public void Miss_past_the_ttl_is_treated_as_absent_and_removed()
    {
        Cache(T0).PutMiss(Key);

        Assert.Null(Cache(T0.AddDays(15)).TryGet(Key));   // stale → re-search
        Assert.False(File.Exists(PathFor));               // and dropped from disk
    }

    [Fact]
    public void Art_less_partial_hit_from_an_older_version_is_invalidated()
    {
        // Text but no cover (the "starry song" shape) written under version 1.
        Cache(T0, version: 1).Put(Key, new MetadataCache.Entry("Song", "Artist", null, null, null));

        // New logic version, well within the TTL — still invalidated because the version changed.
        Assert.Null(Cache(T0.AddHours(1), version: 2).TryGet(Key));
    }

    [Fact]
    public void Legacy_unversioned_miss_is_invalidated()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathFor, "{}");   // a pre-versioning negative entry

        Assert.Null(Cache(T0).TryGet(Key));
    }
}

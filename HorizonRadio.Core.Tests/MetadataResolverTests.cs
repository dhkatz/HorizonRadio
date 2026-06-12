using System.Collections.Generic;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The merge engine: contributions fill only what's missing by default, honor the
/// provider order, and respect per-field forced overrides.
/// </summary>
public class MetadataResolverTests
{
    private sealed class FakeContributor(string id, MetadataContribution contribution) : IMetadataProvider
    {
        public string Id => id;
        public Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct)
            => Task.FromResult<MetadataContribution?>(contribution);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Track Seed(string title = "SrcTitle", string artist = "", byte[]? art = null) =>
        new(title, artist, null, art, "local", "Local Files");

    private static MetadataPolicy Policy(IEnumerable<string> order, Dictionary<MetadataField, string>? forced = null)
        => new([MetadataPolicy.SourceId, .. order], forced ?? new());

    [Fact]
    public async Task No_contributors_returns_seed_unchanged()
    {
        var resolver = new MetadataResolver();
        var seed = Seed();
        var result = await resolver.ResolveAsync(seed, CancellationToken.None);
        Assert.Same(seed, result);
    }

    [Fact]
    public async Task Provider_fills_only_missing_fields()
    {
        var resolver = new MetadataResolver();
        var art = new byte[] { 1, 2, 3 };
        resolver.Configure(
            [new FakeContributor("mb", new MetadataContribution(Title: "Canonical", Artist: "RealArtist", Art: art))],
            Policy(["mb"]));

        var result = await resolver.ResolveAsync(Seed(title: "SrcTitle", artist: ""), CancellationToken.None);

        Assert.Equal("SrcTitle", result.Title);      // source title kept (it was present)
        Assert.Equal("RealArtist", result.Artist);    // artist was missing → filled
        Assert.Same(art, result.AlbumArt);            // art was missing → filled
    }

    [Fact]
    public async Task Forced_field_overrides_even_when_source_has_it()
    {
        var resolver = new MetadataResolver();
        var srcArt = new byte[] { 9 };
        var spotifyArt = new byte[] { 7 };
        resolver.Configure(
            [new FakeContributor("spotify", new MetadataContribution(Art: spotifyArt))],
            Policy(["spotify"], new() { [MetadataField.Art] = "spotify" }));

        var result = await resolver.ResolveAsync(Seed(art: srcArt), CancellationToken.None);
        Assert.Same(spotifyArt, result.AlbumArt);
    }

    [Fact]
    public async Task Order_decides_precedence_between_providers()
    {
        var resolver = new MetadataResolver();
        var a = new FakeContributor("a", new MetadataContribution(Album: "AlbumA"));
        var b = new FakeContributor("b", new MetadataContribution(Album: "AlbumB"));

        resolver.Configure([a, b], Policy(["a", "b"]));
        var first = await resolver.ResolveAsync(Seed(), CancellationToken.None);
        Assert.Equal("AlbumA", first.Album);

        resolver.Configure([a, b], Policy(["b", "a"]));
        var second = await resolver.ResolveAsync(Seed(), CancellationToken.None);
        Assert.Equal("AlbumB", second.Album);
    }
}

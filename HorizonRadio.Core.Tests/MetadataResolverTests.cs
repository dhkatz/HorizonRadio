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

    // Contributes only when the query's (artist, title) matches exactly — i.e. a catalog that
    // only "has" one specific track, for exercising candidate validation.
    private sealed class MatchingContributor(string id, string artist, string title, MetadataContribution contribution) : IMetadataProvider
    {
        public string Id => id;
        public Task<MetadataContribution?> ContributeAsync(MetadataQuery q, CancellationToken ct)
            => Task.FromResult<MetadataContribution?>(
                string.Equals(q.Artist, artist, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(q.Title, title, StringComparison.OrdinalIgnoreCase) ? contribution : null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Throws if asked to contribute — proves a code path never reaches the provider search.
    private sealed class ThrowingContributor : IMetadataProvider
    {
        public string Id => "boom";
        public Task<MetadataContribution?> ContributeAsync(MetadataQuery q, CancellationToken ct)
            => throw new InvalidOperationException("a non-resolvable placeholder must not be searched");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Track Seed(string title = "SrcTitle", string artist = "", byte[]? art = null, byte[]? fallbackArt = null) =>
        new(title, artist, null, art, "local", "Local Files", FallbackArt: fallbackArt);

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
    public async Task Non_resolvable_placeholder_skips_the_provider_search()
    {
        var resolver = new MetadataResolver();
        var logo = new byte[] { 4, 2 };
        // A contributor that would throw if queried — so a green test proves it's never reached.
        resolver.Configure([new ThrowingContributor()], Policy(["boom"]));

        // A radio station card before any song: not a song, carries its logo, Resolvable = false.
        var placeholder = new Track("Vocaloid Radio", "Vocaloid Radio", null, logo,
            "radio", "Internet Radio", Resolvable: false);

        var r = await resolver.ResolveAsync(placeholder, CancellationToken.None);

        Assert.Same(logo, r.AlbumArt);              // station logo kept, not a false-matched cover
        Assert.Equal("Vocaloid Radio", r.Title);
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
    public async Task Fallback_art_fills_in_when_no_provider_finds_a_cover()
    {
        var resolver = new MetadataResolver();
        var logo = new byte[] { 4, 2 };
        // A provider that matches text but supplies no art (the Niconico-only track case).
        resolver.Configure(
            [new FakeContributor("itunes", new MetadataContribution(Title: "Canonical"))],
            Policy(["itunes"]));

        var result = await resolver.ResolveAsync(Seed(art: null, fallbackArt: logo), CancellationToken.None);

        Assert.Same(logo, result.AlbumArt); // last-resort station logo
    }

    [Fact]
    public async Task Real_art_beats_fallback_art()
    {
        var resolver = new MetadataResolver();
        var logo = new byte[] { 4, 2 };
        var cover = new byte[] { 1, 1 };
        resolver.Configure(
            [new FakeContributor("itunes", new MetadataContribution(Art: cover))],
            Policy(["itunes"]));

        var result = await resolver.ResolveAsync(Seed(art: null, fallbackArt: logo), CancellationToken.None);

        Assert.Same(cover, result.AlbumArt); // a found cover always wins
    }

    [Fact]
    public async Task Fallback_art_applies_even_with_no_contributors()
    {
        var resolver = new MetadataResolver();
        var logo = new byte[] { 4, 2 };

        var result = await resolver.ResolveAsync(Seed(art: null, fallbackArt: logo), CancellationToken.None);

        Assert.Same(logo, result.AlbumArt);
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

    // -- candidate validation --

    private static Track RadioSeed(string title, string artist, IReadOnlyList<TitleCandidate>? candidates, byte[]? fallback = null) =>
        new(title, artist, null, null, "radio", "Internet Radio", FallbackArt: fallback, Candidates: candidates);

    [Fact]
    public async Task Candidate_that_the_catalog_confirms_wins_over_a_wrong_primary()
    {
        var resolver = new MetadataResolver();
        var art = new byte[] { 1, 2, 3 };
        // Catalog only "has" Heavenz / テロメアの産声.
        resolver.Configure(
            [new MatchingContributor("itunes", "Heavenz", "テロメアの産声",
                new MetadataContribution(Title: "テロメアの産声", Artist: "Heavenz", Art: art))],
            Policy(["itunes"]));

        // Primary mis-parse (channel as artist) + the correct candidate.
        var seed = RadioSeed("Heavenz - テロメアの産声", "ExGrooveCh",
            [new TitleCandidate("Heavenz", "テロメアの産声")]);
        var r = await resolver.ResolveAsync(seed, CancellationToken.None);

        Assert.Equal("テロメアの産声", r.Title);   // the confirmed candidate became the result
        Assert.Equal("Heavenz", r.Artist);
        Assert.Same(art, r.AlbumArt);
    }

    [Fact]
    public async Task Primary_match_short_circuits_candidates()
    {
        var resolver = new MetadataResolver();
        var art = new byte[] { 4 };
        resolver.Configure(
            [new MatchingContributor("itunes", "MuryokuP", "Sacred Secret", new MetadataContribution(Art: art))],
            Policy(["itunes"]));

        // Primary already correct; the reversed candidate would NOT match, but is never tried.
        var seed = RadioSeed("Sacred Secret", "MuryokuP", [new TitleCandidate("Sacred Secret", "MuryokuP")]);
        var r = await resolver.ResolveAsync(seed, CancellationToken.None);

        Assert.Equal("Sacred Secret", r.Title);
        Assert.Same(art, r.AlbumArt);
    }

    [Fact]
    public async Task No_candidate_matches_keeps_the_primary_and_fallback_art()
    {
        var resolver = new MetadataResolver();
        var logo = new byte[] { 9 };
        // Catalog has neither the primary nor the candidate.
        resolver.Configure(
            [new MatchingContributor("itunes", "Someone Else", "Other Song", new MetadataContribution(Art: [7]))],
            Policy(["itunes"]));

        var seed = RadioSeed("Heavenz - テロメアの産声", "ExGrooveCh",
            [new TitleCandidate("Heavenz", "テロメアの産声")], fallback: logo);
        var r = await resolver.ResolveAsync(seed, CancellationToken.None);

        Assert.Equal("Heavenz - テロメアの産声", r.Title); // primary display retained
        Assert.Same(logo, r.AlbumArt);                      // station-logo fallback
        Assert.Null(r.Candidates);                          // cleared on output
    }

    // -- match verdict (drives play history's "couldn't identify" warning) --

    [Fact]
    public async Task ResolveDetailed_reports_matched_when_a_catalog_confirms()
    {
        var resolver = new MetadataResolver();
        resolver.Configure(
            [new MatchingContributor("itunes", "RealArtist", "Real Song", new MetadataContribution(Art: [1]))],
            Policy(["itunes"]));

        var (_, matched) = await resolver.ResolveDetailedAsync(
            Seed(title: "Real Song", artist: "RealArtist"), CancellationToken.None);

        Assert.True(matched);
    }

    [Fact]
    public async Task ResolveDetailed_reports_unmatched_when_no_provider_confirms()
    {
        var resolver = new MetadataResolver();
        resolver.Configure(
            [new MatchingContributor("itunes", "Someone Else", "Other Song", new MetadataContribution(Art: [1]))],
            Policy(["itunes"]));

        var (_, matched) = await resolver.ResolveDetailedAsync(
            Seed(title: "Mystery", artist: "Unknown"), CancellationToken.None);

        Assert.False(matched);
    }

    [Fact]
    public async Task ResolveDetailed_reports_matched_via_a_confirmed_candidate()
    {
        var resolver = new MetadataResolver();
        resolver.Configure(
            [new MatchingContributor("itunes", "Heavenz", "テロメアの産声", new MetadataContribution(Art: [1]))],
            Policy(["itunes"]));

        var seed = RadioSeed("Heavenz - テロメアの産声", "ExGrooveCh",
            [new TitleCandidate("Heavenz", "テロメアの産声")]);
        var (_, matched) = await resolver.ResolveDetailedAsync(seed, CancellationToken.None);

        Assert.True(matched);
    }

    [Fact]
    public async Task ResolveDetailed_reports_unmatched_with_no_contributors()
    {
        var resolver = new MetadataResolver();
        var (_, matched) = await resolver.ResolveDetailedAsync(Seed(), CancellationToken.None);
        Assert.False(matched);
    }
}

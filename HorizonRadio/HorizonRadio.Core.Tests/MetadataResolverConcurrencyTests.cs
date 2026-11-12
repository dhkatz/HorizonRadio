using HorizonRadio.Core.Metadata;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// How the resolver behaves when lookups overlap: identical lookups share one run, providers
/// are asked at once when the artist is known, the merge stays in priority order whoever
/// answers first, and cancelling one caller doesn't fail the others.
/// </summary>
public class MetadataResolverConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // A provider that holds every call until released, counting calls and recording what it
    // was asked and under which priority ticket.
    private sealed class GatedProvider(string id, MetadataContribution? answer) : IMetadataProvider
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public MetadataQuery? LastQuery;
        public PriorityTicket? Ticket;
        public CancellationToken LastToken;

        public string Id => id;

        public async Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastQuery = query;
            Ticket = MetadataPriority.Current;
            LastToken = ct;
            Called.TrySetResult();
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            return answer;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Answers after a delay, recording the query it got.
    private sealed class DelayedProvider(string id, TimeSpan delay, MetadataContribution? answer) : IMetadataProvider
    {
        public MetadataQuery? LastQuery;
        public string Id => id;

        public async Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct)
        {
            LastQuery = query;
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return answer;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Track Seed(string title = "Song", string artist = "Artist", byte[]? art = null) =>
        new(title, artist, null, art, "radio", "Radio");

    private static MetadataResolver Resolver(params IMetadataProvider[] providers)
    {
        var resolver = new MetadataResolver(NoLog.Instance);
        resolver.Configure(providers, new MetadataPolicy(
            [MetadataPolicy.SourceId, .. providers.Select(p => p.Id)], new Dictionary<MetadataField, string>()));
        return resolver;
    }

    [Fact]
    public async Task Identical_lookups_ask_each_provider_once()
    {
        var art = new byte[] { 1 };
        var provider = new GatedProvider("p", new MetadataContribution(Album: "Album", Art: art));
        var resolver = Resolver(provider);

        // Now Playing, the queue tile, and History all ask about the new song at once.
        var resolves = Enumerable.Range(0, 3)
            .Select(_ => resolver.ResolveAsync(Seed(), CancellationToken.None))
            .ToArray();
        await provider.Called.Task.WaitAsync(Timeout);
        provider.Release.SetResult();
        var results = await Task.WhenAll(resolves).WaitAsync(Timeout);

        Assert.Equal(1, provider.Calls);
        Assert.All(results, r => Assert.Equal("Album", r.Album));
        Assert.All(results, r => Assert.Same(art, r.AlbumArt));
    }

    [Fact]
    public async Task Callers_sharing_a_lookup_keep_their_own_art()
    {
        var provider = new GatedProvider("p", new MetadataContribution(Album: "Album")); // no art
        var resolver = Resolver(provider);
        var mine = new byte[] { 1 };
        var theirs = new byte[] { 2 };

        var first = resolver.ResolveAsync(Seed(art: mine), CancellationToken.None);
        var second = resolver.ResolveAsync(Seed(art: theirs), CancellationToken.None);
        await provider.Called.Task.WaitAsync(Timeout);
        provider.Release.SetResult();

        Assert.Same(mine, (await first.WaitAsync(Timeout)).AlbumArt);
        Assert.Same(theirs, (await second.WaitAsync(Timeout)).AlbumArt);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Providers_are_asked_at_once_when_the_artist_is_known()
    {
        var first = new GatedProvider("first", new MetadataContribution(Album: "A"));
        var second = new GatedProvider("second", new MetadataContribution(Year: 2020));
        var resolver = Resolver(first, second);

        var resolve = resolver.ResolveAsync(Seed(), CancellationToken.None);

        // Both are asked before either answers.
        await Task.WhenAll(first.Called.Task, second.Called.Task).WaitAsync(Timeout);
        first.Release.SetResult();
        second.Release.SetResult();
        var result = await resolve.WaitAsync(Timeout);
        Assert.Equal("A", result.Album);
        Assert.Equal(2020, result.Year);
    }

    [Fact]
    public async Task Merge_follows_priority_not_who_answered_first()
    {
        var slowArt = new byte[] { 1 };
        var fastArt = new byte[] { 2 };
        var resolver = Resolver(
            new DelayedProvider("preferred", TimeSpan.FromMilliseconds(150), new MetadataContribution(Art: slowArt)),
            new DelayedProvider("fallback", TimeSpan.Zero, new MetadataContribution(Art: fastArt)));

        var result = await resolver.ResolveAsync(Seed(), CancellationToken.None).WaitAsync(Timeout);

        Assert.Same(slowArt, result.AlbumArt);
    }

    [Fact]
    public async Task Without_an_artist_later_providers_search_with_the_one_found_earlier()
    {
        var finder = new DelayedProvider("finder", TimeSpan.Zero, new MetadataContribution(Artist: "Found"));
        var searcher = new DelayedProvider("searcher", TimeSpan.Zero, null);
        var resolver = Resolver(finder, searcher);

        await resolver.ResolveAsync(Seed(artist: ""), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal("Found", searcher.LastQuery?.Artist);
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_fail_the_others()
    {
        var provider = new GatedProvider("p", new MetadataContribution(Album: "Album"));
        var resolver = Resolver(provider);
        using var skipped = new CancellationTokenSource();

        var gaveUp = resolver.ResolveAsync(Seed(), skipped.Token);
        var stillWaiting = resolver.ResolveAsync(Seed(), CancellationToken.None);
        await provider.Called.Task.WaitAsync(Timeout);
        await skipped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);
        Assert.False(provider.LastToken.IsCancellationRequested);
        provider.Release.SetResult();
        Assert.Equal("Album", (await stillWaiting.WaitAsync(Timeout)).Album);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task The_lookup_stops_when_everyone_gives_up()
    {
        var provider = new GatedProvider("p", new MetadataContribution(Album: "Album"));
        var resolver = Resolver(provider);
        using var skipped = new CancellationTokenSource();

        var resolve = resolver.ResolveAsync(Seed(), skipped.Token);
        await provider.Called.Task.WaitAsync(Timeout);
        await skipped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolve);
        Assert.True(provider.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_slow_provider_does_not_hold_back_the_early_result()
    {
        var slowArt = new byte[] { 1 };
        var slow = new GatedProvider("slow", new MetadataContribution(Art: slowArt));
        var fast = new DelayedProvider("fast", TimeSpan.Zero, new MetadataContribution(Album: "Fast"));
        var resolver = Resolver(slow, fast);
        var early = new TaskCompletionSource<Track>(TaskCreationOptions.RunContinuationsAsynchronously);

        var resolve = resolver.ResolveAsync(Seed(), CancellationToken.None, ResolvePriority.Foreground,
            t => early.TrySetResult(t));

        // The fast answer shows while the slow provider is still working...
        var shown = await early.Task.WaitAsync(Timeout);
        Assert.Equal("Fast", shown.Album);
        Assert.Null(shown.AlbumArt);
        Assert.False(resolve.IsCompleted);

        // ...and the full result adds the slow provider's art when it arrives.
        slow.Release.SetResult();
        var full = await resolve.WaitAsync(Timeout);
        Assert.Equal("Fast", full.Album);
        Assert.Same(slowArt, full.AlbumArt);
    }

    [Fact]
    public async Task No_early_result_when_everyone_answers_quickly()
    {
        var resolver = Resolver(
            new DelayedProvider("a", TimeSpan.Zero, new MetadataContribution(Album: "A")),
            new DelayedProvider("b", TimeSpan.FromMilliseconds(20), new MetadataContribution(Year: 2020)));
        var earlyCalls = 0;

        var result = await resolver.ResolveAsync(Seed(), CancellationToken.None, ResolvePriority.Foreground,
            _ => Interlocked.Increment(ref earlyCalls)).WaitAsync(Timeout);

        Assert.Equal(0, earlyCalls);
        Assert.Equal(2020, result.Year);
    }

    [Fact]
    public async Task No_early_result_when_nothing_matched()
    {
        var slow = new GatedProvider("slow", null);
        var resolver = Resolver(slow, new DelayedProvider("fast", TimeSpan.Zero, null));
        var earlyCalls = 0;

        var resolve = resolver.ResolveAsync(Seed(), CancellationToken.None, ResolvePriority.Foreground,
            _ => Interlocked.Increment(ref earlyCalls));
        await slow.Called.Task.WaitAsync(Timeout);
        await Task.Delay(600); // past the grace period
        slow.Release.SetResult();
        await resolve.WaitAsync(Timeout);

        Assert.Equal(0, earlyCalls);
    }

    [Fact]
    public async Task The_song_playing_now_promotes_a_background_lookup_it_joins()
    {
        var provider = new GatedProvider("p", new MetadataContribution(Album: "Album"));
        var resolver = Resolver(provider);

        var row = resolver.ResolveAsync(Seed(), CancellationToken.None, ResolvePriority.Background);
        await provider.Called.Task.WaitAsync(Timeout);
        Assert.False(provider.Ticket!.IsForeground);

        var nowPlaying = resolver.ResolveAsync(Seed(), CancellationToken.None);
        Assert.True(provider.Ticket.IsForeground);

        provider.Release.SetResult();
        await Task.WhenAll(row, nowPlaying).WaitAsync(Timeout);
        Assert.Equal(1, provider.Calls);
    }
}

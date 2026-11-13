using HorizonRadio.Core.History;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The radio songs in the history are identified by the engine, whatever shows the history: one a
/// catalog confirms gets its official uploads to replay, one none confirms is marked to report.
/// </summary>
public sealed class HistoryIdentifierTests
{
    [Fact]
    public async Task Radio_songs_are_identified_and_given_somewhere_to_replay_them()
    {
        var store = new PlayHistoryStore();
        store.Add(Radio("melt", "Melt", "ryo"));
        store.Add(Radio("mystery", "Track 07", "Unknown"));
        await using var resolver = new MetadataResolver(NoLog.Instance);
        resolver.Configure([new Catalog()], new MetadataPolicy([MetadataPolicy.SourceId, "vocadb"], new Dictionary<MetadataField, string>()));

        using var identifier = new HistoryIdentifier(store, resolver, SourceCatalog.Empty, NoLog.Instance);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        identifier.Idle += () => idle.TrySetResult();
        identifier.Start();
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var melt = store.All.Single(e => e.Id == "melt");
        Assert.Equal(HistoryMatchState.Matched, melt.MatchState);
        Assert.Equal("https://www.youtube.com/watch?v=melt", Assert.Single(melt.Sources).Locator);
        Assert.Equal(HistoryMatchState.Unmatched, store.All.Single(e => e.Id == "mystery").MatchState);
    }

    private static PlayHistoryEntry Radio(string id, string title, string artist) => new()
    {
        Id = id,
        PlayedAt = DateTimeOffset.UtcNow,
        Title = title,
        Artist = artist,
        SourceId = "radio",
        SourceDisplay = "Internet Radio",
    };

    // Knows one song, with its official upload.
    private sealed class Catalog : IMetadataProvider
    {
        public string Id => "vocadb";

        public Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct) =>
            Task.FromResult(query is { Title: "Melt", Artist: "ryo" }
                ? new MetadataContribution("Melt", "ryo", Playables: [new PlayableRef("YouTube", "https://www.youtube.com/watch?v=melt")])
                : null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Net;
using System.Text;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Metadata.Apple;
using HorizonRadio.Metadata.VocaDb;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// A provider caches only what the catalog actually answered. "Not found" is an answer and is
/// cached; a failed request (503 when a catalog throttles, a timeout, an art server error) is
/// not, so the song is looked up again next time instead of being remembered as a miss.
/// </summary>
public sealed class ProviderFailureCachingTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "hzn-failure-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch (IOException) { }
    }

    // Answers every request with whatever the test currently says, counting them.
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string ItunesHit = """
        {"results":[{"trackName":"DYE","artistName":"AVTechNO!","collectionName":"Dye - Single",
                     "artworkUrl100":"https://art.example/100x100bb.jpg","releaseDate":"2015-01-01T00:00:00Z"}]}
        """;

    private static MetadataQuery Query() => new("radio", "DYE", "AVTechNO!", null, null);

    [Fact]
    public async Task A_throttled_search_is_retried_not_cached_as_a_miss()
    {
        var http = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        var provider = new ItunesProvider(new MetadataCache(_cacheDir), IMetadataTrace.Off, new HttpClient(http));

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => provider.ContributeAsync(Query(), CancellationToken.None));

        // The catalog recovers: the song is found, because the failure wasn't remembered as a miss.
        http.Respond = r => r.RequestUri!.Host == "art.example" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(ItunesHit);
        var found = await provider.ContributeAsync(Query(), CancellationToken.None);
        Assert.Equal("Dye - Single", found?.Album);
    }

    [Fact]
    public async Task A_real_miss_is_cached()
    {
        var http = new StubHandler { Respond = _ => Json("""{"results":[]}""") };
        var provider = new ItunesProvider(new MetadataCache(_cacheDir), IMetadataTrace.Off, new HttpClient(http));

        Assert.Null(await provider.ContributeAsync(Query(), CancellationToken.None));
        var asked = http.Requests;
        Assert.Null(await provider.ContributeAsync(Query(), CancellationToken.None));

        Assert.Equal(asked, http.Requests); // answered from the cache
    }

    [Fact]
    public async Task A_match_whose_art_failed_is_used_but_not_cached()
    {
        var http = new StubHandler
        {
            Respond = r => r.RequestUri!.Host == "art.example"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(ItunesHit),
        };
        var provider = new ItunesProvider(new MetadataCache(_cacheDir), IMetadataTrace.Off, new HttpClient(http));

        var first = await provider.ContributeAsync(Query(), CancellationToken.None);
        Assert.Equal("Dye - Single", first?.Album); // the text is still shown now
        Assert.Null(first?.Art);

        // Next time the art server is back, so the song gets its art.
        http.Respond = r => r.RequestUri!.Host == "art.example"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
            : Json(ItunesHit);
        var second = await provider.ContributeAsync(Query(), CancellationToken.None);
        Assert.Equal([1, 2, 3], second?.Art);
    }

    [Fact]
    public async Task Missing_art_is_an_answer_and_is_cached()
    {
        var http = new StubHandler
        {
            Respond = r => r.RequestUri!.Host == "art.example" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(ItunesHit),
        };
        var provider = new ItunesProvider(new MetadataCache(_cacheDir), IMetadataTrace.Off, new HttpClient(http));

        await provider.ContributeAsync(Query(), CancellationToken.None);
        var asked = http.Requests;
        Assert.Equal("Dye - Single", (await provider.ContributeAsync(Query(), CancellationToken.None))?.Album);

        Assert.Equal(asked, http.Requests);
    }

    [Fact]
    public async Task VocaDb_network_errors_are_failures_not_misses()
    {
        var http = new StubHandler { Respond = _ => throw new HttpRequestException("connection reset") };
        var provider = new VocaDbProvider(new MetadataCache(_cacheDir), IMetadataTrace.Off, new HttpClient(http));

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => provider.ContributeAsync(Query(), CancellationToken.None));
    }

    // A provider whose request timed out: HttpClient reports a timeout as a cancellation.
    private sealed class TimingOutProvider : IMetadataProvider
    {
        public string Id => "slow";
        public Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AlbumProvider : IMetadataProvider
    {
        public string Id => "ok";
        public Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct) =>
            Task.FromResult<MetadataContribution?>(new MetadataContribution(Album: "Album"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task One_provider_timing_out_does_not_lose_the_others_answers()
    {
        var resolver = new MetadataResolver();
        resolver.Configure([new TimingOutProvider(), new AlbumProvider()],
            new MetadataPolicy([MetadataPolicy.SourceId, "slow", "ok"], new Dictionary<MetadataField, string>()));

        var result = await resolver.ResolveAsync(new Track("DYE", "AVTechNO!", null, null, "radio", "Radio"), CancellationToken.None);

        Assert.Equal("Album", result.Album);
    }
}

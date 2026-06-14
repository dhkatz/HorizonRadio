using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Radio;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The radio content player turns a locator into exactly one (infinite) station item.
/// A plain http(s) URL (the paste-a-URL path) resolves with no directory call; a blank
/// or non-stream locator is rejected with a user-facing message.
/// </summary>
public class RadioContentPlayerTests
{
    private static RadioContentPlayer Player() => new("ffmpeg.exe", RadioBrowserClient.Shared);

    [Fact]
    public async Task Http_locator_yields_one_station_item_without_a_directory_lookup()
    {
        var items = await Player().EnumerateAsync(
            new ContentRef(RadioSourceFactory.SourceId, "https://example.com/stream.mp3"),
            CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(RadioSourceFactory.SourceId, items[0].Metadata.SourceId);
    }

    [Fact]
    public async Task Blank_locator_throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Player().EnumerateAsync(new ContentRef(RadioSourceFactory.SourceId, "  "), CancellationToken.None));
        Assert.Contains("station URL", ex.Message);
    }

    [Fact]
    public async Task Non_stream_locator_throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Player().EnumerateAsync(new ContentRef(RadioSourceFactory.SourceId, "ftp://nope"), CancellationToken.None));
        Assert.Contains("http", ex.Message);
    }
}

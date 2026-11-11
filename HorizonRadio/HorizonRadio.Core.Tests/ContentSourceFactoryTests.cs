using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.YouTube;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Verifies the content-addressable factories: they advertise themselves via
/// <see cref="IContentSourceFactory"/> (and never as self-driven), name the right
/// content field, and build their engine from the environment fields alone —
/// validating tools (engine) separately from content (locator).
/// </summary>
[Collection("tool-resolver")]
public class ContentSourceFactoryTests
{
    [Fact]
    public void Local_factory_is_content_addressable()
        => Assert.IsAssignableFrom<IContentSourceFactory>(new LocalFileSourceFactory());

    [Fact]
    public void Local_factory_content_key_is_path()
    {
        var f = new LocalFileSourceFactory();
        Assert.Equal(LocalFileSourceFactory.KeyPath, f.ContentKey);
    }

    [Fact]
    public void Content_factories_are_not_self_driven()
    {
        // A content source only plays through the queue; it can't be started on the runner.
        Assert.All(SourceCatalog.All.OfType<IContentSourceFactory>(),
            f => Assert.IsNotAssignableFrom<ISelfDrivenSourceFactory>(f));
    }

    [Fact]
    public void YouTube_factory_is_content_addressable()
        => Assert.IsAssignableFrom<IContentSourceFactory>(new YouTubeSourceFactory());

    [Fact]
    public void YouTube_factory_content_key_is_url()
    {
        var f = new YouTubeSourceFactory();
        Assert.Equal(YouTubeSourceFactory.KeyUrl, f.ContentKey);
    }

    [Fact]
    public void Content_factories_expose_a_locator_hint()
    {
        Assert.False(string.IsNullOrWhiteSpace(new LocalFileSourceFactory().LocatorHint));
        Assert.False(string.IsNullOrWhiteSpace(new YouTubeSourceFactory().LocatorHint));
    }

    [Fact]
    public void YouTube_create_player_missing_tools_throws()
    {
        // Simulate a clean machine: nothing configured AND nothing discoverable.
        ToolResolver.DiscoverOverride = _ => null;
        try
        {
            var f = new YouTubeSourceFactory();
            var ex = Assert.Throws<InvalidOperationException>(() => f.CreatePlayer(new ConfigValues()));
            Assert.Contains("yt-dlp", ex.Message);
        }
        finally { ToolResolver.DiscoverOverride = null; }
    }

    [Fact]
    public async Task YouTube_player_validates_tools_apart_from_content()
    {
        // Tools present but URL empty: tool validation (CreatePlayer) passes and
        // content validation (EnumerateAsync) fails — proving the env/content split.
        using var dir = TempDir.Create();
        var f = new YouTubeSourceFactory();
        var player = f.CreatePlayer(ToolValues(dir));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => player.EnumerateAsync(new ContentRef(f.Id, ""), default));
        Assert.Contains("video or playlist URL", ex.Message);
    }

    private static ConfigValues ToolValues(TempDir dir)
    {
        var values = new ConfigValues();
        values.Set(YouTubeSourceFactory.KeyYtDlp, dir.Touch("yt-dlp.exe"));
        values.Set(YouTubeSourceFactory.KeyFfmpeg, dir.Touch("ffmpeg.exe"));
        return values;
    }
}

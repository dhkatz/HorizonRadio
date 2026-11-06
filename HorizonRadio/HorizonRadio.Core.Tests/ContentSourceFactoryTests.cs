using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.YouTube;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Verifies the content-addressable factories: they advertise themselves via
/// <see cref="IContentSourceFactory"/>, name the right content field, and their
/// single-start <c>Create</c> is a thin adapter that builds the engine from the
/// environment fields and opens the content field — validating tools (engine)
/// before content (locator).
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
    public async Task Local_factory_create_opens_the_content_field()
    {
        using var dir = TempDir.Create();
        dir.Touch("song.flac");
        var f = new LocalFileSourceFactory();
        var values = new ConfigValues();
        values.Set(f.ContentKey, dir.Path);
        await using var source = f.Create(values);
        Assert.Equal("local", source.Id);
    }

    [Fact]
    public void Local_factory_create_empty_path_throws()
    {
        var f = new LocalFileSourceFactory();
        Assert.Throws<InvalidOperationException>(() => f.Create(new ConfigValues()));
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
    public void WithLocator_sets_trimmed_value_under_content_key()
    {
        var f = new LocalFileSourceFactory();
        var values = new ConfigValues().WithLocator(f, @"  C:\Music  ");
        Assert.Equal(@"C:\Music", values.GetString(f.ContentKey));
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
    public void YouTube_create_validates_tools_before_content()
    {
        // Tools present but URL empty: tool validation (CreatePlayer) passes and
        // content validation (Open) fails — proving the env/content split.
        using var dir = TempDir.Create();
        var f = new YouTubeSourceFactory();
        var values = ToolValues(dir);
        var ex = Assert.Throws<InvalidOperationException>(() => f.Create(values));
        Assert.Contains("video or playlist URL", ex.Message);
    }

    [Fact]
    public async Task YouTube_create_with_tools_and_url_returns_source()
    {
        using var dir = TempDir.Create();
        var f = new YouTubeSourceFactory();
        var values = ToolValues(dir);
        values.Set(f.ContentKey, "https://youtu.be/x");
        await using var source = f.Create(values);
        Assert.Equal("youtube", source.Id);
    }

    private static ConfigValues ToolValues(TempDir dir)
    {
        var values = new ConfigValues();
        values.Set(YouTubeSourceFactory.KeyYtDlp, dir.Touch("yt-dlp.exe"));
        values.Set(YouTubeSourceFactory.KeyFfmpeg, dir.Touch("ffmpeg.exe"));
        return values;
    }
}

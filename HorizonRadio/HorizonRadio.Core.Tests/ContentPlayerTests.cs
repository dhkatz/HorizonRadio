using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.YouTube;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Locks the content/engine decouple: an <see cref="IContentPlayer"/> opens a
/// runnable source for a <see cref="ContentRef"/> and rejects bad locators with
/// the same user-facing messages the old factory.Create surfaced.
/// </summary>
public class ContentPlayerTests
{
    [Fact]
    public void Local_open_empty_locator_throws()
    {
        var player = new LocalContentPlayer();
        var ex = Assert.Throws<InvalidOperationException>(
            () => player.Open(new ContentRef("local", "")));
        Assert.Contains("pick a music folder", ex.Message);
    }

    [Fact]
    public void Local_open_missing_path_throws()
    {
        var player = new LocalContentPlayer();
        var missing = Path.Combine(Path.GetTempPath(), "hzn-missing-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => player.Open(new ContentRef("local", missing)));
        Assert.Contains("doesn't exist", ex.Message);
    }

    [Fact]
    public void Local_open_folder_without_audio_throws()
    {
        using var dir = TempDir.Create();
        dir.Touch("notes.txt");
        var player = new LocalContentPlayer();
        var ex = Assert.Throws<InvalidOperationException>(
            () => player.Open(new ContentRef("local", dir.Path)));
        Assert.Contains("no audio files", ex.Message);
    }

    [Fact]
    public async Task Local_open_folder_with_audio_returns_local_source()
    {
        using var dir = TempDir.Create();
        dir.Touch("song.mp3");
        var player = new LocalContentPlayer();
        await using var source = player.Open(new ContentRef("local", dir.Path));
        Assert.Equal("local", source.Id);
    }

    [Fact]
    public void YouTube_open_empty_locator_throws()
    {
        var player = new YouTubeContentPlayer("yt-dlp", "ffmpeg", normalise: false);
        var ex = Assert.Throws<InvalidOperationException>(
            () => player.Open(new ContentRef("youtube", "  ")));
        Assert.Contains("video or playlist URL", ex.Message);
    }

    [Fact]
    public async Task YouTube_open_url_returns_youtube_source()
    {
        // The player builds the source object; it doesn't touch yt-dlp/ffmpeg or
        // the network until StartAsync, so placeholder tool paths are fine here.
        var player = new YouTubeContentPlayer("yt-dlp", "ffmpeg", normalise: false);
        await using var source = player.Open(new ContentRef("youtube", "https://youtu.be/x"));
        Assert.Equal("youtube", source.Id);
    }
}

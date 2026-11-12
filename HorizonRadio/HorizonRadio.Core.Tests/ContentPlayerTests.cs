using HorizonRadio.Sdk.Sources;
using HorizonRadio.Source.Local;
using HorizonRadio.Source.YouTube;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// An <see cref="IContentPlayer"/> rejects a locator it can't play with a user-facing
/// message before doing any work (no yt-dlp run, no directory walk on a missing path).
/// </summary>
public class ContentPlayerTests
{
    [Fact]
    public async Task Local_enumerate_empty_locator_throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new LocalContentPlayer().EnumerateAsync(new ContentRef("local", ""), default));
        Assert.Contains("pick a music folder", ex.Message);
    }

    [Fact]
    public async Task Local_enumerate_missing_path_throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), "hzn-missing-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new LocalContentPlayer().EnumerateAsync(new ContentRef("local", missing), default));
        Assert.Contains("doesn't exist", ex.Message);
    }

    [Fact]
    public async Task YouTube_enumerate_blank_locator_throws_without_running_yt_dlp()
    {
        // Placeholder tool paths: a blank URL must be rejected before yt-dlp is invoked.
        var player = new YouTubeContentPlayer("yt-dlp", "ffmpeg", normalise: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => player.EnumerateAsync(new ContentRef("youtube", "  "), default));
        Assert.Contains("video or playlist URL", ex.Message);
    }
}

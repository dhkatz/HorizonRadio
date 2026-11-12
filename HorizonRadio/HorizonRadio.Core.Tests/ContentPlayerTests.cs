using HorizonRadio.Sdk.Sources;
using HorizonRadio.Source.Local;
using HorizonRadio.Source.YouTube;
using HorizonRadio.Tools.YtDlp;

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
        // Placeholder tool paths, and no process may start: a blank URL is rejected before yt-dlp runs.
        var tools = new YouTubeTools(new YtDlpClient(NoProcesses.Instance, NoLog.Instance), "yt-dlp", "ffmpeg",
            NoProcesses.Instance, NoLog.Instance);
        var player = new YouTubeContentPlayer(tools, normalise: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => player.EnumerateAsync(new ContentRef("youtube", "  "), default));
        Assert.Contains("video or playlist URL", ex.Message);
    }
}

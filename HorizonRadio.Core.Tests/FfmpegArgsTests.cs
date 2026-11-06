using HorizonRadio.Tools.FFmpeg;

namespace HorizonRadio.Core.Tests;

public class FfmpegArgsTests
{
    [Fact]
    public void Url_decode_starts_from_the_top_by_default()
    {
        var args = Ffmpeg.BuildUrlDecodeArgs("https://example/stream", normalise: false);
        Assert.DoesNotContain("-ss", args);
        Assert.Equal("https://example/stream", args[Array.IndexOf(args, "-i") + 1]);
    }

    // Resuming mid-track: an input seek, so it must come before -i.
    [Fact]
    public void Url_decode_can_start_mid_track()
    {
        var args = Ffmpeg.BuildUrlDecodeArgs("https://example/stream", normalise: false, TimeSpan.FromSeconds(83.25));
        var ss = Array.IndexOf(args, "-ss");
        Assert.True(ss >= 0 && ss < Array.IndexOf(args, "-i"));
        Assert.Equal("83.25", args[ss + 1]);
    }
}

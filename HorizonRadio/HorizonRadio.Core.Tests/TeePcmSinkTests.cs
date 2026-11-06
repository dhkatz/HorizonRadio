using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Tests;

public class TeePcmSinkTests
{
    private sealed class RecordingSink(bool accept = true) : IPcmSink
    {
        public int Chunks { get; private set; }

        public bool Send(ReadOnlySpan<short> samples)
        {
            Chunks++;
            return accept;
        }
    }

    private static readonly short[] Chunk = [1, 2, 3, 4];

    [Fact]
    public void Routes_to_primary_and_preview()
    {
        var primary = new RecordingSink();
        var preview = new RecordingSink();
        var tee = new TeePcmSink(primary);
        tee.AttachPreview(preview);

        Assert.True(tee.Send(Chunk));
        Assert.Equal(1, primary.Chunks);
        Assert.Equal(1, preview.Chunks);
    }

    [Fact]
    public void Gated_primary_is_skipped_but_preview_still_counts_as_sent()
    {
        var primary = new RecordingSink();
        var preview = new RecordingSink();
        var tee = new TeePcmSink(primary);
        tee.AttachPreview(preview);
        tee.SetPrimaryEnabled(false);

        Assert.True(tee.Send(Chunk));
        Assert.Equal(0, primary.Chunks);
        Assert.Equal(1, preview.Chunks);
    }

    // A game integration with no in-game audio (e.g. "no game") has no primary sink.
    [Fact]
    public void Without_a_primary_only_the_preview_receives_audio()
    {
        var preview = new RecordingSink();
        var tee = new TeePcmSink(primary: null);

        Assert.False(tee.Send(Chunk)); // nowhere to send yet
        tee.AttachPreview(preview);
        Assert.True(tee.Send(Chunk));
        Assert.Equal(1, preview.Chunks);
    }
}

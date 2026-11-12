using HorizonRadio.Core.Audio;
using HorizonRadio.Sdk.Sources;

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
        var tee = new TeePcmSink(primary) { Route = new TeeRoute(true, preview) };

        Assert.True(tee.Send(Chunk));
        Assert.Equal(1, primary.Chunks);
        Assert.Equal(1, preview.Chunks);
    }

    [Fact]
    public void Gated_primary_is_skipped_but_preview_still_counts_as_sent()
    {
        var primary = new RecordingSink();
        var preview = new RecordingSink();
        var tee = new TeePcmSink(primary) { Route = TeeRoute.PreviewOnly(preview) };

        Assert.True(tee.Send(Chunk));
        Assert.Equal(0, primary.Chunks);
        Assert.Equal(1, preview.Chunks);
    }

    [Fact]
    public void Routing_nowhere_drops_the_chunk()
    {
        var primary = new RecordingSink();
        var tee = new TeePcmSink(primary) { Route = TeeRoute.Nowhere };

        Assert.False(tee.Send(Chunk));
        Assert.Equal(0, primary.Chunks);
    }

    // A game integration with no in-game audio (e.g. "no game") has no primary sink.
    [Fact]
    public void Without_a_primary_only_the_preview_receives_audio()
    {
        var preview = new RecordingSink();
        var tee = new TeePcmSink(primary: null);

        Assert.False(tee.Send(Chunk)); // nowhere to send yet
        tee.Route = new TeeRoute(true, preview);
        Assert.True(tee.Send(Chunk));
        Assert.Equal(1, preview.Chunks);
    }
}

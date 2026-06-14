using System.Text;
using HorizonRadio.Core.Sources.Radio;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The ICY (SHOUTcast/Icecast) de-interleave: audio bytes must pass through byte-exact
/// while the in-band StreamTitle metadata is lifted out and parsed — including the
/// no-metadata pass-through and the "no change this round" (zero-length) case.
/// </summary>
public class IcyStreamReaderTests
{
    // Build a stream of [metaInt audio][1 len byte][len*16 metadata] blocks.
    private static byte[] BuildIcyStream(int metaInt, params (byte[] audio, string? title)[] blocks)
    {
        using var ms = new MemoryStream();
        foreach (var (audio, title) in blocks)
        {
            ms.Write(audio, 0, audio.Length);
            if (title is null)
            {
                ms.WriteByte(0); // length byte 0 → no metadata this round
            }
            else
            {
                var payload = Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
                int padded = (payload.Length + 15) / 16 * 16; // pad up to a 16-byte multiple
                ms.WriteByte((byte)(padded / 16));
                ms.Write(payload, 0, payload.Length);
                for (int i = payload.Length; i < padded; i++) ms.WriteByte(0);
            }
        }
        return ms.ToArray();
    }

    private static byte[] Audio(int len, byte fill)
    {
        var a = new byte[len];
        Array.Fill(a, fill);
        return a;
    }

    [Fact]
    public async Task Strips_metadata_and_passes_audio_through_byte_exact()
    {
        int metaInt = 8;
        var a1 = Audio(8, 0x11);
        var a2 = Audio(8, 0x22);
        var src = new MemoryStream(BuildIcyStream(metaInt,
            (a1, "Daft Punk - Get Lucky"),
            (a2, null)));
        var target = new MemoryStream();
        var titles = new List<string>();

        await IcyStreamReader.PumpInterleavedAsync(src, metaInt, target,
            meta => { var t = IcyStreamReader.ExtractStreamTitle(Encoding.UTF8.GetString(meta).TrimEnd('\0')); if (t != null) titles.Add(t); },
            CancellationToken.None);

        // Audio is exactly a1 + a2, with no metadata bytes leaking in.
        Assert.Equal(a1.Concat(a2).ToArray(), target.ToArray());
        Assert.Equal(["Daft Punk - Get Lucky"], titles);
    }

    [Fact]
    public async Task No_metaint_copies_stream_through_unchanged()
    {
        var payload = Audio(5000, 0x7F);
        var src = new MemoryStream(payload);
        var target = new MemoryStream();

        await IcyStreamReader.PumpInterleavedAsync(src, metaInt: 0, target,
            _ => Assert.Fail("no metadata expected when metaInt is 0"), CancellationToken.None);

        Assert.Equal(payload, target.ToArray());
    }

    [Theory]
    [InlineData("StreamTitle='Artist - Song';StreamUrl='http://x';", "Artist - Song")]
    [InlineData("StreamTitle='Only Title';", "Only Title")]
    [InlineData("StreamTitle='Has ; semicolon';", "Has ; semicolon")]
    [InlineData("StreamUrl='http://x';", null)]
    [InlineData("", null)]
    public void ExtractStreamTitle_parses_the_title(string meta, string? expected)
        => Assert.Equal(expected, IcyStreamReader.ExtractStreamTitle(meta));
}

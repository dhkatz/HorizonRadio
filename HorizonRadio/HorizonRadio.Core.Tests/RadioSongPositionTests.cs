using HorizonRadio.Core.Lyrics.Lrclib;
using HorizonRadio.Core.Sources.Radio;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Radio lyrics follow the song from where its title change falls in the audio: the byte offset
/// of the title, converted to time by the stream's rate.
/// </summary>
public class StreamClockTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void A_constant_bitrate_header_places_bytes_in_time()
    {
        var clock = new StreamClock(headerKbps: 128); // 16,000 bytes a second

        Assert.Equal(S(10), clock.PlaysAt(160_000));
    }

    [Fact]
    public void Without_a_header_the_rate_is_measured_once_the_pipes_have_filled()
    {
        var clock = new StreamClock(headerKbps: null);
        // The server's burst: lots of bytes, little decoded yet. Not measured.
        clock.Sample(200_000, S(1));
        Assert.Null(clock.PlaysAt(1));

        // Steady state: 12,000 bytes a second, read 3 s ahead of what's decoded.
        for (int t = 5; t <= 30; t++) clock.Sample(12_000L * (t + 3), S(t));

        Assert.Equal(12_000, clock.Rate!.Value, 1);
    }

    [Fact]
    public void A_nominal_header_on_a_variable_bitrate_stream_gives_way_to_the_measurement()
    {
        var clock = new StreamClock(headerKbps: 128); // says 16,000 B/s
        for (int t = 5; t <= 30; t++) clock.Sample(14_000L * (t + 2), S(t)); // runs at 14,000

        Assert.Equal(14_000, clock.Rate!.Value, 1);
    }

    [Fact]
    public void A_header_within_a_few_percent_of_the_measurement_is_kept()
    {
        var clock = new StreamClock(headerKbps: 128);
        for (int t = 5; t <= 30; t++) clock.Sample(16_200L * t, S(t)); // jitter around 16,000

        Assert.Equal(16_000, clock.Rate!.Value, 1);
    }
}

public class SongPositionTests
{
    private sealed class Item(TimeSpan? duration, TimeSpan position) : PlayableItem
    {
        public Item Init() { Duration = duration; return this; }
        public override TimeSpan Position => position;
        public override Task PlayAsync(PumpContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void A_track_with_a_length_is_one_song()
    {
        Assert.Equal(TimeSpan.FromSeconds(42), new Item(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(42)).Init().SongPosition);
        Assert.Null(new Item(null, TimeSpan.FromSeconds(42)).Init().SongPosition); // a live stream can't tell by default
    }
}

public class LrclibRadioPickTests
{
    private static LrclibProvider.Record Rec(double seconds, string word) =>
        new("Song", "Band", seconds, Instrumental: false, null,
            string.Join('\n', Enumerable.Range(0, (int)(seconds * 0.9 / 10)).Select(i => $"[{i * 10 / 60:00}:{i * 10 % 60:00}.00]{word} {i}")));

    [Fact]
    public void Radio_takes_the_cut_most_uploads_agree_on()
    {
        // One extended mix first, then three uploads of the standard cut.
        var picked = LrclibProvider.Pick(
            [Rec(420, "extended"), Rec(200, "standard"), Rec(201, "standard"), Rec(200, "standard")],
            new LyricsQuery("Song", "Band"));

        Assert.StartsWith("standard", picked!.Lines[0].Text, StringComparison.Ordinal);
    }
}

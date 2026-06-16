using HorizonRadio.Core.Audio;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The cubic perceptual taper that maps the volume slider's position to a linear
/// gain, so the fader eases down smoothly instead of staying loud until the bottom.
/// </summary>
public class VolumeTaperTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.125)]      // 0.5³ — half travel is already ~-18 dB
    [InlineData(0.75, 0.421875)]  // the shipped default position
    public void ToGain_is_cubic(double position, double expected)
        => Assert.Equal(expected, VolumeTaper.ToGain(position), 5);

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(2.0, 1.0)]
    public void ToGain_clamps_out_of_range(double position, double expected)
        => Assert.Equal(expected, VolumeTaper.ToGain(position), 5);

    [Fact]
    public void ToGain_maps_NaN_to_silence()
        => Assert.Equal(0f, VolumeTaper.ToGain(double.NaN));

    [Fact]
    public void ToGain_is_monotonic_increasing()
    {
        var prev = -1f;
        for (var p = 0.0; p <= 1.0; p += 0.05)
        {
            var g = VolumeTaper.ToGain(p);
            Assert.True(g >= prev, $"gain dropped at position {p}");
            prev = g;
        }
    }
}

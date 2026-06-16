using System;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// Maps a volume <em>slider position</em> (0..1, what the user drags and what we
/// persist) to a <em>linear gain</em> (0..1, what we multiply samples by).
///
/// A linear fader feels broken: perceived loudness is roughly logarithmic, so a
/// straight position→gain map keeps almost all of the audible attenuation in the
/// bottom sliver of travel — the slider sounds "loud until it suddenly isn't."
/// We use a cubic taper (gain = position³), the same approximation most media
/// players use: the top of the slider is fine-grained and it eases down to
/// silence smoothly. At 50% travel you're at gain 0.125 (≈ -18 dB); at 0 you're
/// truly silent.
/// </summary>
public static class VolumeTaper
{
    /// <summary>Convert a slider position in [0,1] to a linear gain in [0,1].
    /// Out-of-range input is clamped; NaN maps to silence (Math.Clamp would
    /// propagate it, and a NaN gain poisons every downstream sample).</summary>
    public static float ToGain(double position)
    {
        if (double.IsNaN(position)) return 0f;
        var p = Math.Clamp(position, 0.0, 1.0);
        return (float)(p * p * p);
    }
}

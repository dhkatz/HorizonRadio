using System.Numerics;

namespace HorizonRadio.Core.Fingerprints;

/// <summary>Where a stretch of audio fits in a recording, by their fingerprints.</summary>
/// <param name="Offset">The recording's word that the stretch's first word lines up with (negative
/// when the stretch starts before the recording does).</param>
/// <param name="ErrorRate">The share of fingerprint bits that differ where they overlap there: near 0
/// for the same audio, about 0.5 for unrelated audio.</param>
/// <param name="RunnerUp">The error rate of the best fit anywhere else (not right next to
/// <paramref name="Offset"/>). Close to <paramref name="ErrorRate"/> means the stretch fits two places
/// about as well, like a chorus that repeats.</param>
/// <param name="Overlap">How many words overlapped at <paramref name="Offset"/>.</param>
public readonly record struct FingerprintMatch(int Offset, double ErrorRate, double RunnerUp, int Overlap)
{
    /// <summary>How much better the best fit is than the next best elsewhere.</summary>
    public double Margin => RunnerUp - ErrorRate;

    /// <summary>Where in the recording the stretch starts.</summary>
    public TimeSpan ReferenceTime => Chromaprint.FrameStep * Offset;
}

/// <summary>Finds where a stretch of audio fits in a recording by sliding one fingerprint along the
/// other and counting the bits that differ.</summary>
public static class FingerprintAligner
{
    /// <summary>Fits this close to the best (in words) are the same fit, a frame either way.</summary>
    public const int Neighbourhood = 4;

    /// <summary>The best fit of <paramref name="live"/> in <paramref name="reference"/> where at least
    /// <paramref name="minOverlap"/> words overlap, or null when they can't overlap that much.</summary>
    public static FingerprintMatch? Align(ReadOnlySpan<uint> reference, ReadOnlySpan<uint> live, int minOverlap)
    {
        minOverlap = Math.Max(1, minOverlap);
        if (reference.Length < minOverlap || live.Length < minOverlap) return null;

        int first = minOverlap - live.Length, last = reference.Length - minOverlap;
        var rates = new double[last - first + 1];
        var overlaps = new int[rates.Length];
        int best = -1;
        for (int k = first; k <= last; k++)
        {
            // Live word i lines up with reference word i + k.
            int from = Math.Max(0, -k), to = Math.Min(live.Length, reference.Length - k);
            int errors = 0;
            for (int i = from; i < to; i++) errors += BitOperations.PopCount(live[i] ^ reference[i + k]);
            int n = to - from;
            rates[k - first] = errors / (32.0 * n);
            overlaps[k - first] = n;
            if (best < 0 || rates[k - first] < rates[best]) best = k - first;
        }

        double runnerUp = 1;
        for (int j = 0; j < rates.Length; j++)
            if (Math.Abs(j - best) > Neighbourhood && rates[j] < runnerUp) runnerUp = rates[j];

        return new FingerprintMatch(best + first, rates[best], runnerUp, overlaps[best]);
    }

    /// <summary>The error rate with <paramref name="live"/>'s first word lined up with
    /// <paramref name="reference"/>'s word <paramref name="offset"/>; 1 when they don't overlap.</summary>
    public static double ErrorRateAt(ReadOnlySpan<uint> reference, ReadOnlySpan<uint> live, int offset)
    {
        int from = Math.Max(0, -offset), to = Math.Min(live.Length, reference.Length - offset);
        if (to <= from) return 1;
        int errors = 0;
        for (int i = from; i < to; i++) errors += BitOperations.PopCount(live[i] ^ reference[i + offset]);
        return errors / (32.0 * (to - from));
    }
}

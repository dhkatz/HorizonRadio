namespace HorizonRadio.Core.Fingerprints;

/// <summary>
/// The silence around a recording. Uploads of the same song pad it differently (a video's title card
/// can add seconds of silence before the music), so a song's time is counted from its first sound,
/// not from the start of whichever file it was matched against: lyrics are timed to the music.
/// </summary>
public static class Silence
{
    /// <summary>Quieter than this (dBFS, over 10 ms) is silence. Measured on uploads of the same songs,
    /// the music starts sharply above it, and the lead-ins this finds account for the difference
    /// between where each upload places the song to within 0.01 s.</summary>
    private const double ThresholdDb = -45;

    private static readonly double Threshold = short.MaxValue * Math.Pow(10, ThresholdDb / 20);

    /// <summary>How long <paramref name="mono"/> is silent for before its first sound (all of it when
    /// it's silent throughout).</summary>
    public static TimeSpan Before(ReadOnlySpan<short> mono, int sampleRate)
    {
        int window = Math.Max(1, sampleRate / 100);
        for (int at = 0; at + window <= mono.Length; at += window)
            if (Loud(mono.Slice(at, window))) return TimeSpan.FromSeconds((double)at / sampleRate);
        return TimeSpan.FromSeconds((double)mono.Length / sampleRate);
    }

    /// <summary>How long <paramref name="mono"/> is silent for after its last sound.</summary>
    public static TimeSpan After(ReadOnlySpan<short> mono, int sampleRate)
    {
        int window = Math.Max(1, sampleRate / 100);
        for (int end = mono.Length; end - window >= 0; end -= window)
            if (Loud(mono.Slice(end - window, window))) return TimeSpan.FromSeconds((double)(mono.Length - end) / sampleRate);
        return TimeSpan.FromSeconds((double)mono.Length / sampleRate);
    }

    private static bool Loud(ReadOnlySpan<short> window)
    {
        double sum = 0;
        foreach (var s in window) sum += (double)s * s;
        return Math.Sqrt(sum / window.Length) > Threshold;
    }
}

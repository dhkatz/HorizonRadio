using System.Numerics;

namespace HorizonRadio.Core.Fingerprints;

/// <summary>What lining a stretch of live audio up against a recording found.</summary>
public enum MatchVerdict
{
    /// <summary>The audio is this recording, at <see cref="RecordingFit.SongTime"/>.</summary>
    Matched,

    /// <summary>The audio is this recording but fits more than one place in it (a chorus that
    /// repeats); more audio will tell.</summary>
    Ambiguous,

    /// <summary>The audio isn't this recording.</summary>
    Different,

    /// <summary>Neither clearly the recording nor clearly not; more audio will tell.</summary>
    Unsure,
}

/// <summary>Where a stretch of live audio fits in a recording: the audio at <paramref name="StreamTime"/>
/// on the stream's clock is <paramref name="SongTime"/> into the recording.</summary>
public readonly record struct RecordingFit(
    MatchVerdict Verdict, TimeSpan StreamTime, TimeSpan SongTime, double ErrorRate, double Margin);

/// <summary>
/// Lines a stretch of a station's audio up against a recording of the song by their fingerprints.
/// <para>
/// The thresholds come from a real station (Vocaloid Radio, 320 kbit/s MP3) against YouTube and
/// Niconico uploads of its songs. Where the station plays the recording, 1–8% of fingerprint bits
/// differ over 20 s (up to 11% over the first 12 s). Another version of the song (a re-upload with
/// its own mix, a short edit) fits at 13–18%, placing the song a few tenths of a second off. Against
/// other songs, the best fit anywhere still has 35% or more differing, even over 8 s. A song with a
/// repeated section fits two places about equally (within 1%), which <see cref="MinMargin"/> catches.
/// </para>
/// </summary>
public static class RecordingMatch
{
    /// <summary>At most this share of bits may differ for the audio to be the song (this recording, or
    /// another version of it).</summary>
    public const double MaxErrorRate = 0.2;

    /// <summary>At most this share of bits differs, over <see cref="Conclusive"/> of audio, when the
    /// station plays this very recording rather than another version of the song.</summary>
    public const double ExactErrorRate = 0.1;

    /// <summary>The best fit must beat every fit elsewhere by this much to be the place.</summary>
    public const double MinMargin = 0.05;

    /// <summary>With this share of bits differing over <see cref="Conclusive"/> of audio, it isn't the recording.</summary>
    public const double DifferentErrorRate = 0.3;

    /// <summary>How much audio a verdict on which recording it is needs.</summary>
    public static readonly TimeSpan Conclusive = TimeSpan.FromSeconds(20);

    /// <summary>The least overlap a fit counts over (about 7.5 s): short overlaps of unrelated audio
    /// can fit by chance.</summary>
    public const int MinOverlapWords = 60;

    /// <summary>Steps per fingerprint frame tried when refining a fit: a frame is ~0.124 s, so 8 places
    /// it to ~0.015 s.</summary>
    private const int Refinements = 8;

    /// <summary>Fingerprint words (about 2 s) the station must stay on the recording for, from where
    /// its version of the song is taken to start.</summary>
    private const int SettledWords = 16;

    /// <summary>Where <paramref name="audio"/> (mono at <see cref="Chromaprint.SampleRate"/>, its first
    /// sample at <paramref name="start"/> on the stream's clock) fits in the recording whose fingerprint
    /// is <paramref name="reference"/>.</summary>
    public static RecordingFit Locate(uint[] reference, short[] audio, TimeSpan start)
    {
        var live = Chromaprint.Compute(audio);
        // Half the stretch may hang off either end of the recording: the station's audio from just
        // before the song began (the previous song's end) or after it ended counts for nothing.
        var match = FingerprintAligner.Align(reference, live, Math.Max(MinOverlapWords, live.Length / 2));
        if (match is not { } m) return new RecordingFit(MatchVerdict.Unsure, start, TimeSpan.Zero, 1, 0);

        var length = TimeSpan.FromSeconds((double)audio.Length / Chromaprint.SampleRate);
        if (m.ErrorRate > MaxErrorRate)
        {
            var verdict = m.ErrorRate >= DifferentErrorRate && length >= Conclusive ? MatchVerdict.Different : MatchVerdict.Unsure;
            return new RecordingFit(verdict, start, m.ReferenceTime, m.ErrorRate, m.Margin);
        }
        if (m.Margin < MinMargin)
            return new RecordingFit(MatchVerdict.Ambiguous, start, m.ReferenceTime, m.ErrorRate, m.Margin);

        // The fit is to the nearest frame. Fingerprint the audio again from a little later each time,
        // and keep the start that lines up best: frames then fall where the recording's do.
        int bestShift = 0, bestOffset = m.Offset;
        double bestRate = m.ErrorRate;
        for (int r = 1; r < Refinements; r++)
        {
            int shift = r * Chromaprint.FrameStepSamples / Refinements;
            var shifted = Chromaprint.Compute(audio.AsSpan(shift));
            for (int k = m.Offset - 1; k <= m.Offset + 1; k++)
            {
                var rate = FingerprintAligner.ErrorRateAt(reference, shifted, k);
                if (rate < bestRate)
                {
                    bestRate = rate;
                    bestShift = shift;
                    bestOffset = k;
                }
            }
        }

        return new RecordingFit(
            MatchVerdict.Matched,
            start + TimeSpan.FromSeconds((double)bestShift / Chromaprint.SampleRate),
            Chromaprint.FrameStep * bestOffset,
            bestRate,
            m.Margin);
    }

    /// <summary>
    /// Where in the recording the station's audio settles on it, from <paramref name="audio"/> (first
    /// sample at <paramref name="start"/>) that runs from before the song through its start, and where
    /// the song was placed (<paramref name="placed"/>). That's not quite where the song starts: stations
    /// crossfade, so the previous song runs over the new one's first seconds (0.7–3.3 s, measured), and
    /// it's found a little late. It's far later when the recording opens with a prelude the station's
    /// version doesn't have (a music video's scene before the song). Null when the station already plays
    /// the recording from the stretch's first moment (the song began before it), or never settles on it.
    /// </summary>
    public static TimeSpan? StationSettles(uint[] reference, short[] audio, TimeSpan start, RecordingFit placed)
    {
        var live = Chromaprint.Compute(audio);
        // The recording's word lined up with the stretch's first word, by the placement.
        var at = placed.SongTime + (start - placed.StreamTime);
        int offset = (int)Math.Round(at.TotalSeconds / Chromaprint.FrameStep.TotalSeconds);

        var errors = new int[live.Length];
        for (int i = 0; i < live.Length; i++)
        {
            int j = i + offset;
            errors[i] = j >= 0 && j < reference.Length ? BitOperations.PopCount(live[i] ^ reference[j]) : 32;
        }

        // The first word from which two seconds fit closely, and the two after still fit.
        double close = MaxErrorRate * 32 * SettledWords, still = DifferentErrorRate * 32 * SettledWords;
        for (int i = 0; i + 2 * SettledWords <= errors.Length; i++)
        {
            if (Sum(errors, i, SettledWords) > close || Sum(errors, i + SettledWords, SettledWords) > still) continue;
            return i == 0 ? null : Chromaprint.FrameStep * (i + offset);
        }
        return null;
    }

    private static int Sum(int[] values, int from, int count)
    {
        int sum = 0;
        for (int i = from; i < from + count; i++) sum += values[i];
        return sum;
    }
}

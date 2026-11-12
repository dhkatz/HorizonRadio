using HorizonRadio.Sdk.Audio;

namespace HorizonRadio.Core.Fingerprints;

/// <summary>
/// The last few minutes of a live stream's audio, kept to line up against a recording of the song.
/// Takes the engine's PCM as it plays (stereo at <see cref="AudioFormat.SampleRate"/>) and keeps it
/// mono at <see cref="Chromaprint.SampleRate"/>, low-passed before the rate drops so nothing folds
/// back into the band the fingerprint looks at. The stream's clock is the audio written: the sample
/// written <c>t</c> into the stream has stream time <c>t</c>.
/// </summary>
public sealed class LiveAudioBuffer
{
    private static readonly int Factor = AudioFormat.SampleRate / Chromaprint.SampleRate;
    private static readonly float[] Taps = LowPass(Factor);

    private readonly Lock _lock = new();
    private readonly short[] _ring;
    private long _written; // mono samples written since the stream (re)started

    // The low-pass filter's input history (mono, at the engine's rate) and where the next output falls.
    private readonly float[] _history = new float[Taps.Length];
    private int _historyAt;
    private int _phase;

    static LiveAudioBuffer()
    {
        if (AudioFormat.SampleRate % Chromaprint.SampleRate != 0 || AudioFormat.Channels != 2)
            throw new InvalidOperationException("the engine's PCM format doesn't divide down to Chromaprint's");
    }

    public LiveAudioBuffer(TimeSpan capacity) =>
        _ring = new short[(int)(capacity.TotalSeconds * Chromaprint.SampleRate)];

    /// <summary>How much of the stream has been written: the stream time of its end.</summary>
    public TimeSpan Written
    {
        get { lock (_lock) return Time(_written); }
    }

    /// <summary>Add the engine's interleaved stereo PCM.</summary>
    public void Write(ReadOnlySpan<short> stereo)
    {
        lock (_lock)
        {
            for (int i = 0; i + 1 < stereo.Length; i += 2)
            {
                _history[_historyAt] = (stereo[i] + stereo[i + 1]) * 0.5f;
                _historyAt = (_historyAt + 1) % _history.Length;
                if (++_phase < Factor) continue;
                _phase = 0;

                // Convolve the filter with the newest samples (oldest tap first).
                float acc = 0;
                int h = _historyAt;
                for (int t = 0; t < Taps.Length; t++)
                {
                    acc += Taps[t] * _history[h];
                    h = h + 1 == _history.Length ? 0 : h + 1;
                }
                _ring[_written % _ring.Length] = (short)Math.Clamp(MathF.Round(acc), short.MinValue, short.MaxValue);
                _written++;
            }
        }
    }

    /// <summary>The audio from <paramref name="from"/> (or as far back as it's kept) to the end, at
    /// most the last <paramref name="span"/> of it, and the stream time of its first sample; null when
    /// there's none.</summary>
    public (short[] Samples, TimeSpan Start)? Read(TimeSpan from, TimeSpan span)
    {
        lock (_lock)
        {
            return Copy(Math.Max(FirstAt(from), _written - Samples(span)), _written);
        }
    }

    /// <summary>The audio between <paramref name="start"/> and <paramref name="end"/>, as much of it as
    /// is kept and has been written; null when there's none.</summary>
    public (short[] Samples, TimeSpan Start)? ReadBetween(TimeSpan start, TimeSpan end)
    {
        lock (_lock)
        {
            return Copy(FirstAt(start), Math.Min(_written, FirstAt(end)));
        }
    }

    // Samples [first, end) of what's still kept. Call under _lock.
    private (short[] Samples, TimeSpan Start)? Copy(long first, long end)
    {
        first = Math.Max(first, Math.Max(0, _written - _ring.Length));
        if (first >= end) return null;
        var samples = new short[end - first];
        for (long s = first; s < end; s++) samples[s - first] = _ring[s % _ring.Length];
        return (samples, Time(first));
    }

    /// <summary>Forget the audio: the stream starts over (a reconnect).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _written = 0;
            _phase = 0;
            Array.Clear(_history);
        }
    }

    private static TimeSpan Time(long samples) => TimeSpan.FromSeconds((double)samples / Chromaprint.SampleRate);

    private static long Samples(TimeSpan time) => (long)Math.Max(0, time.TotalSeconds * Chromaprint.SampleRate);

    // The first sample at or after <paramref name="time"/>.
    private static long FirstAt(TimeSpan time) => (long)Math.Max(0, Math.Ceiling(time.TotalSeconds * Chromaprint.SampleRate - 1e-6));

    // A windowed-sinc low-pass that keeps what Chromaprint looks at (it reads up to ~3.5 kHz) and
    // stops short of the new rate's Nyquist frequency.
    private static float[] LowPass(int factor)
    {
        const int Length = 48;
        double cutoff = 0.45 / factor; // of the input rate
        var taps = new float[Length];
        double sum = 0;
        for (int i = 0; i < Length; i++)
        {
            double x = i - (Length - 1) / 2.0;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (Length - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (Length - 1));
            taps[i] = (float)(sinc * window);
            sum += taps[i];
        }
        for (int i = 0; i < Length; i++) taps[i] = (float)(taps[i] / sum);
        return taps;
    }
}

using System.Buffers.Binary;
using HorizonRadio.Core.Audio;
using NAudio.Dsp;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>The sample format the tap announces (Native/include/horizon/tap/protocol.hpp).</summary>
public readonly record struct AudioTapFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    public int BlockAlign => Channels * (BitsPerSample / 8);
}

/// <summary>
/// Turns what the tap sends (the process's own format: usually 48 kHz float, any channel count)
/// into the pipeline's s16 stereo at 44.1 kHz (<see cref="AudioFormat"/>). Stereo is the first two
/// channels; mono plays on both. Feed whole frames; a new format starts the resampler afresh.
/// </summary>
public sealed class AudioTapConverter
{
    private AudioTapFormat? _format;
    private WdlResampler? _resampler;
    private float[] _out = [];
    private short[] _pcm = [];

    public AudioTapFormat? Format => _format;

    public void SetFormat(AudioTapFormat format)
    {
        if (format.Channels <= 0 || format.SampleRate <= 0
            || (format.IsFloat ? format.BitsPerSample != 32 : format.BitsPerSample is not (16 or 24 or 32)))
            throw new ArgumentException($"Unsupported tap format: {format}", nameof(format));
        _format = format;
        _resampler = null;
        if (format.SampleRate != AudioFormat.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true); // input-driven: we push what arrives
            _resampler.SetRates(format.SampleRate, AudioFormat.SampleRate);
        }
    }

    /// <summary>Convert one block. <paramref name="emit"/> gets s16 stereo samples (possibly none,
    /// while the resampler fills).</summary>
    public void Convert(ReadOnlySpan<byte> block, Action<ReadOnlySpan<short>> emit)
    {
        if (_format is not { } f) throw new InvalidOperationException("Audio before a format.");
        var frames = block.Length / f.BlockAlign;
        if (frames == 0) return;

        if (_resampler == null)
        {
            EnsureCapacity(ref _out, frames * 2);
            Decode(block, frames, f, _out, 0);
            Emit(_out, frames, emit);
            return;
        }

        _resampler.ResamplePrepare(frames, 2, out var inBuffer, out var inOffset);
        Decode(block, frames, f, inBuffer, inOffset);
        var maxOut = (int)((long)frames * AudioFormat.SampleRate / f.SampleRate) + 32;
        EnsureCapacity(ref _out, maxOut * 2);
        var got = _resampler.ResampleOut(_out, 0, frames, maxOut, 2);
        if (got > 0) Emit(_out, got, emit);
    }

    // Interleaved stereo floats into dest at offset.
    private static void Decode(ReadOnlySpan<byte> block, int frames, AudioTapFormat f, float[] dest, int offset)
    {
        var bytesPerSample = f.BitsPerSample / 8;
        for (var i = 0; i < frames; i++)
        {
            var frame = block.Slice(i * f.BlockAlign, f.BlockAlign);
            var left = Sample(frame, 0, bytesPerSample, f.IsFloat);
            var right = f.Channels > 1 ? Sample(frame, bytesPerSample, bytesPerSample, f.IsFloat) : left;
            dest[offset + (2 * i)] = left;
            dest[offset + (2 * i) + 1] = right;
        }
    }

    private static float Sample(ReadOnlySpan<byte> frame, int at, int size, bool isFloat)
    {
        var s = frame.Slice(at, size);
        if (isFloat) return BinaryPrimitives.ReadSingleLittleEndian(s);
        return size switch
        {
            2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
            3 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
        };
    }

    private void Emit(float[] samples, int frames, Action<ReadOnlySpan<short>> emit)
    {
        var count = frames * 2;
        EnsureCapacity(ref _pcm, count);
        for (var i = 0; i < count; i++)
            _pcm[i] = (short)Math.Clamp(MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
        emit(_pcm.AsSpan(0, count));
    }

    private static void EnsureCapacity<T>(ref T[] buffer, int size)
    {
        if (buffer.Length < size) buffer = new T[size];
    }

    /// <summary>A Format message's payload.</summary>
    public static AudioTapFormat ParseFormat(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) throw new InvalidDataException("Short tap format message.");
        return new AudioTapFormat(
            SampleRate: (int)BinaryPrimitives.ReadUInt32LittleEndian(payload),
            Channels: BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]),
            BitsPerSample: BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]),
            IsFloat: BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) != 0);
    }
}

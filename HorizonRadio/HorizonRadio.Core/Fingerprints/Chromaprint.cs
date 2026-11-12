using AcoustID;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Fingerprints;

/// <summary>
/// Audio fingerprints as AcoustID computes them (Chromaprint's default algorithm, through the managed
/// port in AcoustID.NET). A fingerprint is one 32-bit word per frame, each describing the pitch content
/// of a short stretch of audio; two recordings of the same song have nearly the same words where they
/// play the same music, whatever their loudness or encoding.
/// </summary>
public static class Chromaprint
{
    /// <summary>The rate Chromaprint analyses audio at. Audio at this rate, mono, is used as is.</summary>
    public const int SampleRate = LiveAudioBuffer.SampleRate;

    /// <summary>Samples between the starts of consecutive frames (a 4096-sample frame, overlapping the
    /// next by two thirds).</summary>
    public const int FrameStepSamples = 4096 / 3;

    /// <summary>How much audio one fingerprint word steps over: about 0.124 s.</summary>
    public static readonly TimeSpan FrameStep = TimeSpan.FromSeconds((double)FrameStepSamples / SampleRate);

    /// <summary>The fingerprint of <paramref name="mono"/>, audio at <see cref="SampleRate"/>. Word
    /// <c>i</c> describes the audio from <c>i * </c><see cref="FrameStep"/> on.</summary>
    public static uint[] Compute(ReadOnlySpan<short> mono)
    {
        var context = new ChromaContext();
        context.Start(SampleRate, 1);
        // Fed in blocks: the port copies what it's given.
        var block = new short[Math.Min(mono.Length, SampleRate * 10)];
        for (int at = 0; at < mono.Length; at += block.Length)
        {
            int n = Math.Min(block.Length, mono.Length - at);
            mono.Slice(at, n).CopyTo(block);
            context.Feed(block, n);
        }
        context.Finish();
        var raw = context.GetRawFingerprint() ?? [];
        var words = new uint[raw.Length];
        for (int i = 0; i < raw.Length; i++) words[i] = unchecked((uint)raw[i]);
        return words;
    }
}

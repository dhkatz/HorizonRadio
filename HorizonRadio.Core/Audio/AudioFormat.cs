namespace HorizonRadio.Core.Audio;

/// <summary>
/// The pipeline's canonical PCM format: s16 interleaved stereo at
/// 44.1 kHz. Every source resamples/converts to this before pushing
/// to the sink, and the DLL's FMOD bridge expects exactly this.
/// Defined here so downstream code can refer to a single constant
/// rather than scattering "44100" and "2" through the codebase.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 44100;
    public const int Channels   = 2;
    public const int BytesPerSample = 2;          // s16
    public const int BytesPerFrame  = Channels * BytesPerSample;
}

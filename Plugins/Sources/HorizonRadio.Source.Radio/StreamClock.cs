using System;

namespace HorizonRadio.Core.Sources.Radio;

/// <summary>
/// When a byte of a radio stream will be heard. The stream is read ahead of playback (the
/// network, ffmpeg's input pipe, and ffmpeg itself all buffer), so a new ICY title arrives some
/// seconds before its song starts playing. Every byte is decoded in order from the start of the
/// connection, so the byte at offset <c>b</c> plays at <c>b / rate</c> into the decoded audio.
/// <para>
/// The rate is the stream's bitrate: the <c>icy-br</c> header's when the stream really runs at
/// it (constant-bitrate MP3 almost always does), otherwise measured as audio arrives against
/// audio decoded, once the pipes have filled and the two move together.
/// </para>
/// </summary>
internal sealed class StreamClock
{
    /// <summary>Measure the rate only once the pipes have filled (the server's initial burst is
    /// over), and over at least this much decoded audio.</summary>
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(20);

    /// <summary>How far the measured rate may stray from the header's before the header is
    /// taken to be nominal (variable-bitrate AAC) and the measurement used instead.</summary>
    private const double HeaderTolerance = 0.03;

    private readonly double? _headerRate; // bytes per second
    private long _baseBytes = -1;
    private TimeSpan _baseDecoded;
    private long _lastBytes;
    private TimeSpan _lastDecoded;

    /// <param name="headerKbps">The <c>icy-br</c> header, in kbit/s, if the server sent one.</param>
    public StreamClock(int? headerKbps) =>
        _headerRate = headerKbps is > 0 ? headerKbps.Value * 1000.0 / 8 : null;

    /// <summary>Note how many audio bytes have been read and how much audio has been decoded.</summary>
    public void Sample(long bytesRead, TimeSpan decoded)
    {
        if (_baseBytes < 0)
        {
            if (decoded < WarmUp) return;
            _baseBytes = bytesRead;
            _baseDecoded = decoded;
        }
        _lastBytes = bytesRead;
        _lastDecoded = decoded;
    }

    /// <summary>The stream's rate in bytes per second, or null while it isn't known yet.</summary>
    public double? Rate
    {
        get
        {
            double? measured = _baseBytes >= 0 && _lastDecoded - _baseDecoded >= MinSpan
                ? (_lastBytes - _baseBytes) / (_lastDecoded - _baseDecoded).TotalSeconds
                : null;
            if (_headerRate is { } header)
                return measured is { } m && Math.Abs(m / header - 1) > HeaderTolerance ? m : header;
            return measured;
        }
    }

    /// <summary>How far into the decoded audio the byte at <paramref name="byteOffset"/> plays,
    /// or null while the rate isn't known.</summary>
    public TimeSpan? PlaysAt(long byteOffset) =>
        Rate is { } rate ? TimeSpan.FromSeconds(byteOffset / rate) : null;
}

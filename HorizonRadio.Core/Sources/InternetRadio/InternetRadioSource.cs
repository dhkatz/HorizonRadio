using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HorizonRadio.Core.Sources.InternetRadio;

/// <summary>
/// Streams audio from a plain internet radio URL (MP3 or Ogg/Vorbis stream)
/// and pushes s16-stereo 44.1 kHz PCM to the game via the standard
/// <see cref="IPcmSink"/> contract, paced to wall-clock so the DLL's
/// ring buffer never overflows.
///
/// <b>Audio decoding</b>:
/// <list type="bullet">
///   <item>MP3 — <see cref="Mp3FileReader"/> which accepts any readable
///   non-seekable stream (it does its own frame-sync internally).</item>
///   <item>OGG/Vorbis — NVorbis <c>VorbisReader</c> via NAudio.Vorbis,
///   also stream-capable.</item>
/// </list>
/// Both paths resample to 44.1 kHz stereo s16 before handing off to the sink.
///
/// <b>Real-time pacing</b>: pacing is based on the number of samples
/// actually decoded each iteration, not on an assumed full chunk size.
/// This is critical: without it the decoder runs at CPU speed and floods
/// the DLL ring buffer, crashing Forza with an OOM exception.
///
/// <b>Metadata</b>: AzuraCast SSE/REST via <see cref="InternetRadioMetadataPoller"/>
/// if a metadata URL is provided; otherwise ICY in-stream metadata
/// (<c>Icy-MetaInt</c> header) is stripped and parsed by <see cref="IcyMetaStream"/>.
///
/// <b>Reconnection</b>: on any stream error the source waits 3 s and
/// re-opens the HTTP connection, so brief hiccups don't kill the radio.
/// </summary>
public sealed class InternetRadioSource : IAudioSource
{
    public string Id          => "internet-radio";
    public string DisplayName => "Internet Radio";

    public event Action<Track>? TrackChanged;

    private readonly InternetRadioOptions _options;
    private CancellationTokenSource?      _stopCts;
    private Task?                         _runLoop;

    // Single shared HttpClient per process. Infinite timeout because
    // we're reading a never-ending streaming response body.
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    static InternetRadioSource()
    {
        _http.DefaultRequestHeaders.Add("Icy-MetaData", "1");
        _http.DefaultRequestHeaders.Add("User-Agent",   "HorizonRadio/1.0");
    }

    // Log to both Trace (DebugView) and a file so we can diagnose Release builds.
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HorizonRadio", "internet-radio.log");
    private static readonly object _logLock = new();

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [hzn-radio] {msg}";
        Trace.WriteLine(line);
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch { /* never let logging kill the audio thread */ }
    }

    public InternetRadioSource(InternetRadioOptions options) { _options = options; }

    // -----------------------------------------------------------------------
    // IAudioSource
    // -----------------------------------------------------------------------

    public Task StartAsync(IPcmSink sink, CancellationToken externalCt)
    {
        // Allow restart after a previous stop (runLoop completed).
        if (_runLoop != null && !_runLoop.IsCompleted) return Task.CompletedTask;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _runLoop = Task.Run(() => RunAsync(sink, _stopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopCts?.Cancel();
        if (_runLoop != null)
        {
            try { await _runLoop.ConfigureAwait(false); } catch { }
            _runLoop = null;
        }
        _stopCts?.Dispose();
        _stopCts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // -----------------------------------------------------------------------
    // Main loop — reconnects on error
    // -----------------------------------------------------------------------

    private async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        PublishPlaceholder();

        // Start external metadata poller if a metadata URL was configured.
        await using var poller = new InternetRadioMetadataPoller(
            _options.MetadataUrl, Id, DisplayName);
        poller.TrackChanged += (title, artist, artUrl) =>
            TrackChanged?.Invoke(new Track(
                Title:         title,
                Artist:        artist,
                Album:         null,
                AlbumArt:      null,
                SourceId:      Id,
                SourceDisplay: _options.StationName,
                ExternalId:    artUrl));
        poller.Start(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log($"connecting to {_options.StreamUrl}");
                await StreamOnceAsync(sink, ct).ConfigureAwait(false);
                Log("stream ended normally");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log($"stream error ({ex.GetType().Name}: {ex.Message}); reconnecting in 3 s");
                Log($"  stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    Log($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(3_000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Single HTTP connection + decode loop
    // -----------------------------------------------------------------------

    private async Task StreamOnceAsync(IPcmSink sink, CancellationToken ct)
    {
        using var req  = new HttpRequestMessage(HttpMethod.Get, _options.StreamUrl);
        using var resp = await _http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Icy-MetaInt: bytes of audio between each embedded metadata block.
        int icyMetaInt = 0;
        if (resp.Headers.TryGetValues("icy-metaint", out var metaIntValues))
            int.TryParse(System.Linq.Enumerable.FirstOrDefault(metaIntValues), out icyMetaInt);

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        Log($"connected; content-type={contentType}, icy-metaint={icyMetaInt}");

        // ReadAsStreamAsync returns the raw socket-backed stream.
        // We own it exclusively from here on.
        var rawStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (rawStream.ConfigureAwait(false))
        {
            // Strip ICY metadata bytes before the decoder sees them.
            // IcyMetaStream is a pure passthrough when icyMetaInt == 0.
            using var icyStream = new IcyMetaStream(rawStream, icyMetaInt,
                leaveInnerOpen: true); // rawStream disposal is handled by await using above

            // Wire ICY metadata events only when no external poller is active.
            if (string.IsNullOrWhiteSpace(_options.MetadataUrl))
            {
                icyStream.TrackChanged += (title, artist) =>
                    TrackChanged?.Invoke(new Track(
                        Title:         title,
                        Artist:        artist,
                        Album:         null,
                        AlbumArt:      null,
                        SourceId:      Id,
                        SourceDisplay: _options.StationName,
                        ExternalId:    null));
            }

            bool isOgg = contentType.Contains("ogg",  StringComparison.OrdinalIgnoreCase)
                      || _options.StreamUrl.EndsWith(".ogg",  StringComparison.OrdinalIgnoreCase)
                      || _options.StreamUrl.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);

        if (isOgg)
        {
            Log("starting OGG pump");
            await PumpOggAsync(icyStream, sink, ct).ConfigureAwait(false);
        }
        else
        {
            Log("starting MP3 pump");
            await PumpMp3Async(icyStream, sink, ct).ConfigureAwait(false);
        }
        }
    }

    // -----------------------------------------------------------------------
    // MP3: NAudio Mp3FileReader — works on non-seekable streams
    // -----------------------------------------------------------------------

    private static async Task PumpMp3Async(Stream stream, IPcmSink sink, CancellationToken ct)
    {
        // Mp3FileReaderBase builds a full table-of-contents by reading the entire
        // stream — catastrophic for an infinite HTTP stream (OOM). Instead, decode
        // frame-by-frame using Mp3Frame + IMp3FrameDecompressor directly.
        // PeekableStream is no longer needed because we never construct Mp3FileReaderBase.
        IMp3FrameDecompressor? decompressor = null;
        try
        {
            // Output buffer: max MP3 frame = 1152 samples × 2 ch × 2 bytes = 4608 bytes
            var pcmBuffer  = new byte[4608 * 4]; // generous headroom
            var floatBuf   = new float[4608 * 2];
            var shortBuf   = new short[4608 * 2];

            var sw        = Stopwatch.StartNew();
            var nextChunk = TimeSpan.Zero;
            bool firstSend = true;

            while (!ct.IsCancellationRequested)
            {
                Mp3Frame frame;
                try   { frame = Mp3Frame.LoadFromStream(stream); }
                catch (EndOfStreamException) { Log("MP3 stream ended"); return; }

                if (decompressor == null)
                {
                    var wf = new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2,
                                               frame.FrameLength, frame.BitRate);
                    decompressor = new AcmMp3FrameDecompressor(wf);
                    Log($"MP3 decompressor created: {frame.SampleRate} Hz, {wf.Channels} ch, {frame.BitRate} kbps");
                }

                int bytesDecompressed = decompressor.DecompressFrame(frame, pcmBuffer, 0);
                if (bytesDecompressed == 0) continue;

                // pcmBuffer contains s16 stereo PCM — convert to float for resampler/converter
                int sampleCount = bytesDecompressed / 2; // 2 bytes per s16 sample
                if (floatBuf.Length < sampleCount) floatBuf = new float[sampleCount];
                if (shortBuf.Length < sampleCount) shortBuf = new short[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                    floatBuf[i] = BitConverter.ToInt16(pcmBuffer, i * 2) / 32768f;

                // Handle mono → stereo
                ISampleProvider provider = new BufferedSampleProvider(
                    floatBuf, sampleCount,
                    WaveFormat.CreateIeeeFloatWaveFormat(frame.SampleRate,
                        decompressor.OutputFormat.Channels));

                if (provider.WaveFormat.Channels == 1)
                    provider = new MonoToStereoSampleProvider(provider);
                if (provider.WaveFormat.SampleRate != AudioFormat.SampleRate)
                    provider = new WdlResamplingSampleProvider(provider, AudioFormat.SampleRate);

                // Drain the provider into shortBuf
                int totalRead = 0;
                while (true)
                {
                    if (shortBuf.Length < totalRead + 4096) Array.Resize(ref shortBuf, shortBuf.Length * 2);
                    var tmp = new float[4096];
                    int r = provider.Read(tmp, 0, tmp.Length);
                    if (r == 0) break;
                    for (int i = 0; i < r; i++) shortBuf[totalRead + i] = Clip(tmp[i]);
                    totalRead += r;
                }

                if (totalRead == 0) continue;

                if (firstSend) { Log($"first sink.Send: {totalRead} samples"); firstSend = false; }
                sink.Send(shortBuf.AsSpan(0, totalRead));

                var framePeriod = TimeSpan.FromMicroseconds(
                    (long)(totalRead / AudioFormat.Channels) * 1_000_000L / AudioFormat.SampleRate);
                nextChunk += framePeriod;

                var now = sw.Elapsed;
                if (nextChunk > now)
                {
                    try { await Task.Delay(nextChunk - now, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                else
                {
                    nextChunk = now;
                }
            }
        }
        finally
        {
            decompressor?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // OGG/Vorbis: NVorbis VorbisReader
    // -----------------------------------------------------------------------

    private static async Task PumpOggAsync(Stream stream, IPcmSink sink, CancellationToken ct)
    {
        using var vorbis = new NVorbis.VorbisReader(stream, closeOnDispose: false);

        // MUST use CreateIeeeFloatWaveFormat — NVorbis outputs float samples,
        // not PCM. Using WaveFormat(sr, 32, ch) would tag it as PCM/32-bit
        // which downstream resamplers misinterpret.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(vorbis.SampleRate, vorbis.Channels);
        var raw    = new NVorbisToSampleProvider(vorbis, format);

        await PumpSampleProviderAsync(raw, sink, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Shared real-time PCM pump
    // -----------------------------------------------------------------------

    private static async Task PumpSampleProviderAsync(ISampleProvider samples,
                                                      IPcmSink sink,
                                                      CancellationToken ct)
    {
        // Ensure stereo
        if (samples.WaveFormat.Channels == 1)
            samples = new MonoToStereoSampleProvider(samples);

        // Ensure 44.1 kHz
        if (samples.WaveFormat.SampleRate != AudioFormat.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, AudioFormat.SampleRate);

        const int ChunkFrames = 2048;
        var floatBuf = new float[ChunkFrames * AudioFormat.Channels];
        var shortBuf = new short[ChunkFrames * AudioFormat.Channels];

        // Real-time pacing — same wall-clock scheme as LocalFileSource and
        // SubprocessPcmSource. Without pacing the decoder runs at CPU speed
        // and floods the DLL ring buffer, crashing the game with OOM.
        var sw        = Stopwatch.StartNew();
        var nextChunk = TimeSpan.Zero;
        bool firstSend = true;

        while (!ct.IsCancellationRequested)
        {
            int read = samples.Read(floatBuf, 0, floatBuf.Length);
            if (read == 0) { Log("decoder returned EOF"); return; }

            // Convert only the samples that were actually decoded.
            for (int i = 0; i < read; i++) shortBuf[i] = Clip(floatBuf[i]);

            if (firstSend) { Log($"first sink.Send: {read} samples, fmt={samples.WaveFormat}"); firstSend = false; }

            // Send only the decoded slice — never zero-pad to a full chunk.
            // Zero-padding injects silence and throws off the pacing math.
            sink.Send(shortBuf.AsSpan(0, read));

            // Pace based on the actual number of samples sent, not the
            // assumed full chunk size. This keeps the pacing accurate even
            // when the decoder returns short reads (common on live streams).
            var framePeriod = TimeSpan.FromMicroseconds(
                (long)(read / AudioFormat.Channels) * 1_000_000L / AudioFormat.SampleRate);
            nextChunk += framePeriod;

            var now = sw.Elapsed;
            if (nextChunk > now)
            {
                try { await Task.Delay(nextChunk - now, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            else
            {
                // We've fallen behind real-time (e.g. decoder stalled).
                // Re-anchor so we don't try to "catch up" by sending
                // bursts with no sleep — that would flood the ring buffer.
                nextChunk = now;
            }
        }
    }

    private static short Clip(float f)
    {
        if (f >  1f) f =  1f;
        if (f < -1f) f = -1f;
        return (short)(f * short.MaxValue);
    }

    private void PublishPlaceholder() =>
        TrackChanged?.Invoke(new Track(
            Title:         "Connecting…",
            Artist:        _options.StationName,
            Album:         null,
            AlbumArt:      null,
            SourceId:      Id,
            SourceDisplay: _options.StationName,
            ExternalId:    null));
}

// ---------------------------------------------------------------------------
// IcyMetaStream — strips ICY in-band metadata from the raw HTTP body
// ---------------------------------------------------------------------------

/// <summary>
/// Forward-read-only stream wrapper that removes ICY metadata blocks from
/// the byte stream before the audio decoder sees them, and raises
/// <see cref="TrackChanged"/> whenever a new <c>StreamTitle</c> appears.
///
/// ICY metadata is inserted every <c>icyMetaInt</c> audio bytes. Each
/// block begins with a 1-byte length indicator (multiply × 16 for byte
/// count), followed by NUL-padded ASCII: <c>StreamTitle='...';StreamUrl='...';</c>
///
/// When <c>icyMetaInt == 0</c> this class is a transparent no-op passthrough.
/// </summary>
internal sealed class IcyMetaStream : Stream
{
    /// <summary>Raised with (title, artist) when a StreamTitle changes.</summary>
    public event Action<string, string>? TrackChanged;

    private readonly Stream _inner;
    private readonly int    _metaInt;
    private readonly bool   _leaveInnerOpen;
    private int             _bytesUntilMeta;
    private long            _position;       // running byte counter; set-only throws

    public IcyMetaStream(Stream inner, int metaInt, bool leaveInnerOpen = false)
    {
        _inner          = inner;
        _metaInt        = metaInt;
        _leaveInnerOpen = leaveInnerOpen;
        _bytesUntilMeta = metaInt > 0 ? metaInt : int.MaxValue;
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        // Id3v2Tag reads Position to know how many tag bytes were consumed.
        // We track a running count of audio bytes delivered to callers.
        // Seek/set is still unsupported — we are a forward-only stream.
        get => _position;
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v)           => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_metaInt == 0)
        {
            int n = _inner.Read(buffer, offset, count);
            _position += n;
            return n;
        }

        // Only request up to _bytesUntilMeta audio bytes so we stop
        // exactly at the boundary where the next metadata block lives.
        int toRead = Math.Min(count, _bytesUntilMeta);
        int actual = _inner.Read(buffer, offset, toRead);
        if (actual <= 0) return actual;

        _position += actual;
        _bytesUntilMeta -= actual;
        if (_bytesUntilMeta == 0)
        {
            ReadAndParseMetaBlock();
            _bytesUntilMeta = _metaInt;
        }
        return actual;
    }

    private void ReadAndParseMetaBlock()
    {
        int lenByte = _inner.ReadByte();
        if (lenByte <= 0) return;

        int blockLen = lenByte * 16;
        var blockBuf = new byte[blockLen];
        int got = 0;
        while (got < blockLen)
        {
            int n = _inner.Read(blockBuf, got, blockLen - got);
            if (n <= 0) break;
            got += n;
        }

        var text = Encoding.UTF8.GetString(blockBuf, 0, got).TrimEnd('\0');
        if (string.IsNullOrEmpty(text)) return;

        var title = ExtractField(text, "StreamTitle");
        if (string.IsNullOrEmpty(title)) return;

        // Conventional format: "Artist - Title"
        string artist = "";
        int sep = title.IndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0)
        {
            artist = title[..sep].Trim();
            title  = title[(sep + 3)..].Trim();
        }

        Debug.WriteLine($"[hzn-radio-icy] {artist} – {title}");
        TrackChanged?.Invoke(title, artist);
    }

    private static string ExtractField(string text, string key)
    {
        var prefix = key + "='";
        int start  = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        start += prefix.Length;
        int end = text.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        return text[start..end];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveInnerOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}

// ---------------------------------------------------------------------------
// BufferedSampleProvider — exposes a float[] slice as an ISampleProvider
// ---------------------------------------------------------------------------

internal sealed class BufferedSampleProvider : ISampleProvider
{
    private readonly float[] _data;
    private readonly int     _count;
    private int              _pos;

    public BufferedSampleProvider(float[] data, int count, WaveFormat format)
    {
        _data      = data;
        _count     = count;
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int available = _count - _pos;
        if (available <= 0) return 0;
        int toCopy = Math.Min(count, available);
        Array.Copy(_data, _pos, buffer, offset, toCopy);
        _pos += toCopy;
        return toCopy;
    }
}

// ---------------------------------------------------------------------------
// PeekableStream — buffers the first N bytes to satisfy header-probe seeks
// ---------------------------------------------------------------------------

/// <summary>
/// Wraps a forward-only stream and buffers the first <c>headerBytes</c> bytes
/// so that a seek back to position 0 (or anywhere within the buffered window)
/// can be satisfied without touching the network. Once the read position moves
/// past the buffer the stream becomes a pure passthrough and seeks throw.
///
/// This is needed because <see cref="NAudio.Wave.Mp3FileReaderBase"/> calls
/// <see cref="NAudio.Wave.Id3v2Tag.ReadTag"/> which reads a few bytes then
/// seeks back to 0 when no ID3 tag is present.
/// </summary>
internal sealed class PeekableStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _header;
    private int  _headerFilled; // bytes actually read into _header so far
    private long _position;     // logical read cursor

    public PeekableStream(Stream inner, int headerBytes = 10 * 1024)
    {
        _inner  = inner;
        _header = new byte[headerBytes];
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;   // advertise seekable so NAudio won't bail
    public override bool CanWrite => false;
    public override long Length   => long.MaxValue; // infinite stream; NAudio reads but never uses for real seeking

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => throw new NotSupportedException("SeekOrigin.End"),
            _                  => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Cannot seek before stream start.");
        // If NAudio tries to seek beyond buffered data (e.g. to Length-128 to probe
        // for Xing/VBRI VBR headers), just leave position where it is and return it.
        // The caller will get an empty read and give up on the VBR probe gracefully.
        if (target > _headerFilled)
            return _position;
        _position = target;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        int totalRead = 0;

        // 1. Serve from header buffer if position is within it.
        if (_position < _headerFilled)
        {
            int fromHeader = (int)Math.Min(count, _headerFilled - _position);
            Buffer.BlockCopy(_header, (int)_position, buffer, offset, fromHeader);
            _position  += fromHeader;
            offset     += fromHeader;
            count      -= fromHeader;
            totalRead  += fromHeader;
            if (count == 0) return totalRead;
        }

        // 2. Read from inner stream. If still within the header window,
        //    also copy into the buffer so future seeks can be satisfied.
        int innerRead = _inner.Read(buffer, offset, count);
        if (innerRead <= 0) return totalRead;

        // Fill header buffer while there is still room.
        if (_headerFilled < _header.Length)
        {
            int canStore = Math.Min(innerRead, _header.Length - _headerFilled);
            Buffer.BlockCopy(buffer, offset, _header, _headerFilled, canStore);
            _headerFilled += canStore;
        }

        _position += innerRead;
        totalRead += innerRead;
        return totalRead;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Do NOT dispose _inner — the caller (IcyMetaStream's await using) owns it.
        base.Dispose(disposing);
    }
}


// ---------------------------------------------------------------------------

internal sealed class NVorbisToSampleProvider : ISampleProvider
{
    private readonly NVorbis.VorbisReader _vorbis;
    public WaveFormat WaveFormat { get; }

    public NVorbisToSampleProvider(NVorbis.VorbisReader vorbis, WaveFormat fmt)
    {
        _vorbis    = vorbis;
        WaveFormat = fmt;
    }

    public int Read(float[] buffer, int offset, int count)
        => _vorbis.ReadSamples(buffer, offset, count);
}

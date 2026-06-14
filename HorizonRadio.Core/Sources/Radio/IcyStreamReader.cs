using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Sources.Radio;

/// <summary>
/// Reads an internet-radio stream over HTTP with ICY (SHOUTcast/Icecast) metadata
/// enabled, splits the interleaved metadata out of the audio, and pumps the clean
/// audio bytes into a target stream (ffmpeg's stdin). The current song is broadcast
/// in-band as an ICY <c>StreamTitle</c> every <c>icy-metaint</c> bytes — we surface
/// each change via <see cref="StreamTitleChanged"/> so the source can republish
/// now-playing as songs roll over.
///
/// We open the stream ourselves (rather than letting ffmpeg fetch the URL) precisely
/// so we can see this metadata; ffmpeg only ever receives decoded-ready audio.
///
/// Compatibility note: this speaks normal HTTP, which covers Icecast and modern
/// SHOUTcast (v2). Legacy SHOUTcast v1 servers that reply with an "ICY 200 OK" status
/// line instead of an HTTP one aren't handled here (HttpClient rejects them); those
/// surface as a connect failure. A raw-socket fallback is a possible follow-up.
/// </summary>
public sealed class IcyStreamReader : IAsyncDisposable
{
    private readonly string _url;
    private readonly HttpClient _http;
    private HttpResponseMessage? _response;

    private string? _lastTitle;

    /// <summary>Raised when the in-band song title changes (already de-duplicated and
    /// trimmed). The raw value is whatever the station broadcasts — usually
    /// "Artist - Title", parsed downstream by the source.</summary>
    public event Action<string>? StreamTitleChanged;

    /// <summary>The station name from the <c>icy-name</c> response header, if any —
    /// used as the display name for the paste-a-URL path where we have no directory entry.</summary>
    public string? IcyName { get; private set; }

    public IcyStreamReader(string url)
    {
        _url = url;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // long-lived stream
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HorizonRadio/0.5 (internet-radio)");
    }

    /// <summary>Connect and read response headers (icy-metaint, icy-name). Must be
    /// called before <see cref="PumpToAsync"/>. Throws on connection/HTTP failure.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");

        // The client timeout is infinite (the body is a long-lived stream), so bound the
        // connect/header phase itself: a dead station must fail, not hang until skipped.
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            _response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The timeout fired (not a real skip/stop) — surface as a non-cancel error so
            // the caller reconnects with backoff rather than treating it as a skip.
            throw new TimeoutException($"Connecting to radio stream timed out: {_url}");
        }
        _response.EnsureSuccessStatusCode();

        IcyName = FirstHeader(_response, "icy-name");
    }

    /// <summary>Read the audio body to EOF/cancellation, stripping ICY metadata and
    /// writing clean audio to <paramref name="target"/>. Returns on EOF (stream ended)
    /// or throws <see cref="OperationCanceledException"/> on stop/skip.</summary>
    public async Task PumpToAsync(Stream target, CancellationToken ct)
    {
        if (_response is null) throw new InvalidOperationException("ConnectAsync first.");

        int metaInt = ParseInt(FirstHeader(_response, "icy-metaint"));
        await using var body = await _response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await PumpInterleavedAsync(body, metaInt, target, HandleMetadata, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Core SHOUTcast/Icecast de-interleave: copy audio from <paramref name="source"/> to
    /// <paramref name="target"/>, lifting an ICY metadata block out every
    /// <paramref name="metaInt"/> bytes and handing its raw bytes to
    /// <paramref name="onMetaBlock"/>. When <paramref name="metaInt"/> is 0 the source is a
    /// plain audio stream and is copied straight through. Pure stream-in/stream-out so it's
    /// unit-testable without HTTP. Returns on EOF.
    /// </summary>
    internal static async Task PumpInterleavedAsync(
        Stream source, int metaInt, Stream target, Action<byte[]> onMetaBlock, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];

        if (metaInt <= 0)
        {
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await target.FlushAsync(ct).ConfigureAwait(false);
            }
            return;
        }

        int bytesUntilMeta = metaInt;
        while (true)
        {
            // Audio run up to the next metadata marker.
            while (bytesUntilMeta > 0)
            {
                int want = Math.Min(buffer.Length, bytesUntilMeta);
                int got = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (got <= 0) return; // EOF
                await target.WriteAsync(buffer.AsMemory(0, got), ct).ConfigureAwait(false);
                bytesUntilMeta -= got;
            }
            await target.FlushAsync(ct).ConfigureAwait(false);

            // One length byte: metadata length / 16. Zero means "no change this round".
            int lenByte = await ReadByteAsync(source, ct).ConfigureAwait(false);
            if (lenByte < 0) return; // EOF
            int metaLen = lenByte * 16;
            if (metaLen > 0)
            {
                var meta = new byte[metaLen];
                if (!await ReadExactAsync(source, meta, ct).ConfigureAwait(false)) return;
                onMetaBlock(meta);
            }

            bytesUntilMeta = metaInt;
        }
    }

    private void HandleMetadata(byte[] meta)
    {
        // Metadata is "StreamTitle='...';StreamUrl='...';", null-padded to a multiple of 16.
        var text = Encoding.UTF8.GetString(meta).TrimEnd('\0');
        var title = ExtractStreamTitle(text);
        if (string.IsNullOrWhiteSpace(title)) return;
        title = title.Trim();
        if (title == _lastTitle) return;
        _lastTitle = title;
        try { StreamTitleChanged?.Invoke(title); }
        catch { /* a handler throwing must not kill the pump */ }
    }

    /// <summary>Pull the value of <c>StreamTitle='…'</c> out of an ICY metadata blob.</summary>
    internal static string? ExtractStreamTitle(string meta)
    {
        const string key = "StreamTitle='";
        int start = meta.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        int end = meta.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0) end = meta.IndexOf('\'', start); // tolerate a missing trailing ;
        if (end < 0) return null;
        return meta.Substring(start, end - start);
    }

    private static async Task<int> ReadByteAsync(Stream s, CancellationToken ct)
    {
        var one = new byte[1];
        int got = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
        return got <= 0 ? -1 : one[0];
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int filled = 0;
        while (filled < buf.Length)
        {
            int got = await s.ReadAsync(buf.AsMemory(filled, buf.Length - filled), ct).ConfigureAwait(false);
            if (got <= 0) return false;
            filled += got;
        }
        return true;
    }

    private static string? FirstHeader(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var values))
            foreach (var v in values) return v;
        if (resp.Content.Headers.TryGetValues(name, out var cvalues))
            foreach (var v in cvalues) return v;
        return null;
    }

    private static int ParseInt(string? s) => int.TryParse(s, out var n) ? n : 0;

    public ValueTask DisposeAsync()
    {
        _response?.Dispose();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

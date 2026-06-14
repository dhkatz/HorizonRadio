using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace HorizonRadio.UI.Imaging;

/// <summary>
/// Loads remote artwork URLs into Avalonia <see cref="Bitmap"/>s for search-result
/// rows. Search sources hand back image URLs (not bytes) to keep result lists cheap
/// to build; this fetches and decodes them lazily, off the UI thread, and caches the
/// downloaded bytes so the same thumbnail (very common — an artist's tracks share
/// album art) is fetched once. Failure-silent: a broken URL just yields the
/// placeholder tile.
/// </summary>
public static class RemoteArt
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Bounded LRU of downloaded bytes (not Bitmaps): a Bitmap is a disposable
    // GPU-backed resource we don't want shared/evicted out from under a live binding,
    // and re-decoding cached bytes is cheap. The cap keeps a long session of varied
    // searches from growing memory without bound — oldest entries fall out first.
    private const int MaxEntries = 128;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, byte[]> Cache = new();
    private static readonly LinkedList<string> Lru = new(); // front = most recent

    public static async Task<Bitmap?> LoadAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var bytes = Get(url);
            if (bytes is null)
            {
                bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                Put(url, bytes);
            }
            return ImageBytes.ToBitmap(bytes);
        }
        catch
        {
            return null; // network/decoding failure → caller shows the placeholder
        }
    }

    private static byte[]? Get(string url)
    {
        lock (Lock)
        {
            if (!Cache.TryGetValue(url, out var bytes)) return null;
            Touch(url);
            return bytes;
        }
    }

    private static void Put(string url, byte[] bytes)
    {
        lock (Lock)
        {
            if (Cache.ContainsKey(url)) { Cache[url] = bytes; Touch(url); return; }
            Cache[url] = bytes;
            Lru.AddFirst(url);
            if (Cache.Count > MaxEntries)
            {
                var oldest = Lru.Last!.Value;
                Lru.RemoveLast();
                Cache.Remove(oldest);
            }
        }
    }

    // Caller holds Lock.
    private static void Touch(string url)
    {
        Lru.Remove(url);
        Lru.AddFirst(url);
    }
}

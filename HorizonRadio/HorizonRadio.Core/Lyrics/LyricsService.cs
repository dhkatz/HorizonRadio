using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Sdk.Lyrics;

namespace HorizonRadio.Core.Lyrics;

/// <summary>
/// Finds the lyrics for a song across the lyrics providers. Every provider is asked at once; the
/// answer is the first provider (in their order) with synced lyrics, else the first unsynced
/// lyrics with other versions (romanized, translated), else the first with any.
/// Answers are remembered for the session; a lookup where a provider failed isn't, so it's tried
/// again next time.
/// </summary>
public sealed class LyricsService : IAsyncDisposable
{
    private const int CacheLimit = 256;

    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly Dictionary<string, SongLyrics?> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Lock _lock = new();

    public LyricsService(IReadOnlyList<ILyricsProvider> providers) => _providers = providers;

    public bool HasProviders => _providers.Count > 0;

    public async Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        var key = Key(query);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var total = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var asks = _providers.Select(p => AskAsync(p, query, stop.Token)).ToList();

        SongLyrics? plain = null;
        bool failed = false;
        try
        {
            foreach (var ask in asks)
            {
                var (lyrics, error) = await ask.ConfigureAwait(false);
                // A cancelled caller's asks come back empty: that's not an answer to remember.
                ct.ThrowIfCancellationRequested();
                if (error) failed = true;
                if (lyrics is { Synced: true })
                {
                    if (!failed) Remember(key, lyrics);
                    Report(query, lyrics, total.ElapsedMilliseconds);
                    return lyrics;
                }
                // Unsynced, the text with a romanization or translations to switch to is the
                // more useful sheet (VocaDB's, for a Vocaloid song LRCLIB only has as plain text).
                if (plain == null || (plain.Variants is not { Count: > 0 } && lyrics?.Variants is { Count: > 0 }))
                    plain = lyrics ?? plain;
            }
        }
        finally
        {
            // The rest are only lower-priority answers now.
            await stop.CancelAsync().ConfigureAwait(false);
        }

        if (!failed) Remember(key, plain);
        Report(query, plain, total.ElapsedMilliseconds);
        return plain;
    }

    private static async Task<(SongLyrics? Lyrics, bool Error)> AskAsync(ILyricsProvider provider, LyricsQuery query, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            var lyrics = await provider.FindAsync(query, ct).ConfigureAwait(false);
            if (lyrics is { Lines.Count: 0 }) lyrics = null;
            Trace.WriteLine($"[hzn-lyrics] {provider.Id}: {Describe(lyrics)} in {elapsed.ElapsedMilliseconds} ms");
            return (lyrics, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, false);
        }
        catch (Exception ex)
        {
            ProcessConsole.Append("lyrics", $"{provider.Id} unavailable: {ex.GetType().Name}: {ex.Message}");
            return (null, true);
        }
    }

    private static void Report(LyricsQuery query, SongLyrics? lyrics, long ms) =>
        ProcessConsole.Append("lyrics",
            $"{query.Artist} – {query.Title}: {(lyrics == null ? "none" : $"{Describe(lyrics)} from {lyrics.ProviderId}")} · {ms} ms");

    private static string Describe(SongLyrics? lyrics) => lyrics switch
    {
        null => "none",
        { Synced: true } when lyrics.Lines.Any(l => l.Words is { Count: > 0 }) => "word-synced",
        { Synced: true } => "line-synced",
        _ => "unsynced",
    };

    private void Remember(string key, SongLyrics? lyrics)
    {
        lock (_lock)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = lyrics;
            _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > CacheLimit) _cache.Remove(_cacheOrder.Dequeue());
        }
    }

    private static string Key(LyricsQuery q) =>
        string.Join('\u001f', q.Title.Trim().ToUpperInvariant(), q.Artist.Trim().ToUpperInvariant(),
            q.Duration is { } d ? ((int)Math.Round(d.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            q.YouTubeVideoId ?? "");

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
        {
            try { await p.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Trace.WriteLine($"[hzn-lyrics] dispose {p.Id}: {ex.Message}"); }
        }
    }
}

namespace HorizonRadio.Sdk.Lyrics;

/// <summary>
/// Finds a song's lyrics in one place (LRCLIB, Unison, …). The host asks every provider and keeps
/// the best answer: synced lyrics before unsynced, then the providers' order.
/// </summary>
public interface ILyricsProvider : IAsyncDisposable
{
    string Id { get; }

    /// <summary>The song's lyrics, or null when this provider has none that fit
    /// <paramref name="query"/>. Throw when the provider couldn't answer (a network error), so the
    /// miss isn't remembered.</summary>
    Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct);
}

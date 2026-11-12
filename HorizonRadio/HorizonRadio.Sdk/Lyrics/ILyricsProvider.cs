namespace HorizonRadio.Sdk.Lyrics;

/// <summary>
/// Finds a song's lyrics in one place (LRCLIB, Unison, …). The host asks every provider and keeps
/// the best answer: synced lyrics before unsynced, then <see cref="IOrdered.SortOrder"/>, so put a
/// provider whose lyrics are timed more carefully first. A plugin registers one in its
/// <see cref="IPluginModule"/>; it has no settings, so it's the provider itself, not a factory.
/// The container disposes it at exit.
/// </summary>
public interface ILyricsProvider : IOrdered, IAsyncDisposable
{
    /// <summary>The song's lyrics, or null when this provider has none that fit
    /// <paramref name="query"/>. Throw when the provider couldn't answer (a network error), so the
    /// miss isn't remembered.</summary>
    Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct);
}

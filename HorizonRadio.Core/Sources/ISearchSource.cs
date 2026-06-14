namespace HorizonRadio.Core.Sources;

/// <summary>What a <see cref="SearchResult"/> points at — mirrors the locator kinds
/// the queue can already resolve (a single track, or a whole album/playlist).</summary>
public enum SearchResultKind
{
    Track,
    Album,
    Playlist,
}

/// <summary>
/// One hit from a unified search: enough to render a result row and, on click, hand
/// straight to <see cref="Queue.QueuePlayback.EnqueueLocatorAsync"/>. <see cref="Locator"/>
/// is a <see cref="ContentRef"/>-ready string for <see cref="SourceId"/>'s factory
/// (e.g. a <c>spotify:track:…</c> URI), so "search → queue" needs no new playback code.
/// </summary>
/// <param name="SourceId">Catalog id of the source that produced this — the key for
/// <see cref="SourceCatalog.Find"/> to get the factory to enqueue against.</param>
/// <param name="Kind">Track / album / playlist, for the row's glyph and grouping.</param>
/// <param name="Title">Primary line (track or album/playlist name).</param>
/// <param name="Subtitle">Secondary line (artist, or owner for a playlist).</param>
/// <param name="ArtUrl">Remote artwork URL, or null. Loaded lazily by the UI — search
/// sources return the URL rather than bytes so a result list stays cheap to build.</param>
/// <param name="Locator">The content locator to enqueue (a Spotify URI / URL, …).</param>
public sealed record SearchResult(
    string SourceId,
    SearchResultKind Kind,
    string Title,
    string Subtitle,
    string? ArtUrl,
    string Locator);

/// <summary>
/// Optional capability for an <see cref="IAudioSourceFactory"/> whose source can be
/// searched by free text. The unified search box queries every catalog factory that
/// implements this and merges the results; each result's <see cref="SearchResult.Locator"/>
/// flows through the normal enqueue path, so adding a source to search is just adding
/// this interface (no UI change). Implemented by the factory — like
/// <see cref="IAuthenticatingSource"/> — because searching is an account/config-time
/// concern that precedes building any per-content source instance.
///
/// Implementations should return an empty list (not throw) when the source isn't
/// usable yet (e.g. not connected), so one unconfigured source can't break a query
/// that spans several.
/// </summary>
public interface ISearchSource
{
    /// <summary>Search the source's catalog. <paramref name="limit"/> caps results per
    /// source (the live dropdown asks for a handful; the full search page asks for more).</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct = default);
}

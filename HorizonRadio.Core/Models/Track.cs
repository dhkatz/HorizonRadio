namespace HorizonRadio.Core.Models;

/// <summary>
/// One alternative interpretation of a freeform/stream title — an (artist, title) guess the
/// metadata resolver can validate against the catalogs. A source (e.g. internet radio) that
/// can't be sure how to split "Channel - Artist - Title" attaches several of these; the
/// resolver keeps whichever one a catalog confidently matches.
/// </summary>
public sealed record TitleCandidate(string? Artist, string Title);

/// <summary>
/// What we know about a currently playing track. The UI binds against
/// this; the audio pipeline (sources and enrichment) produces it; the IPC
/// channel transports it to the DLL for HUD injection. `AlbumArt` is
/// optional — local files often have it via ID3 APIC, Spotify needs an
/// online lookup, and unknown sources may have nothing.
/// </summary>
/// <param name="ExternalId">
/// Source-specific stable id (e.g. "spotify:track:6ikPHWdz..."), used
/// as a cache key for metadata enrichment. May be null for sources
/// that don't expose a stable identifier.
/// </param>
/// <param name="FallbackArt">
/// A low-priority image the source offers — e.g. an internet-radio station's
/// logo — shown only when no better art is available. Unlike <see cref="AlbumArt"/>
/// it never competes with metadata providers: the resolver fills <see cref="AlbumArt"/>
/// from it as a last resort, so a real cover always wins when one is found. Carried
/// through merges untouched; providers never set it.
/// </param>
/// <param name="Candidates">
/// Alternative (artist, title) interpretations of an ambiguous source title (see
/// <see cref="TitleCandidate"/>). A seed-only hint: the resolver tries the primary
/// fields first, then these, and keeps whichever a catalog confirms. Null/empty for the
/// common case and for the resolver's output.
/// </param>
public sealed record Track(
    string Title,
    string Artist,
    string? Album,
    byte[]? AlbumArt,
    string SourceId,
    string SourceDisplay,
    string? ExternalId = null,
    int? Year = null,
    int? TrackNumber = null,
    byte[]? FallbackArt = null,
    IReadOnlyList<TitleCandidate>? Candidates = null)
{
    /// <summary>Placeholder track with no fields set — a non-null default for
    /// holders that haven't learned their real metadata yet.</summary>
    public static readonly Track Empty = new("", "", null, null, "", "");
}

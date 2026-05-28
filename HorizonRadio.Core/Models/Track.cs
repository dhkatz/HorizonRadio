namespace HorizonRadio.Core.Models;

/// <summary>
/// What we know about a currently-playing track. The UI binds against
/// this; the audio pipeline (sources + enrichment) produces it; the IPC
/// channel transports it to the DLL for HUD injection. `AlbumArt` is
/// optional — local files often have it via ID3 APIC, Spotify needs an
/// online lookup, and unknown sources may have nothing.
/// </summary>
public sealed record Track(
    string  Title,
    string  Artist,
    string? Album,
    byte[]? AlbumArt,
    string  SourceId,
    string  SourceDisplay,
    /// <summary>
    /// Source-specific stable id (e.g. "spotify:track:6ikPHWdz..."), used
    /// as a cache key for metadata enrichment. May be null for sources
    /// that don't expose a stable identifier.
    /// </summary>
    string? ExternalId = null);

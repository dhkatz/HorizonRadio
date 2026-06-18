namespace HorizonRadio.Core.Metadata;

/// <summary>
/// A cached metadata lookup result for one provider+query — the persisted shape behind
/// <see cref="IMetadataCache"/>. Every field is nullable because a provider supplies only what it
/// found; <see cref="AlbumArt"/> is the primary payload (the biggest thing a track grows and the
/// one that doesn't change for a recording). An entry with art or PV links is durable; an art-less
/// one is a miss / partial hit that the cache ages out.
/// </summary>
public sealed record MetadataCacheEntry(
    string? Title,
    string? Artist,
    string? Album,
    byte[]? AlbumArt,
    string? Mbid,
    int? Year = null,
    IReadOnlyList<PlayableRef>? Pvs = null);

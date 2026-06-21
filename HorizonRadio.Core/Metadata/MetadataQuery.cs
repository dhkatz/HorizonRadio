using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// The seed a contributor looks a track up from: the best title/artist/album/id
/// known so far. The resolver feeds each contributor the working query (improved
/// by earlier contributors in the chain), so a downstream lookup (MusicBrainz,
/// Spotify) searches against a cleaned-up title/artist rather than a raw video
/// title.
/// </summary>
public sealed record MetadataQuery(
    string SourceId,
    string Title,
    string Artist,
    string? Album,
    string? ExternalId)
{
    public static MetadataQuery FromTrack(Track t) =>
        new(t.SourceId, t.Title, t.Artist, t.Album, t.ExternalId);
}

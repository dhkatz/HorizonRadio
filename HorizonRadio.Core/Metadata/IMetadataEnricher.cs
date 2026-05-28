using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Optional second pass over a freshly-published <see cref="Track"/>.
/// Implementations look the track up in an external database
/// (MusicBrainz, Spotify Web API, last.fm, ...) and return a copy of
/// the track with fields filled in — album, album art, canonical
/// artist/title strings, etc.
///
/// Network-bound by definition. Implementations must be cancellable
/// and rate-limit themselves to the service's published ToS limits.
/// Returning null means "no enrichment available"; callers stick with
/// the source-provided track.
/// </summary>
public interface IMetadataEnricher
{
    /// <summary>Stable lowercase id. Used for cache namespacing and
    /// log messages.</summary>
    string Id { get; }

    /// <summary>Return an enriched copy of <paramref name="track"/>, or
    /// null if no match was found / rate limit hit / error etc. Must
    /// honor cancellation promptly.</summary>
    Task<Track?> EnrichAsync(Track track, CancellationToken ct);
}

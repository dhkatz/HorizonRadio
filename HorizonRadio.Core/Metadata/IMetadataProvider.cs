using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Metadata;

public interface IMetadataProvider : IAsyncDisposable
{
    string Id { get; }

    Task<Track?> EnrichAsync(Track track, CancellationToken ct);
}

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// A metadata contributor: given a <see cref="MetadataQuery"/> it returns only the
/// fields it can supply (a <see cref="MetadataContribution"/>), or null when it has
/// nothing. The <see cref="MetadataResolver"/> merges contributions across providers
/// per the user's <see cref="MetadataPolicy"/>. Implementations own their own caching
/// and any rate limiting. (Named "provider" because that's what the UI calls them;
/// the pipeline treats each as one contributor among several, including the source.)
/// </summary>
public interface IMetadataProvider : IAsyncDisposable
{
    string Id { get; }

    Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct);
}

namespace HorizonRadio.Sdk.Metadata;

/// <summary>
/// A metadata contributor: given a <see cref="MetadataQuery"/> it returns only the
/// fields it can supply (a <see cref="MetadataContribution"/>), or null when it has
/// nothing. The host's <c>MetadataResolver</c> merges contributions across providers
/// per the user's <see cref="MetadataPolicy"/>. Implementations own their own caching
/// and any rate limiting. (Named "provider" because that's what the UI calls them;
/// the pipeline treats each as one contributor among several, including the source.)
/// </summary>
public interface IMetadataProvider : IAsyncDisposable
{
    string Id { get; }

    Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct);

    /// <summary>
    /// True when this catalog knows which version of a song is the original (it links covers and
    /// remixes to it), so given only a title, or a title and the voice that sings it, its answer
    /// names the song's real artist. For such a query the resolver asks these catalogs first and
    /// asks the others with the artist they found, instead of trusting the first store that has
    /// some version by that name (a cover, a remix album).
    /// </summary>
    bool IdentifiesOriginals => false;
}

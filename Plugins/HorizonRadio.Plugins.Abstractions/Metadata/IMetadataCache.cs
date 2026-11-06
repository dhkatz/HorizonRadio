namespace HorizonRadio.Core.Metadata;

/// <summary>
/// The on-disk metadata cache as a metadata provider sees it: look up a prior result, store one,
/// or record a miss. Keyed by a stable hash of (provider-id, query) — providers compute the key
/// with the host cache's <c>Key</c> helper. The concrete implementation (disk layout, freshness /
/// TTL, versioning) lives in the host; a provider only needs this surface, so it can be supplied
/// through <see cref="HorizonRadio.Plugins.Abstractions.IPluginContext"/> without coupling the
/// provider to the host's cache type.
/// </summary>
public interface IMetadataCache
{
    /// <summary>Return the cached entry for <paramref name="key"/>, or null when absent/stale.</summary>
    MetadataCacheEntry? TryGet(string key);

    /// <summary>Store <paramref name="entry"/> under <paramref name="key"/>.</summary>
    void Put(string key, MetadataCacheEntry entry);

    /// <summary>Record that a lookup yielded nothing, so it isn't re-run every time.</summary>
    void PutMiss(string key);
}

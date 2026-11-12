using System.Diagnostics;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Every metadata provider the plugins contribute (their factories), in display order. A service built
/// from the container; <see cref="MetadataConfigStore"/> derives its fresh-install defaults and
/// "introduced" set from it.
/// </summary>
public sealed class MetadataCatalog(IEnumerable<IMetadataProviderFactory> providers)
{
    public const string NoneId = "none";

    /// <summary>Every provider factory the plugins contribute, in display order.</summary>
    public IReadOnlyList<IMetadataProviderFactory> All { get; } = providers.InDisplayOrder();

    /// <summary>Providers enabled out of the box (keyless, no setup), highest priority
    /// first. Spotify is excluded — it needs credentials. VocaDB is last: it fills the
    /// Vocaloid/doujin gap the others miss, but its art is a (non-square) video thumbnail,
    /// so the square-cover providers win when they have a match. Used for fresh-install
    /// defaults and the one-time migration that enables a newly-shipped provider.</summary>
    public static IReadOnlyList<string> DefaultEnabledOrder { get; } = ["itunes", "musicbrainz", "vocadb"];

    public IMetadataProviderFactory? Find(string id) =>
        All.FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// Build the live contributor list + policy from saved config: instantiate each
    /// enabled provider (skipping any that fail to construct, e.g. Spotify without
    /// credentials) and assemble a <see cref="MetadataPolicy"/> with the source first,
    /// the providers in the user's order, and the per-field forced overrides.
    /// </summary>
    public (IReadOnlyList<IMetadataProvider> Contributors, MetadataPolicy Policy) BuildPipeline(
        MetadataConfigStore store)
    {
        var contributors = new List<IMetadataProvider>();
        var enabledIds = new List<string>();
        foreach (var id in store.Order)
        {
            if (Find(id) is not { } factory) continue;
            try
            {
                var values = store.Load(factory.Id, factory.Schema);
                contributors.Add(factory.Create(values));
                enabledIds.Add(factory.Id);
            }
            catch (System.Exception ex)
            {
                Trace.WriteLine($"[hzn-meta] skip {id}: {ex.Message}");
            }
        }

        var policy = new MetadataPolicy(
            [MetadataPolicy.SourceId, .. enabledIds],
            new Dictionary<MetadataField, string>(store.Forced));
        return (contributors, policy);
    }
}

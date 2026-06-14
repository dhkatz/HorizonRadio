using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HorizonRadio.Core.Metadata.Apple;
using HorizonRadio.Core.Metadata.MusicBrainz;
using HorizonRadio.Core.Metadata.Spotify;
using HorizonRadio.Core.Metadata.VocaDb;

namespace HorizonRadio.Core.Metadata;

public static class MetadataCatalog
{
    public const string NoneId = "none";

    public static IReadOnlyList<IMetadataProviderFactory> All { get; } =
    [
        new SpotifyProviderFactory(),
        new ItunesProviderFactory(),
        new MusicBrainzProviderFactory(),
        new VocaDbProviderFactory(),
    ];

    /// <summary>Providers enabled out of the box (keyless, no setup), highest priority
    /// first. Spotify is excluded — it needs credentials. VocaDB is last: it fills the
    /// Vocaloid/doujin gap the others miss, but its art is a (non-square) video thumbnail,
    /// so the square-cover providers win when they have a match. Used for fresh-install
    /// defaults and the one-time migration that enables a newly-shipped provider.</summary>
    public static IReadOnlyList<string> DefaultEnabledOrder { get; } = ["itunes", "musicbrainz", "vocadb"];

    public static IMetadataProviderFactory? Find(string id) =>
        All.FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// Build the live contributor list + policy from saved config: instantiate each
    /// enabled provider (skipping any that fail to construct, e.g. Spotify without
    /// credentials) and assemble a <see cref="MetadataPolicy"/> with the source first,
    /// the providers in the user's order, and the per-field forced overrides.
    /// </summary>
    public static (IReadOnlyList<IMetadataProvider> Contributors, MetadataPolicy Policy) BuildPipeline(
        MetadataConfigStore store, MetadataCache cache)
    {
        var contributors = new List<IMetadataProvider>();
        var enabledIds = new List<string>();
        foreach (var id in store.Order)
        {
            if (Find(id) is not { } factory) continue;
            try
            {
                var values = store.Load(factory.Id, factory.Schema);
                contributors.Add(factory.Create(values, cache));
                enabledIds.Add(factory.Id);
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[hzn-meta] skip {id}: {ex.Message}");
            }
        }

        var policy = new MetadataPolicy(
            [MetadataPolicy.SourceId, .. enabledIds],
            new Dictionary<MetadataField, string>(store.Forced));
        return (contributors, policy);
    }
}

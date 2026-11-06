using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Metadata;

public static class MetadataCatalog
{
    public const string NoneId = "none";

    private static IReadOnlyList<IMetadataPlugin> _plugins = [];

    /// <summary>Register the available metadata plugins. Called once by the composition root — which
    /// references the plugin assemblies and so can name them — BEFORE any provider config is loaded
    /// (<see cref="MetadataConfigStore"/> derives its fresh-install defaults and "introduced" set from
    /// <see cref="All"/>, so the catalog must be populated first). Plugins are listed in display order.</summary>
    public static void Initialize(IReadOnlyList<IMetadataPlugin> plugins)
    {
        _plugins = plugins;
        All = [.. plugins.SelectMany(p => p.Providers)];
    }

    /// <summary>The registered metadata plugins, in display order.</summary>
    public static IReadOnlyList<IMetadataPlugin> Plugins => _plugins;

    /// <summary>Every provider factory the registered plugins contribute, flattened in display order.
    /// Empty until <see cref="Initialize"/> runs.</summary>
    public static IReadOnlyList<IMetadataProviderFactory> All { get; private set; } = [];

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
        MetadataConfigStore store, IPluginContext context)
    {
        var contributors = new List<IMetadataProvider>();
        var enabledIds = new List<string>();
        foreach (var id in store.Order)
        {
            if (Find(id) is not { } factory) continue;
            try
            {
                var values = store.Load(factory.Id, factory.Schema);
                contributors.Add(factory.Create(values, context));
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

using System.Collections.Generic;
using System.Linq;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Registry of every <see cref="IAudioSourceFactory"/> the app knows about. The UI's source picker
/// is bound to <see cref="All"/>; <see cref="Find"/> resolves an id to its factory.
///
/// Populated by the composition root via <see cref="Initialize"/> — which references the source
/// plugin assemblies and so can name them — before any source is resolved. Until then the catalog
/// is empty. Call sites read <see cref="All"/>/<see cref="Find"/> exactly as before.
/// </summary>
public static class SourceCatalog
{
    private static IReadOnlyList<ISourcePlugin> _plugins = [];

    /// <summary>Register the available source plugins (in display order). Called once at startup,
    /// before the catalog is read.</summary>
    public static void Initialize(IReadOnlyList<ISourcePlugin> plugins)
    {
        _plugins = plugins;
        All = [.. plugins.SelectMany(p => p.Sources)];
    }

    /// <summary>The registered source plugins, in display order.</summary>
    public static IReadOnlyList<ISourcePlugin> Plugins => _plugins;

    /// <summary>Every source factory the registered plugins contribute, flattened in display order.
    /// Empty until <see cref="Initialize"/> runs.</summary>
    public static IReadOnlyList<IAudioSourceFactory> All { get; private set; } = [];

    public static IAudioSourceFactory? Find(string id) =>
        All.FirstOrDefault(f => f.Id == id);
}

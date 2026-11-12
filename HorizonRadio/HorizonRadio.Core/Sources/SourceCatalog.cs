using System.Linq;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Registry of every <see cref="IAudioSourceFactory"/> the app knows about. The UI's source picker
/// is bound to <see cref="All"/>; <see cref="Find"/> resolves an id to its factory.
///
/// Populated by the composition root via <see cref="Initialize"/>, from the sources the plugins
/// registered in the container, before any source is resolved. Until then the catalog is empty.
/// </summary>
public static class SourceCatalog
{
    /// <summary>Register the available sources (in display order). Called once at startup, before
    /// the catalog is read.</summary>
    public static void Initialize(IReadOnlyList<IAudioSourceFactory> sources) => All = sources;

    /// <summary>Every source the plugins contribute, in display order. Empty until
    /// <see cref="Initialize"/> runs.</summary>
    public static IReadOnlyList<IAudioSourceFactory> All { get; private set; } = [];

    public static IAudioSourceFactory? Find(string id) =>
        All.FirstOrDefault(f => f.Id == id);
}

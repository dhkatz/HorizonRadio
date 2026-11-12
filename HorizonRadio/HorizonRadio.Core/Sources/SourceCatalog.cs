using HorizonRadio.Core.Hosting;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Every <see cref="IAudioSourceFactory"/> the plugins contribute, in display order: the UI's source
/// picker is bound to <see cref="All"/>; <see cref="Find"/> resolves an id to its factory, and
/// <see cref="Search"/> searches the ones that can be. A service built from the container (resolve it
/// once the tool catalog is filled: sources look their tools up as they're built).
/// </summary>
public sealed class SourceCatalog
{
    public SourceCatalog(IEnumerable<IAudioSourceFactory> sources)
    {
        All = sources.InDisplayOrder();
        Search = new UnifiedSearch(this);
    }

    /// <summary>No sources: the designer's, and a fallback where none are wired.</summary>
    public static SourceCatalog Empty { get; } = new([]);

    public IReadOnlyList<IAudioSourceFactory> All { get; }

    /// <summary>Searches the sources that can be searched.</summary>
    public UnifiedSearch Search { get; }

    public IAudioSourceFactory? Find(string id) => All.FirstOrDefault(f => f.Id == id);
}

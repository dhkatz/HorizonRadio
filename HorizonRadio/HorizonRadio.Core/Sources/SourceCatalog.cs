using HorizonRadio.Core.Hosting;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Every <see cref="IAudioSourceFactory"/> the plugins contribute, in display order: the UI's source
/// picker is bound to <see cref="All"/>; <see cref="Find"/> resolves an id to its factory, and
/// <see cref="Search"/> searches the ones that can be. A service built from the container (resolve it
/// once the tool catalog is filled: sources look their tools up as they're built).
/// </summary>
public sealed class SourceCatalog
{
    private readonly IToolLocator? _tools;

    /// <param name="tools">Finds the tools the sources run; null finds none (the designer's).</param>
    /// <param name="log">Where a failed search is reported; null drops it (the designer's).</param>
    public SourceCatalog(IEnumerable<IAudioSourceFactory> sources, IToolLocator? tools = null, IPluginLog? log = null)
    {
        All = sources.InDisplayOrder();
        Search = new UnifiedSearch(this, log ?? IPluginLog.None);
        _tools = tools;
    }

    /// <summary>No sources: the designer's, and a fallback where none are wired.</summary>
    public static SourceCatalog Empty { get; } = new([]);

    public IReadOnlyList<IAudioSourceFactory> All { get; }

    /// <summary>Searches the sources that can be searched.</summary>
    public UnifiedSearch Search { get; }

    public IAudioSourceFactory? Find(string id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>The tools <paramref name="factory"/> needs that can't be found with
    /// <paramref name="values"/> (<see cref="SourceRequirements.MissingToolKinds"/>).</summary>
    public IReadOnlyList<string> MissingToolKinds(IAudioSourceFactory factory, ConfigValues values) =>
        SourceRequirements.MissingToolKinds(_tools ?? NoTools.Instance, factory, values);

    /// <summary>Throws <see cref="MissingToolException"/> when <paramref name="factory"/> can't run for
    /// lack of its tools (<see cref="SourceRequirements.EnsureToolsAvailable"/>).</summary>
    public void EnsureToolsAvailable(IAudioSourceFactory factory, ConfigValues values) =>
        SourceRequirements.EnsureToolsAvailable(_tools ?? NoTools.Instance, factory, values);

    private sealed class NoTools : IToolLocator
    {
        public static NoTools Instance { get; } = new();
        public string? Find(string kind, string? configuredPath = null) => null;
        public string InstallPath(string kind) => throw new InvalidOperationException("No tools here.");
    }
}

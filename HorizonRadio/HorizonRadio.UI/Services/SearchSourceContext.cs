using System.Collections.Generic;
using System.Linq;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.UI.Services;

/// <summary>
/// Presentation context shared by every search result row: how to name a source, the
/// user's source priority (for the default play target on a merged row), and whether to
/// show source labels at all (only worth it once more than one source is searchable).
/// Built once from the catalog, since the searchable-source set is static; the priority follows its
/// setting (which another of your devices can change).
/// </summary>
public sealed class SearchSourceContext
{
    private readonly IReadOnlyDictionary<string, string> _names;
    private volatile IReadOnlyList<string> _priority;

    /// <summary>True when more than one source is searchable — labels/pickers only earn
    /// their keep then; with a single source the rows look exactly as before.</summary>
    public bool ShowLabels { get; }

    /// <summary>Searches the sources.</summary>
    public UnifiedSearch Search { get; }

    private SearchSourceContext(UnifiedSearch search,
        IReadOnlyDictionary<string, string> names, IReadOnlyList<string> priority, bool showLabels)
    {
        Search = search;
        _names = names;
        _priority = priority;
        ShowLabels = showLabels;
    }

    public static SearchSourceContext Build(SettingsStore settings, SourceCatalog catalog)
    {
        var sources = catalog.Search.SearchableSources;           // catalog order
        var names = sources.ToDictionary(s => s.Id, s => s.DisplayName);

        // User priority first, then any remaining searchable sources in catalog order, so
        // an unlisted/new source still has a defined (lowest) rank.
        List<string> Priority(string[] chosen) => chosen
            .Where(names.ContainsKey)
            .Concat(sources.Select(s => s.Id))
            .Distinct()
            .ToList();

        var context = new SearchSourceContext(catalog.Search, names, Priority(settings.Get(SourceSettings.SearchPriority)), sources.Count > 1);
        _ = settings.Watch(SourceSettings.SearchPriority, chosen => context._priority = Priority(chosen));
        return context;
    }

    public string NameFor(string sourceId) => _names.TryGetValue(sourceId, out var n) ? n : sourceId;

    /// <summary>Priority rank (lower = higher priority); unlisted sources sort last.</summary>
    public int RankOf(string sourceId)
    {
        var i = _priority is List<string> list ? list.IndexOf(sourceId) : -1;
        return i < 0 ? int.MaxValue : i;
    }
}

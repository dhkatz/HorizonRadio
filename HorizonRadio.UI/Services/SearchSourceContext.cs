using System.Collections.Generic;
using System.Linq;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.UI.Services;

/// <summary>
/// Presentation context shared by every search result row: how to name a source, the
/// user's source priority (for the default play target on a merged row), and whether to
/// show source labels at all (only worth it once more than one source is searchable).
/// Built once from the catalog + persisted config, since the searchable-source set is static.
/// </summary>
public sealed class SearchSourceContext
{
    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly IReadOnlyList<string> _priority;

    /// <summary>True when more than one source is searchable — labels/pickers only earn
    /// their keep then; with a single source the rows look exactly as before.</summary>
    public bool ShowLabels { get; }

    private SearchSourceContext(
        IReadOnlyDictionary<string, string> names, IReadOnlyList<string> priority, bool showLabels)
    {
        _names = names;
        _priority = priority;
        ShowLabels = showLabels;
    }

    public static SearchSourceContext Build(SourceConfigStore store)
    {
        var sources = UnifiedSearch.SearchableSources;            // catalog order
        var names = sources.ToDictionary(s => s.Id, s => s.DisplayName);

        // User priority first, then any remaining searchable sources in catalog order, so
        // an unlisted/new source still has a defined (lowest) rank.
        var priority = store.SearchSourcePriority
            .Where(names.ContainsKey)
            .Concat(sources.Select(s => s.Id))
            .Distinct()
            .ToList();

        return new SearchSourceContext(names, priority, sources.Count > 1);
    }

    public string NameFor(string sourceId) => _names.TryGetValue(sourceId, out var n) ? n : sourceId;

    /// <summary>Priority rank (lower = higher priority); unlisted sources sort last.</summary>
    public int RankOf(string sourceId)
    {
        var i = _priority is List<string> list ? list.IndexOf(sourceId) : -1;
        return i < 0 ? int.MaxValue : i;
    }
}

using System.Collections.Generic;
using System.Linq;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// How the resolver merges contributions: a priority-ordered list of contributor
/// ids (the user reorders these in the Metadata tab; the source is always a member),
/// plus optional per-field forced overrides ("always take Art from Spotify, even if
/// the source or an earlier provider has it").
///
/// Resolution per field: if a field is forced to a contributor and that contributor
/// supplied it, use that; otherwise walk <see cref="Order"/> and take the first
/// contributor that supplied the field. So by default (no forces) earlier
/// contributors win and later ones only fill gaps.
/// </summary>
public sealed record MetadataPolicy(
    IReadOnlyList<string> Order,
    IReadOnlyDictionary<MetadataField, string> Forced)
{
    /// <summary>The synthetic contributor id for the source's own metadata; always
    /// present in <see cref="Order"/>.</summary>
    public const string SourceId = "source";

    public static MetadataPolicy Empty { get; } =
        new([SourceId], new Dictionary<MetadataField, string>());

    /// <summary>Source first, then the given network contributors in registry
    /// order — so providers fill what the source is missing, no forced overrides.</summary>
    public static MetadataPolicy Default(IEnumerable<string> contributorIds) =>
        new([SourceId, .. contributorIds.Where(id => id != SourceId)],
            new Dictionary<MetadataField, string>());

    /// <summary>The forced contributor for a field, or null for "auto" (use order).</summary>
    public string? ForcedFor(MetadataField field) =>
        Forced.TryGetValue(field, out var id) ? id : null;
}

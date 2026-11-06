namespace HorizonRadio.Plugins.Abstractions;

/// <summary>
/// Base contract for a plugin — a unit the host discovers and lists. Concrete plugin kinds
/// (metadata, source, game) extend this with the capabilities they contribute, so the host can
/// enumerate plugins generically while each kind exposes its own factories.
/// </summary>
public interface IPlugin
{
    /// <summary>Stable, lowercase id.</summary>
    string Id { get; }

    /// <summary>User-facing label.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Relative position among plugins of the same kind, ascending — the host orders the discovered
    /// plugins by this (then by <see cref="Id"/> as a stable tiebreak) so the source picker and
    /// metadata provider list have a deterministic order even though assembly-scan discovery does not.
    /// First-party plugins set explicit values to preserve their shipped order; third-party plugins
    /// default to the end.
    /// </summary>
    int SortOrder => int.MaxValue;
}

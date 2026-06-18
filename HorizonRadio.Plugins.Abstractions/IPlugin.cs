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
}

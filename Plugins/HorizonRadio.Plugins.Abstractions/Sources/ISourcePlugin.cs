using HorizonRadio.Core.Sources;

namespace HorizonRadio.Plugins.Abstractions;

/// <summary>
/// A source plugin: contributes one or more <see cref="IAudioSourceFactory"/> the host can run,
/// search, mix, and queue. The host discovers source plugins and aggregates their factories into
/// the source catalog. (A single plugin may expose more than one factory — e.g. Spotify ships both
/// the zero-config Connect receiver and the driven, mixable source.)
/// </summary>
public interface ISourcePlugin : IPlugin
{
    /// <summary>The source factories this plugin contributes, in display order.</summary>
    IReadOnlyList<IAudioSourceFactory> Sources { get; }
}

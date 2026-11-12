using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Sdk;

/// <summary>
/// Registers a plugin's contributions and services into the host's container; usually the
/// <see cref="IPlugin"/> class itself. The host finds every module in the plugin assemblies (it needs
/// a public parameterless constructor) and calls it once at startup, after the host's own services,
/// so a module can:
/// <list type="bullet">
///   <item>contribute to what the host collects: a source (<see cref="Sources.IAudioSourceFactory"/>),
///     a metadata provider (<see cref="Metadata.IMetadataProviderFactory"/>), a lyrics provider
///     (<see cref="Lyrics.ILyricsProvider"/>), each alongside the others;</item>
///   <item>add the services those take in their constructors (a connection, a client);</item>
///   <item>replace a host service (register its own <see cref="IProcesses"/>, say), or wrap it
///     (<see cref="PluginServiceCollectionExtensions.Decorate{TService}"/>).</item>
/// </list>
/// Register by type (<c>AddSingleton&lt;IAudioSourceFactory, YouTubeSourceFactory&gt;()</c>) where
/// you can: the container builds it, passing what its constructor asks for, and checks at startup
/// that everything it asks for is registered. The plugin class itself is registered by the host.
///
/// Modules run in <see cref="Order"/>, then by type name. For a service resolved one at a time,
/// the last registration wins, so a later module's replacement wins over an earlier one's; the
/// host logs every host service a module replaces or wraps.
/// </summary>
public interface IPluginModule
{
    /// <summary>When this module runs relative to the others, ascending. Leave it unless a module
    /// must replace a service after another module has.</summary>
    int Order => 0;

    void ConfigureServices(IServiceCollection services);
}

using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Sdk;

/// <summary>
/// Registers a plugin's services into the host's container. The host finds every module in the
/// plugin assemblies (it needs a public parameterless constructor) and calls it once at startup,
/// after the host's own services, so a module can:
/// <list type="bullet">
///   <item>add the services its plugin classes take in their constructors (a connection, a client);</item>
///   <item>add another implementation of a contract the host collects (a source factory, a
///     metadata provider), alongside the others;</item>
///   <item>replace a host service (register its own <see cref="IProcesses"/>, say), or wrap it
///     (<see cref="PluginServiceCollectionExtensions.Decorate{TService}"/>).</item>
/// </list>
/// The plugin classes themselves (<see cref="ISourcePlugin"/>, <see cref="IMetadataPlugin"/>,
/// <see cref="ILyricsPlugin"/>, <see cref="IGamePlugin"/>) are found and registered by the host;
/// a module doesn't register them. They're built by the container, so their constructors can ask
/// for host services and the module's.
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

using HorizonRadio.Core.Events;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core;

/// <summary>
/// Registers the Core engine's services into a DI container. This is the first step of the
/// plugin-system migration: the composition root (the UI's <c>App</c>) builds a container and
/// resolves these instead of hand-constructing them, so ownership and lifetime move to DI
/// incrementally without changing behavior.
///
/// For now it registers the leaf singletons that are safe to let the container own: the persisted
/// config stores, the metadata cache, and the mix content resolver — all non-disposable (or
/// load-from-disk) with no hand-tuned shutdown order, via the exact same calls the root used before,
/// so resolution is behavior-identical. The heavier engine services (source runner, queue, resolver,
/// IPC, Spotify, …) stay App-owned for now: App disposes them in a specific order at shutdown, which
/// container-managed disposal would not preserve. They move once that ordering is handled.
///
/// It also registers the host's side of the plugin SDK: the services plugin constructors take
/// (<see cref="IPluginLog"/>, <see cref="IProcesses"/>, <see cref="IAppFolders"/>,
/// <see cref="IToolLocator"/>, <see cref="IMetadataCache"/>), each of which a plugin module may
/// replace (<see cref="Hosting.PluginRegistration.AddPlugins"/>).
/// </summary>
public static class HorizonCoreServiceCollectionExtensions
{
    /// <param name="settings">The app's settings, opened before the container (startup reads them
    /// first). The caller owns it: the container doesn't dispose an instance it was given.</param>
    public static IServiceCollection AddHorizonCore(this IServiceCollection services, SettingsStore settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(_ => new SourceConfigStore(settings));
        services.AddSingleton(_ => new MetadataConfigStore(settings));
        services.AddSingleton(_ => MixStore.LoadFromDisk());
        services.AddSingleton(_ => PlayHistoryStore.LoadFromDisk());
        services.AddSingleton(_ => new EventRuleStore(settings));
        services.AddSingleton(_ => new InputBindingStore(settings));
        services.AddSingleton(_ => new MetadataCache());
        services.AddSingleton<IMetadataCache>(sp => sp.GetRequiredService<MetadataCache>());

        // What plugins take in their constructors, as the host provides it. A plugin module can
        // replace or wrap any of these (see PluginRegistration.AddPlugins).
        services.AddSingleton<IPluginLog, HostPluginLog>();
        services.AddSingleton<IProcesses, HostProcesses>();
        services.AddSingleton<IAppFolders, HostAppFolders>();
        services.AddSingleton<IToolLocator, HostToolLocator>();

        // Resolves a mix entry to playable content from the source config; non-disposable and its
        // only dependency (the source config store) is registered above, so it's safe to own here.
        services.AddSingleton(sp => new MixContentResolver(sp.GetRequiredService<SourceConfigStore>()));
        return services;
    }
}

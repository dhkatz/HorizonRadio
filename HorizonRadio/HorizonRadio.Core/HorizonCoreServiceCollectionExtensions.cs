using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Fingerprints;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core;

/// <summary>
/// Registers Core's services: the stores (settings, sources, mixes, history, event rules, bindings),
/// the catalogs (sources, metadata, tools), the caches, and the inputs bound actions listen to. The
/// engine (<see cref="Hosting.HorizonEngine"/>) is built from them for the game that runs.
///
/// It also registers the host's side of the plugin SDK: the services plugin constructors take
/// (<see cref="IPluginLog"/>, <see cref="IProcesses"/>, <see cref="IAppFolders"/>,
/// <see cref="IToolLocator"/>, <see cref="IMetadataCache"/>, <see cref="IMetadataTrace"/>,
/// <see cref="IAudioCaches"/>, <see cref="ISourceSettings"/>, <see cref="ITitleModel"/>,
/// <see cref="IPluginSettingsStore"/>, <see cref="IConfirmations"/>, <see cref="INotifications"/>,
/// <see cref="ISecretProtector"/>),
/// each of which a plugin module may replace (<see cref="Hosting.PluginRegistration.AddPlugins"/>).
/// Core's own services take them from the container too, so a replacement reaches Core as well.
/// </summary>
public static class HorizonCoreServiceCollectionExtensions
{
    /// <param name="settings">The app's settings, opened before the container (startup reads them
    /// first). The caller owns it: the container doesn't dispose an instance it was given.</param>
    /// <param name="folders">Where the stores and caches keep their files (the profile's folder, or
    /// a test's). A module may replace it; Core's stores take it from the container, so they follow.</param>
    public static IServiceCollection AddHorizonCore(this IServiceCollection services, SettingsStore settings,
        IAppFolders folders)
    {
        services.AddSingleton(settings);
        services.AddSingleton(_ => new SourceConfigStore(settings));
        services.AddSingleton<ToolCatalog>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ToolInstalls>();
        services.AddSingleton<SourceCatalog>();
        services.AddSingleton<MetadataCatalog>();
        services.AddSingleton(sp => new MetadataConfigStore(settings, sp.GetRequiredService<MetadataCatalog>()));
        services.AddSingleton(sp => MixStore.LoadFromDisk(sp.GetRequiredService<IAppFolders>().Data("mixes.json")));
        services.AddSingleton(sp => PlayHistoryStore.LoadFromDisk(sp.GetRequiredService<IAppFolders>().Data("history.json")));
        services.AddSingleton(sp => new MixEntryInfoStore(sp.GetRequiredService<IAppFolders>().Data("mix-entries")));
        services.AddSingleton(sp => new ReferenceStore(sp.GetRequiredService<IAppFolders>().Data("fingerprints")));
        services.AddSingleton(_ => new EventRuleStore(settings));
        services.AddSingleton(_ => new InputBindingStore(settings));
        // Global keyboard and mouse, and controllers: what bound inputs listen to (the engine's
        // InputBindingService starts and stops them). A module can add one, or a host drop them.
        services.AddSingleton<IInputBackend, SharpHookBackend>();
        services.AddSingleton<IInputBackend, SdlInputBackend>();
        services.AddSingleton(sp => new MetadataCache(sp.GetRequiredService<IAppFolders>().Data("metadata")));
        services.AddSingleton<IMetadataCache>(sp => sp.GetRequiredService<MetadataCache>());
        // What reaches the OS, portable defaults: the platform (IHostPlatform) registers its own.
        services.AddSingleton<ISpeakers, NoSpeakers>();
        services.AddSingleton(ISecretProtector.None);

        // What plugins take in their constructors, as the host provides it. A plugin module can
        // replace or wrap any of these (see PluginRegistration.AddPlugins).
        services.AddSingleton<IPluginLog, HostPluginLog>();
        services.AddSingleton<IProcesses, HostProcesses>();
        services.AddSingleton(folders);
        services.AddSingleton<IToolLocator, HostToolLocator>();
        // The metadata diagnostics (About → diagnostics): the resolver records each lookup in it, and
        // what sources and providers record reaches it as the IMetadataTrace they take.
        services.AddSingleton<MetadataTrace>();
        services.AddSingleton<IMetadataTrace>(sp => sp.GetRequiredService<MetadataTrace>());
        services.AddSingleton<IAudioCaches, HostAudioCaches>();
        services.AddSingleton<ISourceSettings, HostSourceSettings>();
        services.AddSingleton<ITitleModel, HostTitleModel>();
        services.AddSingleton<IPluginSettingsStore, HostPluginSettingsStore>();
        // Nothing to ask with, headless: a front end with dialogs replaces it.
        services.AddSingleton<IConfirmations>(ProceedWithoutAsking.Instance);
        services.AddSingleton<INotifications, LoggedNotifications>();

        // Resolves a mix entry to playable content from the source config; non-disposable and its
        // only dependency (the source config store) is registered above, so it's safe to own here.
        services.AddSingleton(sp => new MixContentResolver(
            sp.GetRequiredService<SourceConfigStore>(), sp.GetRequiredService<SourceCatalog>()));
        return services;
    }
}

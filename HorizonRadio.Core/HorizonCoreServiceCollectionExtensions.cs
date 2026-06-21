using HorizonRadio.Core.Events;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
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
/// </summary>
public static class HorizonCoreServiceCollectionExtensions
{
    public static IServiceCollection AddHorizonCore(this IServiceCollection services)
    {
        services.AddSingleton(_ => SourceConfigStore.LoadFromDisk());
        services.AddSingleton(_ => MetadataConfigStore.LoadFromDisk());
        services.AddSingleton(_ => MixStore.LoadFromDisk());
        services.AddSingleton(_ => PlayHistoryStore.LoadFromDisk());
        services.AddSingleton(_ => EventRuleStore.LoadFromDisk());
        services.AddSingleton(_ => InputBindingStore.LoadFromDisk());
        services.AddSingleton(_ => new MetadataCache());

        // Resolves a mix entry to playable content from the source config; non-disposable and its
        // only dependency (the source config store) is registered above, so it's safe to own here.
        services.AddSingleton(sp => new MixContentResolver(sp.GetRequiredService<SourceConfigStore>()));
        return services;
    }
}

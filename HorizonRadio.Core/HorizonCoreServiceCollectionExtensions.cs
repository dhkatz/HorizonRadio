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
/// For now it registers only the leaf, dependency-free singletons — the persisted config stores
/// and the metadata cache — via the exact same <c>LoadFromDisk</c>/constructor calls the root
/// used before, so resolution is behavior-identical. Heavier services (the resolver, source
/// runner, queue, …) and the plugin registries move into DI in later steps.
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
        return services;
    }
}

using HorizonRadio.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HorizonRadio.Core.Hosting;

/// <summary>
/// Puts the plugins into the host's container: each plugin class, under every plugin kind it is,
/// built by the container (so its constructor can ask for services); then each
/// <see cref="IPluginModule"/>'s own registrations, which may replace or wrap the host's.
/// </summary>
public static class PluginRegistration
{
    /// <summary>The plugin kinds the host collects. A plugin may be several (VocaDB is a metadata
    /// and a lyrics plugin): it's built once and listed under each.</summary>
    public static IReadOnlyList<Type> Kinds { get; } =
        [typeof(ISourcePlugin), typeof(IMetadataPlugin), typeof(ILyricsPlugin), typeof(IGamePlugin)];

    /// <summary>
    /// Registers the plugin classes and modules among <paramref name="types"/> (others are
    /// ignored). Call it after the host's own services: those are what a module can replace, and
    /// <paramref name="log"/> hears of each it does, and of two modules replacing the same one.
    /// </summary>
    /// <exception cref="InvalidOperationException">A module has no parameterless constructor.</exception>
    public static IServiceCollection AddPlugins(
        this IServiceCollection services, IEnumerable<Type> types, Action<string>? log = null)
    {
        var candidates = types
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
        var hostServices = services.Where(d => !d.IsKeyedService).Select(d => d.ServiceType).ToHashSet();

        foreach (var type in candidates)
        {
            var kinds = Kinds.Where(k => k.IsAssignableFrom(type)).ToList();
            if (kinds.Count == 0) continue;
            services.TryAddSingleton(type);
            foreach (var kind in kinds)
                services.AddSingleton(kind, sp => sp.GetRequiredService(type));
        }

        var modules = candidates
            .Where(t => typeof(IPluginModule).IsAssignableFrom(t))
            .Select(CreateModule)
            .OrderBy(m => m.Order)
            .ThenBy(m => m.GetType().FullName, StringComparer.Ordinal);
        var replacedBy = new Dictionary<Type, string>();
        foreach (var module in modules)
        {
            var name = ModuleName(module);
            var before = Snapshot(services);
            module.ConfigureServices(services);
            foreach (var (service, change) in Changes(before, services, hostServices))
            {
                log?.Invoke($"{name} {change} {service.Name}");
                if (change != "replaces") continue;
                if (replacedBy.TryGetValue(service, out var earlier))
                    log?.Invoke($"warning: {earlier} and {name} both replace {service.Name}; {name}'s is used");
                replacedBy[service] = name;
            }
        }
        return services;
    }

    /// <summary>Plugins in the order they're shown: <see cref="IPlugin.SortOrder"/>, then id.</summary>
    public static IReadOnlyList<T> InDisplayOrder<T>(this IEnumerable<T> plugins) where T : IPlugin =>
        [.. plugins.OrderBy(p => p.SortOrder).ThenBy(p => p.Id, StringComparer.Ordinal)];

    private static IPluginModule CreateModule(Type type) =>
        type.GetConstructor(Type.EmptyTypes) is { } ctor
            ? (IPluginModule)ctor.Invoke(null)
            : throw new InvalidOperationException(
                $"The plugin module {type.FullName} needs a public parameterless constructor; " +
                "the services it needs go in the constructors of the classes it registers.");

    // "youtube (HorizonRadio.Source.YouTube.YouTubeModule)" reads worse than the assembly name,
    // which is what a user can match to a plugin.
    private static string ModuleName(IPluginModule module) =>
        module.GetType().Assembly.GetName().Name ?? module.GetType().Name;

    private static Dictionary<Type, ServiceDescriptor[]> Snapshot(IServiceCollection services) =>
        services.Where(d => !d.IsKeyedService)
            .GroupBy(d => d.ServiceType)
            .ToDictionary(g => g.Key, g => g.ToArray());

    // What a module did to each host service: registered another (the last one wins: "replaces"),
    // or swapped the registration in place ("wraps", which is what Decorate does).
    private static IEnumerable<(Type Service, string Change)> Changes(
        Dictionary<Type, ServiceDescriptor[]> before, IServiceCollection services, HashSet<Type> hostServices)
    {
        foreach (var (service, after) in Snapshot(services))
        {
            if (!hostServices.Contains(service)) continue;
            var was = before.GetValueOrDefault(service) ?? [];
            if (after.Length > was.Length) yield return (service, "replaces");
            else if (!after.SequenceEqual(was)) yield return (service, "wraps");
        }
    }
}

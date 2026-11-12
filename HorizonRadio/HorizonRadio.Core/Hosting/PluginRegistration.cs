using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HorizonRadio.Core.Hosting;

/// <summary>
/// Puts the plugins into the host's container: each plugin, listed as an <see cref="IPlugin"/>
/// (and an <see cref="IGamePlugin"/> or <see cref="IToolPlugin"/> if it is one); then each <see cref="IPluginModule"/>'s
/// registrations: the plugin's contributions (sources, metadata and lyrics providers), the services
/// they use, and any host service it replaces or wraps.
/// </summary>
public static class PluginRegistration
{
    /// <summary>What the host lists a plugin class as.</summary>
    public static IReadOnlyList<Type> Kinds { get; } = [typeof(IPlugin), typeof(IGamePlugin), typeof(IToolPlugin)];

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

        // A module is created here, to call it; a plugin that is its own module (the usual shape)
        // is listed as that instance. Any other plugin class (a game) the container builds, so its
        // constructor can take services.
        var modules = candidates
            .Where(t => typeof(IPluginModule).IsAssignableFrom(t))
            .Select(CreateModule)
            .ToList();
        foreach (var module in modules)
        {
            foreach (var kind in Kinds.Where(k => k.IsInstanceOfType(module)))
                services.AddSingleton(kind, module);
        }
        foreach (var type in candidates.Where(t => !typeof(IPluginModule).IsAssignableFrom(t)))
        {
            var kinds = Kinds.Where(k => k.IsAssignableFrom(type)).ToList();
            if (kinds.Count == 0) continue;
            services.TryAddSingleton(type);
            foreach (var kind in kinds)
                services.AddSingleton(kind, sp => sp.GetRequiredService(type));
        }

        var replacedBy = new Dictionary<Type, string>();
        foreach (var module in modules.OrderBy(m => m.Order).ThenBy(m => m.GetType().FullName, StringComparer.Ordinal))
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

    /// <summary>Plugins or contributions in the order they're shown (and, for lyrics, asked):
    /// <see cref="IOrdered.SortOrder"/>, then id.</summary>
    public static IReadOnlyList<T> InDisplayOrder<T>(this IEnumerable<T> items) where T : IOrdered =>
        [.. items.OrderBy(p => p.SortOrder).ThenBy(p => p.Id, StringComparer.Ordinal)];

    private static IPluginModule CreateModule(Type type) =>
        type.GetConstructor(Type.EmptyTypes) is { } ctor
            ? (IPluginModule)ctor.Invoke(null)
            : throw new InvalidOperationException(
                $"The plugin module {type.FullName} needs a public parameterless constructor; " +
                "the services it needs go in the constructors of the classes it registers.");

    // The plugin's id when the module is the plugin; else its assembly, which a user can match to one.
    private static string ModuleName(IPluginModule module) =>
        module is IPlugin plugin ? plugin.Id : module.GetType().Assembly.GetName().Name ?? module.GetType().Name;

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

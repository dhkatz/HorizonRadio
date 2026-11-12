using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Sdk;

/// <summary>Helpers for <see cref="IPluginModule.ConfigureServices"/>.</summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Wraps the registered <typeparamref name="TService"/> (the one resolved now: the last
    /// registration) in <paramref name="decorate"/>, which gets the existing service and the
    /// container and returns the one to use instead: to log what passes through it, add to what it
    /// does, or hand some calls elsewhere. The wrapper keeps the existing service's lifetime, and the
    /// existing one is still built and disposed as before.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing registers <typeparamref name="TService"/>.</exception>
    public static IServiceCollection Decorate<TService>(
        this IServiceCollection services, Func<TService, IServiceProvider, TService> decorate)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);

        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService) && !services[i].IsKeyedService)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            throw new InvalidOperationException($"No {typeof(TService).Name} is registered to decorate.");

        // The existing registration moves under a key only this wrapper knows, so the container
        // keeps building (and disposing) it, and the wrapper takes its place in the same position.
        var existing = services[index];
        var key = new object();
        services.Add(Keyed(existing, key));
        services[index] = ServiceDescriptor.Describe(
            typeof(TService),
            sp => decorate(sp.GetRequiredKeyedService<TService>(key), sp),
            existing.Lifetime);
        return services;
    }

    private static ServiceDescriptor Keyed(ServiceDescriptor d, object key) =>
        d.ImplementationInstance is { } instance ? new ServiceDescriptor(d.ServiceType, key, instance)
        : d.ImplementationFactory is { } factory ? new ServiceDescriptor(d.ServiceType, key, (sp, _) => factory(sp), d.Lifetime)
        : new ServiceDescriptor(d.ServiceType, key, d.ImplementationType!, d.Lifetime);
}

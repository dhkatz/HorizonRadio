using HorizonRadio.Core.Metadata;
using HorizonRadio.Plugins.Abstractions;
using HorizonRadio.UI.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.UI.Services;

/// <summary>
/// Registers the UI host's leaf services into the DI container, alongside Core's
/// <c>AddHorizonCore</c>. Only services that are safe for the container to own live here — they're
/// non-disposable and depend solely on what's already registered, so moving them off App's manual
/// construction can't change the hand-tuned shutdown order. The heavier UI/engine objects and the
/// view-model tree stay App-owned for now (see the App composition root).
/// </summary>
public static class HorizonUiServiceCollectionExtensions
{
    public static IServiceCollection AddHorizonUiServices(this IServiceCollection services)
    {
        // Scans the managed tools dir for installed tools; rescans on demand. Parameterless,
        // non-disposable. Reads ToolCatalog, which Program seeds before the container is built.
        services.AddSingleton<ToolRegistry>();

        // The host's IPluginContext handed to plugin factories. Non-disposable; its cache dependency
        // is registered by AddHorizonCore.
        services.AddSingleton<IPluginContext>(sp => new HostPluginContext(sp.GetRequiredService<MetadataCache>()));
        return services;
    }
}

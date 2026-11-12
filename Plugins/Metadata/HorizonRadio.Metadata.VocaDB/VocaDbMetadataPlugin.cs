using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Metadata.VocaDb;

/// <summary>The VocaDB plugin: contributes the VocaDB metadata provider, and VocaDB's lyrics
/// (original, romanized, translated; never timed, so after the synced lyrics providers).</summary>
public sealed class VocaDbMetadataPlugin : IPlugin, IPluginModule
{
    public string Id => "vocadb";
    public string DisplayName => "VocaDB";
    public int SortOrder => 40;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMetadataProviderFactory, VocaDbProviderFactory>();
        // The lyrics provider has its own VocaDB client, which it owns (and disposes).
        services.AddSingleton<ILyricsProvider>(sp => new VocaDbLyricsProvider(new VocaDbProvider(
            sp.GetRequiredService<IMetadataCache>(), sp.GetRequiredService<IMetadataTrace>())));
    }
}

using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Lyrics.Unison;

/// <summary>The Unison lyrics plugin. First: its lyrics are often synced word by word.</summary>
public sealed class UnisonLyricsPlugin : IPlugin, IPluginModule
{
    public string Id => "unison";
    public string DisplayName => "Unison";
    public int SortOrder => 10;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILyricsProvider, UnisonProvider>();
    }
}

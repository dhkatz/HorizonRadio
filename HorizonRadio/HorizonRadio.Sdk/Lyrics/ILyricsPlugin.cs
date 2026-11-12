using HorizonRadio.Sdk.Lyrics;

namespace HorizonRadio.Sdk;

/// <summary>
/// A lyrics plugin: contributes one <see cref="ILyricsProvider"/>. The host asks the providers in
/// <see cref="IPlugin.SortOrder"/> order and prefers synced lyrics, so put a provider whose lyrics are
/// timed more carefully first.
/// </summary>
public interface ILyricsPlugin : IPlugin
{
    ILyricsProvider CreateLyricsProvider(IPluginContext context);
}

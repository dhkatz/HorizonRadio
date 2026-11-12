namespace HorizonRadio.Sdk;

/// <summary>
/// A plugin: the unit the host discovers and lists, named here. What it adds to the app it
/// registers as an <see cref="IPluginModule"/> (usually the same class): sources
/// (<see cref="Sources.IAudioSourceFactory"/>), metadata providers
/// (<see cref="Metadata.IMetadataProviderFactory"/>), lyrics providers
/// (<see cref="Lyrics.ILyricsProvider"/>), and the services they use. A game is the exception for
/// now: an <see cref="IGamePlugin"/> builds its integration itself.
/// <code>
/// public sealed class YouTubePlugin : IPlugin, IPluginModule
/// {
///     public string Id => "youtube";
///     public string DisplayName => "YouTube";
///     public void ConfigureServices(IServiceCollection services) =>
///         services.AddSingleton&lt;IAudioSourceFactory, YouTubeSourceFactory&gt;();
/// }
/// </code>
/// </summary>
public interface IPlugin : IOrdered
{
    /// <summary>User-facing label.</summary>
    string DisplayName { get; }
}

using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Sdk.Tools;
using HorizonRadio.Tools.Librespot;
using HorizonRadio.Tools.Soloist;

namespace HorizonRadio.Source.Spotify;

/// <summary>
/// The driven Spotify source's settings: their schema (the Sources page's form), and what they
/// mean for the services Spotify plays through. Read fresh each time, so installing librespot or
/// Soloist, or editing a setting, applies at the next launch without a restart. A service of its own
/// (not the factory's) so the connection and players can be built from it before the factory is.
/// </summary>
public sealed class SpotifySettings(IToolLocator tools, IAppFolders folders, ISourceSettings settings)
{
    public IReadOnlyList<ConfigField> Schema { get; } = BuildSchema(tools, folders);

    /// <summary>The settings as saved now.</summary>
    public ConfigValues Load() => settings.Load(SpotifyContentSourceFactory.SourceId, Schema);

    /// <summary>The Client ID of the user's Spotify app (empty until they've entered one).</summary>
    public string ClientId => Load().GetString(SpotifyContentSourceFactory.KeyClientId) ?? "";

    /// <summary>How to start librespot. A path left empty falls back to the copy installed from the
    /// Tools tab (installed after the schema's default was first saved).</summary>
    public LibrespotOptions Librespot()
    {
        var cfg = Load();
        return new LibrespotOptions
        {
            ExecutablePath = Or(cfg.GetString(SpotifyContentSourceFactory.KeyExecutable), Tools.Librespot.Librespot.DiscoverExe(tools) ?? ""),
            DeviceName = Or(cfg.GetString(SpotifyContentSourceFactory.KeyDeviceName), Tools.Librespot.Librespot.DefaultDeviceName(folders)),
            CacheDirectory = Or(cfg.GetString(SpotifyContentSourceFactory.KeyCacheDir), Tools.Librespot.Librespot.DefaultCacheDir(folders)),
            Bitrate = cfg.GetString(SpotifyContentSourceFactory.KeyBitrate) ?? "auto",
            EnableVolumeNormalisation = cfg.GetBool(SpotifyContentSourceFactory.KeyNormalise, true),
        };
    }

    /// <summary>How to start Soloist, or null while it isn't set up (no key, or no soloist.exe).</summary>
    public SoloistOptions? Soloist()
    {
        var cfg = Load();
        var key = cfg.GetString(SpotifyContentSourceFactory.KeySoloistKey)?.Trim();
        var exe = Or(cfg.GetString(SpotifyContentSourceFactory.KeySoloistExecutable), Tools.Soloist.Soloist.DiscoverExe(tools) ?? "");
        if (string.IsNullOrEmpty(key) || !File.Exists(exe)) return null;
        return new SoloistOptions
        {
            ExecutablePath = exe,
            ApiKey = key,
            DeviceName = Tools.Soloist.Soloist.DefaultDeviceName(folders),
            DataDirectory = Tools.Soloist.Soloist.DefaultDataDir(folders),
        };
    }

    /// <summary>The name Soloist shows in the Spotify app.</summary>
    public string SoloistDeviceName => Tools.Soloist.Soloist.DefaultDeviceName(folders);

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static IReadOnlyList<ConfigField> BuildSchema(IToolLocator tools, IAppFolders folders)
    {
        var defaultExe = Tools.Librespot.Librespot.DiscoverExe(tools) ?? "";
        var defaultCache = Tools.Librespot.Librespot.DefaultCacheDir(folders);

        return
        [
            new TextField(
                Key:         SpotifyContentSourceFactory.KeyUri,
                Label:       "Spotify link",
                Placeholder: SpotifyContentSourceFactory.LocatorHintText,
                Description: "A track, playlist, or album. Used for Quick Play and mix entries."),

            new TextField(
                Key:         SpotifyContentSourceFactory.KeyClientId,
                Label:       "Spotify Client ID",
                Placeholder: "from developer.spotify.com → your app",
                Description: "Your own Spotify app's Client ID. Create an app, add the redirect " +
                             "URI shown when you connect, then paste the Client ID here.")
                { IsEnvironment = true },

            new ToolField(
                Key:         SpotifyContentSourceFactory.KeyExecutable,
                Label:       "librespot.exe path",
                ToolKind:    ToolKind.Librespot,
                Default:     defaultExe,
                Description: "Install via the Tools tab, or point at an existing librespot.exe."),

            new TextField(
                Key:         SpotifyContentSourceFactory.KeyDeviceName,
                Label:       "Device name",
                Default:     Tools.Librespot.Librespot.DefaultDeviceName(folders),
                Placeholder: Tools.Librespot.Librespot.DefaultDeviceName(folders),
                Description: "The Connect device librespot registers. The first time, cast to it " +
                             "once from your Spotify app to log it in."),

            new DirectoryField(
                Key:         SpotifyContentSourceFactory.KeyCacheDir,
                Label:       "Cache directory",
                Default:     defaultCache,
                Description: "librespot's login + audio cache, so it stays logged in across restarts.")
                { IsEnvironment = true },

            new EnumField(
                Key:         SpotifyContentSourceFactory.KeyBitrate,
                Label:       "Bitrate",
                Options:     SpotifyContentSourceFactory.BitrateOptions,
                Default:     "auto",
                Description: "Auto lets librespot pick the highest the account is licensed for."),

            new BoolField(
                Key:         SpotifyContentSourceFactory.KeyNormalise,
                Label:       "Volume normalisation",
                Default:     true,
                Description: "Spotify's per-track ReplayGain. Keeps loud and quiet tracks consistent."),

            new TextField(
                Key:         SpotifyContentSourceFactory.KeySoloistKey,
                Label:       "Spotify Soloist key",
                IsSecret:    true,
                Placeholder: "from developer.spotify.com/dashboard/soloist",
                Description: "Spotify refuses librespot the audio of some tracks, for some accounts all of them. " +
                             "With your own Soloist key (Premium), those tracks play through Spotify Soloist, " +
                             "Spotify's own Connect client, instead: Horizon Radio takes its audio inside Soloist " +
                             "so it plays in game, not out loud. That's against Spotify's developer terms and " +
                             "could get the key revoked. Stored encrypted for your Windows account.")
                { IsEnvironment = true },

            new ToolField(
                Key:         SpotifyContentSourceFactory.KeySoloistExecutable,
                Label:       "soloist.exe path",
                ToolKind:    ToolKind.Soloist,
                Default:     Tools.Soloist.Soloist.DiscoverExe(tools) ?? "",
                Description: "Install via the Tools tab (downloaded from Spotify), or point at an existing soloist.exe."),
        ];
    }
}

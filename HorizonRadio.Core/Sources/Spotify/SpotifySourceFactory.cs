using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Factory for <see cref="SpotifySource"/>. Exposes the
/// commonly-tweaked librespot knobs as schema fields so users don't
/// have to read CLI docs to change them.
/// </summary>
public sealed class SpotifySourceFactory : IAudioSourceFactory
{
    public const string KeyExecutable = "executable";
    public const string KeyDeviceName = "deviceName";
    public const string KeyCacheDir = "cacheDir";
    public const string KeyBitrate = "bitrate";
    public const string KeyNormalise = "normalise";

    private static readonly string[] ExeExtensions = ["exe"];
    private static readonly string[] BitrateOptions = ["auto", "96", "160", "320"];

    public string Id => "spotify";
    public string DisplayName => "Spotify Connect";
    public string? Description => "Stream from Spotify via librespot. Cast from your Spotify app to the configured device name.";

    public IReadOnlyList<ConfigField> Schema { get; }

    public SpotifySourceFactory()
    {
        var defaultExe = DiscoverLibrespotExe() ?? "";
        var defaultCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio", "librespot");

        Schema = new ConfigField[]
        {
            // librespot.exe and the cache dir are machine/environment config —
            // flagged so source profiles don't freeze them; they come from the
            // global per-source config at launch.
            new FileField(
                Key:             KeyExecutable,
                Label:           "librespot.exe path",
                ExtensionFilter: ExeExtensions,
                Default:         defaultExe,
                Description:     "Full path to librespot.exe. Bundled copy auto-detected if it lives next to the UI.")
                { IsEnvironment = true },

            new TextField(
                Key:         KeyDeviceName,
                Label:       "Cast device name",
                Default:     "Horizon Radio",
                Placeholder: "Horizon Radio",
                Description: "Shown in your Spotify app's Connect device list."),

            new DirectoryField(
                Key:         KeyCacheDir,
                Label:       "Cache directory",
                Default:     defaultCache,
                Description: "librespot's OAuth + audio cache. Login is cached here so re-handshake isn't required on restart.")
                { IsEnvironment = true },

            new EnumField(
                Key:         KeyBitrate,
                Label:       "Bitrate",
                Options:     BitrateOptions,
                Default:     "auto",
                Description: "Auto lets librespot pick the highest the account is licensed for. Forcing 320 on Free can cause skip-on-play."),

            new BoolField(
                Key:         KeyNormalise,
                Label:       "Volume normalisation",
                Default:     true,
                Description: "Spotify's per-track ReplayGain. Keeps hot and quiet tracks at consistent loudness."),
        };
    }

    public IAudioSource Create(ConfigValues values)
    {
        var exe = values.GetString(KeyExecutable);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidOperationException("Spotify: pick a librespot.exe path.");

        var device = values.GetString(KeyDeviceName);
        if (string.IsNullOrWhiteSpace(device)) device = "Horizon Radio";

        var cache = values.GetString(KeyCacheDir);
        if (string.IsNullOrWhiteSpace(cache))
            cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HorizonRadio", "librespot");

        var bitrate = values.GetString(KeyBitrate) ?? "auto";
        var norm = values.GetBool(KeyNormalise, true);

        return new SpotifySource(new SpotifyOptions
        {
            ExecutablePath = exe,
            DeviceName = device!,
            CacheDirectory = cache!,
            Bitrate = bitrate,
            EnableVolumeNormalisation = norm,
        });
    }

    private static string? DiscoverLibrespotExe()
    {
        var here = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(here, "librespot.exe"),
            Path.Combine(here, "..", "..", "..", "..", "build", "Librespot", "bin", "librespot.exe"),
            Path.Combine(here, "..", "..", "..", "..", "..", "build", "Librespot", "bin", "librespot.exe"),
        };
        foreach (var c in candidates)
        {
            var resolved = Path.GetFullPath(c);
            if (File.Exists(resolved)) return resolved;
        }
        return null;
    }
}

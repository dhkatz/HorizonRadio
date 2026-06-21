using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Tools;
using HorizonRadio.Tools.Librespot;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Spotify as a first-class, mixable content source. Replaces the legacy
/// self-driven "cast to Horizon Radio" receiver: instead of the user's phone
/// driving librespot, <em>our</em> engine drives it track-by-track via the Web API
/// (see <see cref="SpotifyPlaybackService"/>), so Spotify tracks/playlists/albums
/// work in the global queue, in Quick Play, and interleaved inside Mixes — with
/// player-bar transport and seek — exactly like YouTube and local files.
///
/// The librespot/account engine is an app singleton (built once from this schema
/// and exposed via <see cref="SpotifyRuntime"/>); the per-play locator is a Spotify
/// link. Bring-your-own Client ID only — there is no bundled default (Spotify's
/// 2026 Development-Mode rules make a shared app unviable).
/// </summary>
public sealed class SpotifyContentSourceFactory : IContentSourceFactory, IAuthenticatingSource, ISearchSource
{
    /// <summary>Catalog id for the driven, mixable Spotify source — distinct from the
    /// zero-config <see cref="SpotifySourceFactory"/> receiver (id "spotify"), which
    /// stays available so casting needs no developer app.</summary>
    public const string SourceId = "spotify-driven";

    public const string KeyUri = "uri";
    public const string KeyClientId = "clientId";
    public const string KeyExecutable = "executable";
    public const string KeyDeviceName = "deviceName";
    public const string KeyCacheDir = "cacheDir";
    public const string KeyBitrate = "bitrate";
    public const string KeyNormalise = "normalise";

    private static readonly string[] BitrateOptions = ["auto", "96", "160", "320"];

    public string Id => SourceId;
    public string DisplayName => "Spotify";
    public string? Description =>
        "Play Spotify tracks, playlists, and albums — in the queue, in Quick Play, or mixed " +
        "with other sources. Requires Premium and your own Spotify app's Client ID (connect below). " +
        "For zero-setup casting from your phone instead, use \"Spotify Connect\".";

    public string ContentKey => KeyUri;
    public string LocatorHint => "spotify:track:… / playlist / album, or an open.spotify.com link";

    public IReadOnlyList<ConfigField> Schema { get; }

    public SpotifyContentSourceFactory()
    {
        var defaultExe = Librespot.DiscoverExe() ?? "";
        var defaultCache = Librespot.DefaultCacheDir;

        Schema =
        [
            new TextField(
                Key:         KeyUri,
                Label:       "Spotify link",
                Placeholder: LocatorHint,
                Description: "A track, playlist, or album. Used for Quick Play and mix entries."),

            new TextField(
                Key:         KeyClientId,
                Label:       "Spotify Client ID",
                Placeholder: "from developer.spotify.com → your app",
                Description: "Your own Spotify app's Client ID. Create an app, add the redirect " +
                             "URI shown when you connect, then paste the Client ID here.")
                { IsEnvironment = true },

            new ToolField(
                Key:         KeyExecutable,
                Label:       "librespot.exe path",
                ToolKind:    Tools.ToolKind.Librespot,
                Default:     defaultExe,
                Description: "Install via the Tools tab, or point at an existing librespot.exe."),

            new TextField(
                Key:         KeyDeviceName,
                Label:       "Device name",
                Default:     Librespot.DefaultDeviceName,
                Placeholder: Librespot.DefaultDeviceName,
                Description: "The Connect device librespot registers. The first time, cast to it " +
                             "once from your Spotify app to log it in."),

            new DirectoryField(
                Key:         KeyCacheDir,
                Label:       "Cache directory",
                Default:     defaultCache,
                Description: "librespot's login + audio cache, so it stays logged in across restarts.")
                { IsEnvironment = true },

            new EnumField(
                Key:         KeyBitrate,
                Label:       "Bitrate",
                Options:     BitrateOptions,
                Default:     "auto",
                Description: "Auto lets librespot pick the highest the account is licensed for."),

            new BoolField(
                Key:         KeyNormalise,
                Label:       "Volume normalisation",
                Default:     true,
                Description: "Spotify's per-track ReplayGain. Keeps loud and quiet tracks consistent."),
        ];
    }

    public IContentPlayer CreatePlayer(ConfigValues values)
    {
        var playback = SpotifyRuntime.Playback;
        var connection = SpotifyRuntime.Connection;
        if (playback is null || connection is null)
            throw new InvalidOperationException(
                "Spotify isn't initialised. Set your Client ID and connect your account in the Sources tab.");

        return new SpotifyContentPlayer(connection, playback);
    }

    public IAudioSource Create(ConfigValues values)
        => CreatePlayer(values).Open(new ContentRef(Id, values.GetString(ContentKey) ?? ""));

    // -- IAuthenticatingSource (bring-your-own Client ID, PKCE browser login) --

    public bool IsConnected => SpotifyRuntime.Connection?.IsConnected ?? false;

    public string StatusText
    {
        get
        {
            var conn = SpotifyRuntime.Connection;
            if (conn is null) return "Spotify isn't initialised.";
            return IsConnected
                ? "Connected to Spotify."
                : $"Not connected. Add this redirect URI to your Spotify app, then Connect: {conn.RedirectUri}";
        }
    }

    public async Task ConnectAsync(ConfigValues values, CancellationToken ct = default)
    {
        var conn = SpotifyRuntime.Connection
                   ?? throw new InvalidOperationException("Spotify isn't initialised.");

        var clientId = values.GetString(KeyClientId);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Enter your Spotify Client ID first.");

        // Point the connection at the (possibly just-typed) Client ID before the
        // browser handshake, so a user can switch apps without restarting.
        conn.SetClientId(clientId!);
        await conn.LoginAsync(ct).ConfigureAwait(false);
    }

    public void Disconnect() => SpotifyRuntime.Connection?.Logout();

    // -- ISearchSource (free-text Web API search → spotify:track:… locators) --

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        var conn = SpotifyRuntime.Connection;
        // Not initialised / not connected / empty query → no results (never throw, so a
        // disconnected Spotify can't break a search that spans other sources later).
        if (conn is null || !conn.IsConnected || string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        return SpotifySearch.SearchTracksAsync(conn, query.Trim(), limit, ct);
    }
}

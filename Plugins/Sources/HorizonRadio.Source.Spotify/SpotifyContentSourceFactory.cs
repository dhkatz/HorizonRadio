using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Tools.Librespot;

namespace HorizonRadio.Source.Spotify;

/// <summary>
/// Spotify as a first-class, mixable content source. Replaces the legacy
/// self-driven "cast to Horizon Radio" receiver: instead of the user's phone
/// driving librespot, <em>our</em> engine drives it track-by-track via the Web API
/// (see <see cref="SpotifyPlaybackService"/>), so Spotify tracks/playlists/albums
/// work in the global queue, in Quick Play, and interleaved inside Mixes — with
/// player-bar transport and seek — exactly like YouTube and local files.
///
/// The librespot/account engine is a singleton the plugin registers (built from
/// <see cref="SpotifySettings"/>); the per-play locator is a Spotify link. Bring-your-own Client ID only — there is no bundled default (Spotify's
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
    public const string KeySoloistKey = "soloistKey";
    public const string KeySoloistExecutable = "soloistExecutable";

    internal static readonly string[] BitrateOptions = ["auto", "96", "160", "320"];

    public string Id => SourceId;
    public int SortOrder => 21;
    public string DisplayName => "Spotify";
    public string? Description =>
        "Play Spotify tracks, playlists, and albums — in the queue, in Quick Play, or mixed " +
        "with other sources. Requires Premium and your own Spotify app's Client ID (connect below). " +
        "For zero-setup casting from your phone instead, use \"Spotify Connect\".";

    public string ContentKey => KeyUri;
    internal const string LocatorHintText = "spotify:track:… / playlist / album, or an open.spotify.com link";

    public string LocatorHint => LocatorHintText;

    public bool Recognizes(string locator) => SpotifyLinks.TryParse(locator, out _);

    public IReadOnlyList<ConfigField> Schema => _settings.Schema;

    private readonly SpotifySettings _settings;
    private readonly SpotifyConnection _connection;
    private readonly SpotifyPlaybackService _playback;
    private readonly SoloistPlaybackService _soloist;
    private readonly SpotifyPlayerChoice _choice;
    private readonly IPluginLog _log;

    public SpotifyContentSourceFactory(SpotifySettings settings, SpotifyConnection connection,
        SpotifyPlaybackService playback, SoloistPlaybackService soloist, SpotifyPlayerChoice choice, IPluginLog log)
    {
        _settings = settings;
        _connection = connection;
        _playback = playback;
        _soloist = soloist;
        _choice = choice;
        _log = log;
    }

    public IContentPlayer CreatePlayer(ConfigValues values) =>
        new SpotifyContentPlayer(_connection, _playback, _soloist, _choice, _log);

    // -- IAuthenticatingSource (bring-your-own Client ID, PKCE browser login) --

    public bool IsConnected => _connection.IsConnected;

    public string StatusText
    {
        get
        {
            return IsConnected
                ? "Connected to Spotify."
                : $"Not connected. Add this redirect URI to your Spotify app, then Connect: {_connection.RedirectUri}";
        }
    }

    public async Task ConnectAsync(ConfigValues values, CancellationToken ct = default)
    {
        var clientId = values.GetString(KeyClientId);
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Enter your Spotify Client ID first.");

        // Point the connection at the (possibly just-typed) Client ID before the
        // browser handshake, so a user can switch apps without restarting.
        _connection.SetClientId(clientId!);
        await _connection.LoginAsync(ct).ConfigureAwait(false);
    }

    public void Disconnect() => _connection.Logout();

    // -- ISearchSource (free-text Web API search → spotify:track:… locators) --

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        // Not connected / empty query → no results (never throw, so a disconnected Spotify
        // can't break a search that spans other sources).
        if (!_connection.IsConnected || string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        return SpotifySearch.SearchTracksAsync(_connection, _log, query.Trim(), limit, ct);
    }
}

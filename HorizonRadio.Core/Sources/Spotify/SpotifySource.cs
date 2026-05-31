using System.Collections.Generic;
using System.Diagnostics;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Spotify Connect source backed by a librespot subprocess. librespot
/// announces a Connect target on the local network; the user picks it
/// from their Spotify app, and audio streams out via librespot's
/// <c>--backend pipe</c> stdout. We forward the PCM to the bridge.
///
/// Track titles are timed to actual playback rather than scraped naively:
/// librespot logs <c>"Loading &lt;X&gt; with Spotify URI"</c> both when a
/// track starts AND when it PRELOADS the next one for gapless playback —
/// the preload fires up to ~30 s early, which made the in-game title flip
/// long before the current song ended. Instead we cache id→title from the
/// load line but only announce when librespot reports the track actually
/// starting, via its <c>--onevent</c> hook (see <see cref="BuildArgs"/>).
///
/// The hook is a one-liner that echoes the player event to stderr; since
/// librespot spawns it with inherited stdio, the line lands in the same
/// stderr stream we already drain — no extra pipe or helper process, and
/// it surfaces in the Console tab for free. If the hook never reports
/// (older librespot, blocked cmd, …) we fall back to announcing on the
/// load line so the HUD is never stuck.
///
/// Transport control is intentionally not exposed: librespot 0.x
/// doesn't accept commands from outside the Spotify Connect protocol,
/// and we don't ship a Spotify Web API client. Users pause/skip from
/// their Spotify app.
/// </summary>
public sealed class SpotifySource(SpotifyOptions options) : IAudioSource
{
    public string Id => "spotify";
    public string DisplayName => "Spotify Connect";

    public event Action<Track>? TrackChanged;

    private SubprocessPcmSource? _subprocess;

    // id (bare base62) -> (title, full spotify URI), cached from librespot's
    // "Loading <title> with Spotify URI <uri>" lines. Accessed only from the
    // single stderr-drain thread, so no locking is needed.
    private readonly Dictionary<string, (string Title, string Uri)> _trackById = new();

    // The most recently loaded track. librespot's TRACK_ID env var doesn't
    // always match the id encoding in the load line's URI, so the id lookup
    // can miss; for normal gapless playback the newest-loaded track IS the
    // one a play event is starting, so we use it as the resolution fallback.
    private (string Title, string Uri)? _newestLoaded;

    // Dedupe key of what's currently on the HUD; stops repeat play/
    // track_changed events (resume-from-pause re-fires "playing") from
    // re-announcing the same track.
    private string? _announcedKey;

    // Set once we've seen a single --onevent breadcrumb. Until then we
    // announce on the load line so an environment where the hook doesn't
    // fire still updates the HUD (degrades to the old, slightly-early
    // behavior instead of showing nothing).
    private bool _eventsWorking;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-spotify] {msg}");

    public async Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_subprocess != null) return;

        Directory.CreateDirectory(options.CacheDirectory);

        var args = BuildArgs(options);
        _subprocess = new SubprocessPcmSource(new SubprocessPcmSource.Config
        {
            ExecutablePath = options.ExecutablePath,
            Args = args,
            ToolName = "librespot",
            OnStderrLine = OnStderr,
        });
        await _subprocess.StartAsync(sink, ct).ConfigureAwait(false);

        // Publish a placeholder so the HUD shows "Spotify Connect" right
        // away. The real title comes when librespot logs a Loading line
        // — that's the only metadata channel we have without forking it.
        TrackChanged?.Invoke(new Track(
            Title: "Waiting for Spotify Connect…",
            Artist: "Cast from your Spotify app to “" + options.DeviceName + "”",
            Album: null,
            AlbumArt: null,
            SourceId: Id,
            SourceDisplay: DisplayName,
            ExternalId: null));
    }

    public async Task StopAsync()
    {
        if (_subprocess == null) return;
        await _subprocess.DisposeAsync().ConfigureAwait(false);
        _subprocess = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnStderr(string line)
    {
        // Our --onevent breadcrumb, echoed into librespot's stderr:
        // "HZNEV <player_event> <track_id>".
        if (line.StartsWith("HZNEV ", StringComparison.Ordinal))
        {
            HandleEvent(line);
            return;
        }

        // librespot's per-track load line. NOTE: this fires for gapless
        // PRELOAD of the next track too (up to ~30 s early), so we only
        // cache it here — the actual HUD flip happens on the play event.
        if (TryParseLoading(line, out var id, out var title, out var uri))
        {
            _trackById[id] = (title, uri);
            _newestLoaded = (title, uri);
            // Fallback only: announce immediately if the event hook isn't
            // reporting. The first track's load line is a real start (no
            // preload yet), so this also covers session startup before the
            // first event arrives.
            if (!_eventsWorking) Announce(id, title, uri);
        }
    }

    private void HandleEvent(string line)
    {
        // "HZNEV <event> <track_id?>"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;

        _eventsWorking = true;

        // Only a track actually entering playback should move the HUD.
        // preloading / loading / preload_next / end_of_track / seeked /
        // paused / stopped are deliberately ignored here.
        var ev = parts[1];
        if (ev != "track_changed" && ev != "playing") return;

        var rawId = parts.Length >= 3 ? parts[2] : "";
        var id = NormalizeId(rawId);
        // An unexpanded "%TRACK_ID%" or empty id means we can't match by id;
        // fall through to the newest-loaded track below.
        bool validId = id.Length > 0 && id[0] != '%';

        // Resolve the title: prefer an exact id match (when librespot's
        // TRACK_ID lines up with the load line's URI), else the most
        // recently loaded track — which, for normal gapless playback, is
        // the one this event is starting. This keeps the HUD correct even
        // when the two id encodings differ.
        var track = validId && _trackById.TryGetValue(id, out var hit)
            ? hit
            : _newestLoaded;
        if (track is null) return;

        // Dedupe on the event id when we have one (stable per track, so a
        // re-fired "playing" on resume doesn't re-announce); else on the
        // resolved URI.
        var key = validId ? id : track.Value.Uri;
        Announce(key, track.Value.Title, track.Value.Uri);
    }

    private void Announce(string key, string title, string uri)
    {
        if (key == _announcedKey) return;
        _announcedKey = key;

        TrackChanged?.Invoke(new Track(
            Title: string.IsNullOrEmpty(title) ? "Spotify" : title,
            Artist: "",   // not in the load line; enrichment fills it later
            Album: null,
            AlbumArt: null,
            SourceId: Id,
            SourceDisplay: DisplayName,
            ExternalId: string.IsNullOrEmpty(uri) ? null : uri));
    }

    private static bool TryParseLoading(string line, out string id, out string title, out string uri)
    {
        id = title = uri = "";

        const string kLoading = "Loading <";
        const string kEndTag = "> with Spotify URI";

        var loadIdx = line.IndexOf(kLoading, StringComparison.Ordinal);
        if (loadIdx < 0) return false;

        int titleStart = loadIdx + kLoading.Length;
        int titleEnd = line.IndexOf(kEndTag, titleStart, StringComparison.Ordinal);
        if (titleEnd <= titleStart) return false;

        title = line.Substring(titleStart, titleEnd - titleStart);

        int uriStart = titleEnd + kEndTag.Length;
        var uriSlice = line.AsSpan(uriStart).TrimStart(' ').TrimStart('<');
        int gt = uriSlice.IndexOf('>');
        if (gt <= 0) return false;

        uri = uriSlice[..gt].ToString();   // e.g. spotify:track:<id>
        id = NormalizeId(uri);
        return id.Length > 0;
    }

    // librespot's TRACK_ID env var is sometimes a full "spotify:track:<id>"
    // URI and sometimes the bare base62 id, depending on version; the load
    // line always carries the URI. Reduce both to the bare id so they match.
    private static string NormalizeId(string s)
    {
        int colon = s.LastIndexOf(':');
        return colon >= 0 ? s[(colon + 1)..] : s;
    }

    private static string[] BuildArgs(SpotifyOptions o)
    {
        // Pinned defaults mirror the C++ build:
        //   --backend pipe         write s16 PCM to stdout
        //   --format S16           explicit format (defensive)
        //   --volume-ctrl fixed    we control volume via FMOD/game,
        //                          not via Spotify Connect's slider
        //   --enable-volume-norm.  even per-track loudness so the AGC
        //                          doesn't pump between hot/quiet songs
        //
        // Bitrate is set only if the user picked a non-Auto value;
        // omitting it lets librespot pick the highest the account is
        // licensed for (96/160/320) without surprising free-tier users
        // by forcing 320 and triggering skip-on-play.
        var list = new List<string>
        {
            "--name",                          o.DeviceName,
            "--backend",                       "pipe",
            "--format",                        "S16",
            "--cache",                         o.CacheDirectory,
            "--volume-ctrl",                   "fixed",

            // Player-event hook used to time the in-game title to actual
            // playback (see class docs). librespot splits this string on
            // whitespace and spawns it per event with PLAYER_EVENT/TRACK_ID
            // (etc.) in the environment, inheriting its own stdio — so the
            // echo lands in librespot's stderr, which we already drain. The
            // "1>&2" keeps it off stdout (that's the PCM pipe). We only key
            // off PLAYER_EVENT + TRACK_ID, both cmd-safe, so there's no
            // quoting hazard from track titles.
            "--onevent",                       "cmd /c echo HZNEV %PLAYER_EVENT% %TRACK_ID% 1>&2",
        };
        if (o.EnableVolumeNormalisation) list.Add("--enable-volume-normalisation");
        if (!string.IsNullOrEmpty(o.Bitrate) && o.Bitrate != "auto")
        {
            list.Add("--bitrate");
            list.Add(o.Bitrate);
        }
        return list.ToArray();
    }
}

public sealed class SpotifyOptions
{
    public required string ExecutablePath { get; init; }
    public required string DeviceName { get; init; }
    public required string CacheDirectory { get; init; }
    public string Bitrate { get; init; } = "auto"; // 96|160|320|auto
    public bool EnableVolumeNormalisation { get; init; } = true;
}

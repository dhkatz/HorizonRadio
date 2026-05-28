using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Spotify Connect source backed by a librespot subprocess. librespot
/// announces a Connect target on the local network; the user picks it
/// from their Spotify app, and audio streams out via librespot's
/// <c>--backend pipe</c> stdout. We forward the PCM to the bridge and
/// scrape track titles from librespot's stderr <c>"Loading &lt;X&gt; with
/// Spotify URI"</c> log lines (librespot doesn't emit structured
/// metadata events on stderr).
///
/// Transport control is intentionally not exposed: librespot 0.x
/// doesn't accept commands from outside the Spotify Connect protocol,
/// and we don't ship a Spotify Web API client. Users pause/skip from
/// their Spotify app.
/// </summary>
public sealed class SpotifyLibrespotSource : IAudioSource
{
    public string Id          => "spotify";
    public string DisplayName => "Spotify Connect";

    public event Action<Track>? TrackChanged;

    private readonly SpotifyLibrespotOptions _options;
    private SubprocessPcmSource?             _subprocess;

    public SpotifyLibrespotSource(SpotifyLibrespotOptions options) { _options = options; }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-spotify] {msg}");

    public async Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_subprocess != null) return;

        Directory.CreateDirectory(_options.CacheDirectory);

        var args = BuildArgs(_options);
        _subprocess = new SubprocessPcmSource(new SubprocessPcmSource.Config
        {
            ExecutablePath = _options.ExecutablePath,
            Args           = args,
            OnStderrLine   = OnStderr,
        });
        await _subprocess.StartAsync(sink, ct).ConfigureAwait(false);

        // Publish a placeholder so the HUD shows "Spotify Connect" right
        // away. The real title comes when librespot logs a Loading line
        // — that's the only metadata channel we have without forking it.
        TrackChanged?.Invoke(new Track(
            Title:         "Waiting for Spotify Connect…",
            Artist:        "Cast from your Spotify app to “" + _options.DeviceName + "”",
            Album:         null,
            AlbumArt:      null,
            SourceId:      Id,
            SourceDisplay: DisplayName,
            ExternalId:    null));
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
        // librespot's per-track INFO log: "... Loading <TITLE> with
        // Spotify URI <spotify:track:...>". Only path that surfaces
        // track changes; there's no separate event channel.
        const string kLoading  = "Loading <";
        const string kEndTag   = "> with Spotify URI";

        var loadIdx = line.IndexOf(kLoading, StringComparison.Ordinal);
        if (loadIdx < 0) return;

        int titleStart = loadIdx + kLoading.Length;
        int titleEnd   = line.IndexOf(kEndTag, titleStart, StringComparison.Ordinal);
        if (titleEnd <= titleStart) return;

        var title = line.Substring(titleStart, titleEnd - titleStart);

        // Extract spotify:track:<id> URI tail as the ExternalId so
        // future enrichment (MusicBrainz / album art) has something
        // canonical to key off of.
        string? externalId = null;
        int uriStart = titleEnd + kEndTag.Length;
        var uriSlice = line.AsSpan(uriStart).TrimStart(' ').TrimStart('<');
        int gt = uriSlice.IndexOf('>');
        if (gt > 0) externalId = uriSlice[..gt].ToString();

        TrackChanged?.Invoke(new Track(
            Title:         title,
            Artist:        "",   // not in the INFO log; enrichment later
            Album:         null,
            AlbumArt:      null,
            SourceId:      Id,
            SourceDisplay: DisplayName,
            ExternalId:    externalId));
    }

    private static string[] BuildArgs(SpotifyLibrespotOptions o)
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
        var list = new System.Collections.Generic.List<string>
        {
            "--name",                          o.DeviceName,
            "--backend",                       "pipe",
            "--format",                        "S16",
            "--cache",                         o.CacheDirectory,
            "--volume-ctrl",                   "fixed",
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

public sealed class SpotifyLibrespotOptions
{
    public required string ExecutablePath           { get; init; }
    public required string DeviceName               { get; init; }
    public required string CacheDirectory           { get; init; }
    public          string Bitrate                  { get; init; } = "auto"; // 96|160|320|auto
    public          bool   EnableVolumeNormalisation{ get; init; } = true;
}

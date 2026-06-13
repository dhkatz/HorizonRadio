using System.Collections.Generic;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>librespot launch knobs, shared by both Spotify sources (the zero-config
/// cast receiver and the engine-driven content source).</summary>
public sealed record LibrespotOptions
{
    public required string ExecutablePath { get; init; }
    public required string DeviceName { get; init; }
    public required string CacheDirectory { get; init; }
    public string Bitrate { get; init; } = "auto"; // 96|160|320|auto
    public bool EnableVolumeNormalisation { get; init; } = true;
}

/// <summary>
/// One home for the librespot CLI contract and install discovery, so the cast
/// receiver (<see cref="SpotifySource"/>, <c>--autoplay on</c>) and the driven
/// service (<see cref="SpotifyPlaybackService"/>, <c>--autoplay off</c>) can't drift
/// — they share the exact same args (incl. the <c>HZNEV --onevent</c> breadcrumb both
/// stderr parsers depend on), exe-probe list, and default cache dir.
/// </summary>
public static class Librespot
{
    public const string DefaultDeviceName = "Horizon Radio";

    /// <summary>librespot's login + audio cache (so it stays logged in across restarts).</summary>
    public static string DefaultCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HorizonRadio", "librespot");

    /// <summary>Locate a librespot.exe next to the app, in the managed tools dir, or in
    /// a dev build output. Null if none found.</summary>
    public static string? DiscoverExe()
    {
        var here = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(here, "librespot.exe"),
            ToolsPaths.ExeFor(ToolKind.Librespot),
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

    /// <summary>
    /// Build the librespot command line. <paramref name="autoplay"/> is the one real
    /// difference between the two sources: the receiver keeps playing related tracks
    /// when the user's queue ends (on); the driven engine must STOP at end_of_track and
    /// hand control back to our queue (off).
    /// </summary>
    public static string[] BuildArgs(LibrespotOptions o, bool autoplay)
    {
        var list = new List<string>
        {
            "--name",          o.DeviceName,
            "--backend",       "pipe",     // write s16 PCM to stdout
            "--format",        "S16",
            "--cache",         o.CacheDirectory,
            "--volume-ctrl",   "fixed",    // volume is controlled by the game, not Connect
            "--autoplay",      autoplay ? "on" : "off",

            // Player-event hook echoed to stderr (which we already drain): we key off
            // PLAYER_EVENT + TRACK_ID (+ POSITION_MS/DURATION_MS), all cmd-safe, so a
            // track title can't introduce a quoting hazard. "1>&2" keeps it off stdout
            // (the PCM pipe).
            "--onevent",       "cmd /c echo HZNEV %PLAYER_EVENT% %TRACK_ID% %POSITION_MS% %DURATION_MS% 1>&2",
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

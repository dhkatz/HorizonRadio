using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Tools.Soloist;

/// <summary>How Horizon Radio starts Spotify Soloist.</summary>
public sealed record SoloistOptions
{
    public required string ExecutablePath { get; init; }

    /// <summary>The user's own Soloist key (developer.spotify.com/dashboard/soloist).</summary>
    public required string ApiKey { get; init; }

    public required string DeviceName { get; init; }
    public required string DataDirectory { get; init; }
}

/// <summary>A Soloist build, from <c>soloist.exe --version</c>.</summary>
public sealed record SoloistBuild(string Version, DateOnly Built)
{
    /// <summary>Soloist refuses to run from this day on (exit code <see cref="Soloist.ExpiredExitCode"/>).</summary>
    public DateOnly Expires => Built.AddDays(Soloist.LifetimeDays);
}

/// <summary>
/// Spotify Soloist's command line, install location and build facts, in one place
/// (https://developer.spotify.com/documentation/soloist).
/// </summary>
public static partial class Soloist
{
    public const string ExeName = "soloist.exe";

    /// <summary>The Connect device Soloist shows in the Spotify app (librespot is "Horizon Radio").</summary>
    public static string DefaultDeviceName(IAppFolders folders) => folders.Label("Horizon Radio (Soloist)");

    /// <summary>Spotify's own download link for the Windows build. Spotify asks that Soloist isn't
    /// redistributed, so the app fetches it from here on the user's request rather than shipping it.</summary>
    public const string DownloadUrl = "https://soloist-builds.spotifycdn.com/soloist_release_windows_x86_64.zip";

    public const string DownloadsPage = "https://developer.spotify.com/documentation/soloist/reference/downloads-and-updates";

    /// <summary>Where a user makes their key (needs Spotify Premium).</summary>
    public const string KeyPage = "https://developer.spotify.com/dashboard/soloist";

    /// <summary>Builds expire this many days after they're built.</summary>
    public const int LifetimeDays = 90;

    /// <summary>The exit code of an expired build.</summary>
    public const int ExpiredExitCode = 10;

    /// <summary>Soloist's pairing, settings and audio cache, kept with the app's data.</summary>
    public static string DefaultDataDir(IAppFolders folders) => folders.Data("soloist");

    /// <summary>The installed soloist.exe (managed tools dir, or next to the app), or null.</summary>
    public static string? DiscoverExe(IToolLocator tools) => tools.Find(ToolKind.Soloist);

    /// <summary>
    /// The command line: the user's key, our data directory (so pairing persists), the local
    /// WebSocket API on a port the OS picks (written to <c>&lt;data&gt;/ws.port</c>), and full volume:
    /// Soloist's own volume scales the audio the tap takes, and the game sets the level.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(SoloistOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            "--device-name", options.DeviceName,
            "--api-key", options.ApiKey,
            "--data-dir", options.DataDirectory,
            "--cache-dir", Path.Combine(options.DataDirectory, "cache"),
            "--ws", "127.0.0.1:0",
            "--initial-volume", "100",
        ];
    }

    // "soloist 1.3.10.52 build 1794182509 (20261109) (g19412df6ea) (windows/x86_64)"
    [GeneratedRegex(@"^soloist\s+(?<version>\d+(?:\.\d+)+)\b.*?\((?<date>\d{8})\)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();

    /// <summary>The build in <c>--version</c> output, or null if it doesn't look like Soloist's.</summary>
    public static SoloistBuild? ParseVersion(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var m = VersionLine().Match(output.Trim());
        if (!m.Success) return null;
        return DateOnly.TryParseExact(m.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var built)
            ? new SoloistBuild(m.Groups["version"].Value, built)
            : null;
    }

    /// <summary>Ask an installed soloist.exe which build it is. Null if it can't say.</summary>
    public static async Task<SoloistBuild?> GetBuildAsync(IProcesses processes, string exePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath, "--version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = processes.Start(psi);
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return ParseVersion(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// Decides a tool's <see cref="ToolFreshness"/> by comparing the hash
/// recorded in its installed <c>.sha256</c> sidecar against the hash the
/// installer expects (<see cref="IToolInstaller.GetExpectedHashAsync"/>).
///
/// Hash comparison — not version parsing — is what makes this robust and
/// uniform: the sidecar already holds the verified artifact hash, the
/// installer already knows how to fetch (or read) the expected one, and
/// neither path needs to spawn the tool or parse a version string. Any
/// ambiguity (offline, no sidecar, empty pin) resolves to
/// <see cref="ToolFreshness.Unknown"/> rather than a false "stale".
/// </summary>
public static class ToolFreshnessChecker
{
    public static async Task<ToolFreshness> CheckAsync(
        IToolInstaller installer, InstalledTool? installed, HttpClient http, CancellationToken ct)
    {
        if (installed is null)
            return ToolFreshness.Missing;

        string? installedHash;
        string? expected;
        try
        {
            // Fingerprint what's actually installed (live exe hash for
            // single-file tools, recorded archive hash for ffmpeg). Null
            // means we can't tell — e.g. ffmpeg with no sidecar.
            installedHash = await installer.GetInstalledHashAsync(installed, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(installedHash))
                return ToolFreshness.Unknown;

            expected = await installer.GetExpectedHashAsync(http, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Network/IO/parse failure — treat as Unknown, never nag.
            return ToolFreshness.Unknown;
        }

        if (string.IsNullOrWhiteSpace(expected))
            return ToolFreshness.Unknown;

        return string.Equals(expected, installedHash, StringComparison.OrdinalIgnoreCase)
            ? ToolFreshness.UpToDate
            : ToolFreshness.UpdateAvailable;
    }
}

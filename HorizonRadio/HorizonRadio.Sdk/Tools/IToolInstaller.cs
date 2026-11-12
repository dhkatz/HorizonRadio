using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Sdk.Tools;

/// <summary>
/// One installer per tool kind. Implementations download, extract (if
/// needed), and atomically place the binary where the host says
/// (<see cref="ToolInstallTarget"/>). They are stateless — the registry
/// owns "what's installed" and the host where tools go; installers only
/// know "how to install".
/// </summary>
public interface IToolInstaller
{
    /// <summary>Tool kind this installer handles (see <see cref="ToolKind"/>).</summary>
    string Kind { get; }

    /// <summary>Human-readable label for the Tools tab card.</summary>
    string DisplayName { get; }

    /// <summary>Short one-liner about what the tool does.</summary>
    string Description { get; }

    /// <summary>Download + install into <paramref name="target"/>. Reports progress through
    /// <paramref name="progress"/> (null is fine). Throws on failure
    /// rather than returning a status — the caller wraps in try/catch
    /// and surfaces the message; that way installers can let HttpClient
    /// and ZipArchive exceptions propagate without translation.</summary>
    Task InstallAsync(ToolInstallTarget target, IProgress<ToolInstallProgress>? progress, CancellationToken ct);

    /// <summary>
    /// The SHA-256 this installer expects the installed artifact's
    /// <c>.sha256</c> sidecar to match — the baseline for the
    /// provisioning-freshness check. Latest-policy tools fetch their
    /// upstream sums file (network); the pinned tool returns its
    /// embedded-manifest hash (offline). Returns null when it can't be
    /// determined — offline, a missing sums file, or an empty manifest
    /// pin — in which case freshness is reported as <c>Unknown</c>,
    /// never <c>UpdateAvailable</c>. The install path verifies the
    /// downloaded bytes against this same value, so sidecar and baseline
    /// are always the same kind of hash.
    /// </summary>
    Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct);

    /// <summary>
    /// The hash of the CURRENTLY-INSTALLED artifact, in the same space as
    /// <see cref="GetExpectedHashAsync"/> so the two compare directly. For
    /// single-file tools this is the live hash of the installed exe — so
    /// freshness is correct even when the <c>.sha256</c> sidecar is missing
    /// or stale (e.g. a hand-dropped binary). ffmpeg overrides it to return
    /// the recorded archive hash, since the extracted exe can't reproduce
    /// the upstream zip hash. Null when it can't be determined → Unknown.
    /// </summary>
    Task<string?> GetInstalledHashAsync(InstalledTool installed, CancellationToken ct);
}

/// <summary>
/// Progress snapshot emitted during an install. <see cref="Fraction"/>
/// is null when the work is indeterminate (e.g. "Extracting…"); the UI
/// shows a spinner instead of a progress bar in that case.
/// </summary>
public sealed record ToolInstallProgress(string Status, double? Fraction = null);

/// <summary>
/// Where an install goes, chosen by the host: the tool's own folder (created by the installer if
/// missing; an archive's other files go here too) and the file the tool is found by afterwards (the
/// exe, or a model's data file), which gets the <c>.sha256</c> sidecar.
/// </summary>
public sealed record ToolInstallTarget(string Directory, string Path);

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// Shared plumbing for the per-tool installers: the streaming download
/// loop, the atomic single-file install (download to <c>.new</c> →
/// verify → move into place → write sidecar), the HttpClient factory,
/// and the verify-or-throw decision. The leaf installers supply only
/// what differs — the URL, the progress label, and where the expected
/// hash comes from (<see cref="GetExpectedHashAsync"/>).
///
/// Temp-file lifetime is owned here: <see cref="DownloadVerifyInstallAsync"/>
/// wraps the whole sequence in try/finally so the <c>.new</c> file is
/// removed on ANY fault (HTTP, hash mismatch, IO) — not just on
/// cancellation, which is all the old per-installer loops cleaned up.
/// </summary>
public abstract class ToolInstallerBase : IToolInstaller
{
    public abstract string Kind { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }

    public abstract Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct);

    public abstract Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct);

    /// <summary>Where the installed file lands — a model's data file or an exe, per
    /// <see cref="ToolsPaths.PathFor"/>. Virtual for an installer with a truly bespoke layout.</summary>
    protected virtual string InstalledPath => ToolsPaths.PathFor(Kind);

    public virtual async Task<string?> GetInstalledHashAsync(InstalledTool installed, CancellationToken ct)
    {
        // Single-file tools (yt-dlp, librespot): hash the actual installed
        // exe so freshness is correct even with no/stale sidecar — a
        // hand-dropped old binary is still detected as out of date. ffmpeg
        // overrides this; its sidecar holds the archive hash instead.
        if (string.IsNullOrEmpty(installed.Path) || !File.Exists(installed.Path))
            return null;
        return await HashVerification.ComputeFileSha256Async(installed.Path, ct).ConfigureAwait(false);
    }

    /// <summary>One HttpClient configured the way every tool download
    /// wants it: a generous timeout and our User-Agent (GitHub's CDN and
    /// gyan.dev both serve anonymous, so no auth).</summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout) => new()
    {
        Timeout = timeout,
        DefaultRequestHeaders = { { "User-Agent", "HorizonRadio-Tools/1.0" } },
    };

    /// <summary>Resolve the pinned download coordinates for a manifest-pinned tool (librespot, the
    /// title model): the entry must exist, be <c>pinned</c> policy, have a build for the current
    /// RID, and a non-empty URL. <paramref name="label"/> names the tool in errors;
    /// <paramref name="emptyUrlMessage"/> overrides the "URL not filled in" message (e.g. to tell
    /// the user they can drop the file manually). Shared so every pinned installer validates the
    /// same way.</summary>
    protected static ToolPlatform ResolvePinnedPlatform(
        ToolManifest manifest, string kind, string label, string? emptyUrlMessage = null)
    {
        var entry = manifest.For(kind)
            ?? throw new InvalidOperationException($"tools.manifest.json has no '{kind}' entry.");
        if (!entry.IsPinned)
            throw new InvalidOperationException($"{label} manifest policy is '{entry.Policy}', expected 'pinned'.");

        var platform = entry.Platform(ToolManifest.CurrentRid)
            ?? throw new InvalidOperationException(
                $"tools.manifest.json has no {label} build for '{ToolManifest.CurrentRid}'.");
        if (string.IsNullOrWhiteSpace(platform.Url))
            throw new InvalidOperationException(emptyUrlMessage
                ?? $"tools.manifest.json {label} '{ToolManifest.CurrentRid}' has an empty URL.");

        return platform;
    }

    /// <summary>The pinned SHA-256 for the current RID from the manifest, read offline; null when
    /// absent (post-bump bootstrap window) → the installer downloads-and-trusts with a warning.</summary>
    protected static string? PinnedHash(ToolManifest manifest, string kind)
    {
        var sha = manifest.For(kind)?.Platform(ToolManifest.CurrentRid)?.Sha256;
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    /// <summary>
    /// Full install for a single-file tool (yt-dlp, librespot): download
    /// the URL to <c>{exe}.new</c>, verify against
    /// <see cref="GetExpectedHashAsync"/>, atomically move into place, and
    /// record the verified hash in the sidecar. ffmpeg overrides
    /// <see cref="InstallAsync"/> directly because it ships an archive.
    /// </summary>
    protected async Task DownloadVerifyInstallAsync(
        HttpClient http, string url, string label,
        IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        ToolsPaths.EnsureDir(Kind);
        var dest = InstalledPath;
        var tmp = dest + ".new";

        try
        {
            progress?.Report(new ToolInstallProgress("Connecting…"));
            await DownloadToFileAsync(http, url, tmp, label, progress, ct).ConfigureAwait(false);

            progress?.Report(new ToolInstallProgress("Verifying download…"));
            var expected = await GetExpectedHashAsync(http, ct).ConfigureAwait(false);
            var actual = await HashVerification.ComputeFileSha256Async(tmp, ct).ConfigureAwait(false);
            VerifyOrThrow(label, expected, actual, progress);

            progress?.Report(new ToolInstallProgress("Installing…"));
            // File.Move overwrite=true is atomic on NTFS for same-volume
            // moves, which is what we have (both paths under tools\).
            File.Move(tmp, dest, overwrite: true);

            // Record the hash we saw (expected when verified, else the
            // local hash) so the registry surfaces "verified" and the
            // freshness check has a baseline to compare against.
            HashVerification.WriteSidecar(dest, expected ?? actual);

            progress?.Report(new ToolInstallProgress("Done", Fraction: 1.0));
        }
        finally
        {
            // The leak fix: on success the move already consumed tmp, so
            // this is a no-op; on any fault (HTTP/hash/IO/cancel) it
            // removes the half-written .new file the old code leaked.
            TryDelete(tmp);
        }
    }

    /// <summary>
    /// Stream <paramref name="url"/> to <paramref name="destPath"/> with
    /// progress. Does NOT clean up on failure — the caller owns the temp
    /// file's lifetime via try/finally.
    /// </summary>
    protected static async Task DownloadToFileAsync(
        HttpClient http, string url, string destPath, string label,
        IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);

        var buf = new byte[81920];
        long got = 0;
        int n;
        while ((n = await src.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            got += n;
            progress?.Report(total is long t && t > 0
                ? new ToolInstallProgress(
                    $"Downloading {label} ({FormatSize(got)} / {FormatSize(t)})",
                    Fraction: (double)got / t)
                : new ToolInstallProgress($"Downloading {label} ({FormatSize(got)})"));
        }
    }

    /// <summary>
    /// Compare an expected hash to what we actually downloaded. A null
    /// expected hash is the lenient case (no published sums, or an empty
    /// manifest pin during the post-bump bootstrap window): we install
    /// unverified but say so, matching the behaviour every installer had
    /// before. A present-but-mismatched hash always throws.
    /// </summary>
    protected static void VerifyOrThrow(
        string label, string? expected, string actual,
        IProgress<ToolInstallProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            progress?.Report(new ToolInstallProgress(
                $"Warning: no published SHA-256 for {label}; installed without verification."));
            return;
        }

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{label} SHA-256 mismatch.\nExpected: {expected}\nGot:      {actual}");
    }

    /// <summary>Adaptive byte formatter — KB under a megabyte, MB above,
    /// so a 13 MB yt-dlp.exe and an 80 MB ffmpeg zip both read sensibly
    /// off one helper.</summary>
    protected static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024L
            ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
            : $"{bytes / 1024} KB";

    protected static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

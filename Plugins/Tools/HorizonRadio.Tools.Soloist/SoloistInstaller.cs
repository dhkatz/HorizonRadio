using System.IO.Compression;
using System.Security.Cryptography;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Tools.Soloist;

/// <summary>
/// Downloads Spotify's Windows build of Soloist from Spotify's CDN (<see cref="Soloist.DownloadUrl"/>)
/// and extracts <c>soloist.exe</c>, its changelog and its third-party licenses into the managed tools
/// dir. Fetched on the user's request, never shipped with the app (Spotify asks that Soloist isn't
/// redistributed).
///
/// Spotify publishes no checksum, but its CDN's ETag is the zip's MD5, so a download is checked
/// against the ETag the CDN served. That MD5 is kept beside the exe (<c>soloist.zip.md5</c>) and is
/// what freshness compares, so the Tools tab sees an update as soon as Spotify publishes a new build
/// (a HEAD request, no download); the usual <c>.sha256</c> sidecar records the zip's SHA-256. Updates
/// matter more here than for other tools: a build stops running 90 days after it was built.
/// </summary>
public sealed class SoloistInstaller : ToolInstallerBase
{
    public override string Kind => ToolKind.Soloist;
    public override string DisplayName => "Spotify Soloist";
    public override string Description =>
        "Spotify's own Connect client. Plays the Spotify tracks librespot is refused, with your Soloist key " +
        "(Spotify settings). Builds stop working 90 days after release, so keep it updated.";

    private const string Md5FileName = "soloist.zip.md5";

    // The files worth keeping from the archive; anything else in it is skipped.
    private static readonly string[] Keep = [Soloist.ExeName, "CHANGELOG.md", "THIRD_PARTY_LICENSES.txt"];

    public override async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        ToolsPaths.EnsureDir(Kind);
        var targetDir = ToolsPaths.DirectoryFor(Kind);
        var tmpZip = Path.Combine(targetDir, "soloist.zip.tmp");
        TryDelete(Path.Combine(targetDir, Soloist.ExeName + MovedAside)); // left by the last update

        using var http = CreateHttpClient(TimeSpan.FromMinutes(10));
        try
        {
            progress?.Report(new ToolInstallProgress("Connecting to Spotify…"));
            var expected = await GetExpectedHashAsync(http, ct).ConfigureAwait(false);
            await DownloadToFileAsync(http, Soloist.DownloadUrl, tmpZip, "Soloist", progress, ct).ConfigureAwait(false);

            progress?.Report(new ToolInstallProgress("Verifying download…"));
            var actual = await ComputeMd5Async(tmpZip, ct).ConfigureAwait(false);
            if (expected == null)
                progress?.Report(new ToolInstallProgress("Warning: Spotify's CDN gave no checksum; installed without verification."));
            else if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The Soloist download doesn't match what Spotify's CDN says it is.\nExpected MD5: {expected}\nGot:          {actual}");

            progress?.Report(new ToolInstallProgress("Extracting…"));
            Extract(tmpZip, targetDir, ct);
            var sha256 = await HashVerification.ComputeFileSha256Async(tmpZip, ct).ConfigureAwait(false);
            HashVerification.WriteSidecar(ToolsPaths.PathFor(Kind), sha256);
            await File.WriteAllTextAsync(Path.Combine(targetDir, Md5FileName), actual, ct).ConfigureAwait(false);
            progress?.Report(new ToolInstallProgress("Done", Fraction: 1.0));
        }
        finally
        {
            TryDelete(tmpZip);
        }
    }

    /// <summary>The MD5 of the build Spotify serves now, from its ETag (a HEAD request). Null when
    /// offline or when the ETag isn't an MD5.</summary>
    public override async Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Soloist.DownloadUrl);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return Md5FromEtag(response.Headers.ETag?.Tag);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // The MD5 of the zip this install came from: the same space as the ETag, so they compare.
    public override async Task<string?> GetInstalledHashAsync(InstalledTool installed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var path = Path.Combine(ToolsPaths.DirectoryFor(Kind), Md5FileName);
        if (!File.Exists(path)) return null;
        var text = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
        return Md5FromEtag(text);
    }

    /// <summary>An ETag that's an MD5 (<c>"&lt;32 hex&gt;"</c>), lowercased; else null (a multipart
    /// upload's ETag isn't one).</summary>
    internal static string? Md5FromEtag(string? etag)
    {
        var value = etag?.Trim().TrimStart('W', '/').Trim('"');
        return value is { Length: 32 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    private static async Task<string> ComputeMd5Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
#pragma warning disable CA5351 // Not security: the CDN's ETag is an MD5, and it's what we can check against.
        var hash = await MD5.HashDataAsync(stream, ct).ConfigureAwait(false);
#pragma warning restore CA5351
        return Convert.ToHexStringLower(hash);
    }

    private const string MovedAside = ".old";

    // A Soloist playing this session locks its exe against being overwritten, but not against
    // being renamed: move it aside, and the new build runs from Soloist's next start.
    private static void Replace(string tmp, string dst)
    {
        try
        {
            File.Move(tmp, dst, overwrite: true);
            return;
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(dst))
        {
        }
        var aside = dst + MovedAside;
        TryDelete(aside);
        File.Move(dst, aside);
        File.Move(tmp, dst);
    }

    private static void Extract(string zipPath, string targetDir, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries
            .Where(e => Keep.Contains(Path.GetFileName(e.FullName), StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (!entries.Any(e => string.Equals(Path.GetFileName(e.FullName), Soloist.ExeName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Soloist download has no soloist.exe; Spotify may have changed the package.");

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var dst = Path.Combine(targetDir, Path.GetFileName(entry.FullName));
            var tmp = dst + ".new";
            try
            {
                using (var src = entry.Open())
                using (var fs = File.Create(tmp))
                {
                    src.CopyTo(fs);
                }
                Replace(tmp, dst);
            }
            finally
            {
                TryDelete(tmp);
            }
        }
    }
}

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.Tools.FFmpeg;

/// <summary>
/// Downloads the gyan.dev "release-essentials" ffmpeg build and extracts
/// the contents of its <c>bin/</c> folder (ffmpeg.exe, ffprobe.exe,
/// ffplay.exe) into <see cref="ToolsPaths.DirectoryFor"/>.
///
/// gyan.dev ships static MSVC builds at a stable URL that always points
/// to the current release. The archive's top-level folder name embeds the
/// release date, so we walk entries rather than hard-code a path.
///
/// Roughly ~80 MB compressed, ~180 MB extracted; the bin folder we keep is
/// closer to 130 MB. The bulk is ffmpeg.exe itself (~70 MB) because gyan's
/// builds statically link everything.
///
/// Unlike the single-file tools this overrides <see cref="InstallAsync"/>
/// rather than using the base's single-file path — it still reuses the
/// shared download loop, HttpClient factory, and verify decision, and
/// hashes/compares the ARCHIVE (one check covers every extracted file).
/// </summary>
public sealed class FfmpegInstaller : ToolInstallerBase
{
    public override string Kind => ToolKind.Ffmpeg;
    public override string DisplayName => "ffmpeg";
    public override string Description => "Decodes the resolved audio stream to s16/44.1k stereo PCM for the in-game radio.";

    private const string LatestUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    // gyan.dev publishes a sidecar SHA-256 next to each release zip.
    // Format is bare "<hex>  <filename>" or just "<hex>"; we handle both.
    private const string SumsUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";

    public override async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        ToolsPaths.EnsureDir(Kind);
        var targetDir = ToolsPaths.DirectoryFor(Kind);
        var tmpZip = Path.Combine(targetDir, "ffmpeg.zip.tmp");

        using var http = CreateHttpClient(TimeSpan.FromMinutes(15));
        try
        {
            progress?.Report(new ToolInstallProgress("Connecting to gyan.dev…"));
            await DownloadToFileAsync(http, LatestUrl, tmpZip, "ffmpeg", progress, ct).ConfigureAwait(false);

            // Verify the zip before we extract. Hashing the archive (rather
            // than the extracted exe) is what the upstream publishes, and
            // one check covers every file we pull out of bin/.
            progress?.Report(new ToolInstallProgress("Verifying download…"));
            var expected = await GetExpectedHashAsync(http, ct).ConfigureAwait(false);
            var actual = await HashVerification.ComputeFileSha256Async(tmpZip, ct).ConfigureAwait(false);
            VerifyOrThrow("ffmpeg zip", expected, actual, progress);

            progress?.Report(new ToolInstallProgress("Extracting…"));
            ExtractBinFolder(tmpZip, targetDir, progress, ct);

            // Write the sidecar for ffmpeg.exe with the ZIP hash — that's
            // the file the registry surfaces, and it's the same hash chain
            // (zip integrity ⇒ contents integrity) the freshness check
            // re-compares against the current upstream zip hash.
            HashVerification.WriteSidecar(ToolsPaths.PathFor(Kind), expected ?? actual);

            progress?.Report(new ToolInstallProgress("Done", Fraction: 1.0));
        }
        finally
        {
            TryDelete(tmpZip);
        }
    }

    public override Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct) =>
        HashVerification.FetchExpectedSha256Async(
            http, SumsUrl, matchFilename: "ffmpeg-release-essentials.zip", ct);

    // The sidecar records the ARCHIVE hash (what gyan.dev publishes); the
    // extracted ffmpeg.exe can't reproduce it, so freshness compares the
    // recorded zip hash. No sidecar ⇒ null ⇒ Unknown (we'd have to
    // re-download the zip to know, which is the whole install).
    public override Task<string?> GetInstalledHashAsync(InstalledTool installed, CancellationToken ct)
        => Task.FromResult(string.IsNullOrWhiteSpace(installed.Sha256) ? null : installed.Sha256);

    /// <summary>
    /// Walk the archive and copy every <c>*/bin/*.exe|*.dll</c> entry into
    /// <paramref name="targetDir"/> flat (no subfolder). Skips the dated
    /// top-level wrapper folder and the doc / presets we don't need.
    /// Streams from the zip directly so we never materialise the extracted
    /// tree twice on disk.
    /// </summary>
    private static void ExtractBinFolder(
        string zipPath, string targetDir,
        IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        // Find the bin/ folder entries. Path inside the zip is something
        // like "ffmpeg-7.0.1-essentials_build/bin/ffmpeg.exe".
        var binEntries = archive.Entries
            .Where(e => e.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                     && !e.FullName.EndsWith('/'))
            .ToList();

        if (binEntries.Count == 0)
            throw new InvalidOperationException(
                "ffmpeg archive layout changed — no bin/ entries found.");

        int done = 0;
        foreach (var entry in binEntries)
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry.FullName);
            if (string.IsNullOrEmpty(name)) continue;

            var dst = Path.Combine(targetDir, name);
            // Stage to a temp file then move into place so an interrupted
            // extract can't leave a half-written exe sitting at the path
            // the registry would find via Exists(). try/finally so a mid-
            // extract fault (e.g. disk full) doesn't leave a stray .new.
            var tmp = dst + ".new";
            try
            {
                using (var src = entry.Open())
                using (var fs = File.Create(tmp))
                {
                    src.CopyTo(fs);
                }
                File.Move(tmp, dst, overwrite: true);
            }
            finally
            {
                TryDelete(tmp);
            }

            done++;
            progress?.Report(new ToolInstallProgress(
                $"Extracting ({done}/{binEntries.Count}): {name}",
                Fraction: 0.5 + 0.5 * done / binEntries.Count));
        }
    }
}

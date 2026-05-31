using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// Downloads the gyan.dev "release-essentials" ffmpeg build and
/// extracts the contents of its <c>bin/</c> folder (ffmpeg.exe,
/// ffprobe.exe, ffplay.exe) into <see cref="ToolsPaths.DirectoryFor"/>.
///
/// gyan.dev ships static MSVC builds at a stable URL that always
/// points to the current release. The archive's top-level folder
/// name embeds the release date, so we walk entries rather than
/// hard-code a path.
///
/// Roughly ~80 MB compressed, ~180 MB extracted; the bin folder we
/// actually keep is closer to 130 MB. The bulk is ffmpeg.exe itself
/// (~70 MB) because gyan's builds statically link everything.
/// </summary>
public sealed class FfmpegInstaller : IToolInstaller
{
    public string Kind => ToolKind.Ffmpeg;
    public string DisplayName => "ffmpeg";
    public string Description => "Decodes the resolved audio stream to s16/44.1k stereo PCM for the in-game radio.";

    private const string LatestUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    // gyan.dev publishes a sidecar SHA-256 next to each release zip.
    // Format is bare "<hex>  <filename>" or just "<hex>", we handle both.
    private const string SumsUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";

    public async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        ToolsPaths.EnsureDir(Kind);
        var targetDir = ToolsPaths.DirectoryFor(Kind);
        var tmpZip = Path.Combine(targetDir, "ffmpeg.zip.tmp");

        progress?.Report(new ToolInstallProgress("Connecting to gyan.dev…"));

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15),
            DefaultRequestHeaders = { { "User-Agent", "HorizonRadio-Tools/1.0" } },
        };

        try
        {
            using (var response = await http.GetAsync(
                LatestUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(tmpZip);

                var buf = new byte[81920];
                long got = 0;
                while (true)
                {
                    int n;
                    try { n = await src.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false); }
                    catch when (ct.IsCancellationRequested) { throw; }
                    if (n <= 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    got += n;
                    if (total is long t && t > 0)
                    {
                        progress?.Report(new ToolInstallProgress(
                            $"Downloading ffmpeg ({got / (1024 * 1024)} / {t / (1024 * 1024)} MB)",
                            Fraction: (double)got / t));
                    }
                    else
                    {
                        progress?.Report(new ToolInstallProgress(
                            $"Downloading ffmpeg ({got / (1024 * 1024)} MB)"));
                    }
                }
            }

            // Verify the zip before we extract. Hashing the archive
            // (rather than the extracted exe) is what the upstream
            // publishes, and one check covers every file we'll pull
            // out of bin/.
            progress?.Report(new ToolInstallProgress("Verifying download…"));
            var expected = await HashVerification
                .FetchExpectedSha256Async(http, SumsUrl, matchFilename: "ffmpeg-release-essentials.zip", ct)
                .ConfigureAwait(false);
            var actual = await HashVerification
                .ComputeFileSha256Async(tmpZip, ct).ConfigureAwait(false);

            if (expected != null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"ffmpeg zip SHA-256 mismatch.\nExpected: {expected}\nGot:      {actual}");
            }

            progress?.Report(new ToolInstallProgress("Extracting…"));
            ExtractBinFolder(tmpZip, targetDir, progress, ct);

            // Write sidecar for ffmpeg.exe specifically — that's the
            // file the registry surfaces, and it's the same hash chain
            // (zip integrity ⇒ contents integrity) as long as our
            // ExtractBinFolder doesn't transform bytes.
            HashVerification.WriteSidecar(
                ToolsPaths.ExeFor(Kind), expected ?? actual);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
        }

        progress?.Report(new ToolInstallProgress("Done", Fraction: 1.0));
    }

    /// <summary>
    /// Walk the archive and copy every <c>*/bin/*.exe|*.dll</c> entry
    /// into <paramref name="targetDir"/> flat (no subfolder). Skips the
    /// dated top-level wrapper folder and the doc / presets we don't
    /// need. Streams from the zip directly so we never materialise the
    /// extracted tree twice on disk.
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
            // the registry would find via Exists().
            var tmp = dst + ".new";
            using (var src = entry.Open())
            using (var fs = File.Create(tmp))
            {
                src.CopyTo(fs);
            }
            File.Move(tmp, dst, overwrite: true);

            done++;
            progress?.Report(new ToolInstallProgress(
                $"Extracting ({done}/{binEntries.Count}): {name}",
                Fraction: 0.5 + 0.5 * done / binEntries.Count));
        }
    }
}

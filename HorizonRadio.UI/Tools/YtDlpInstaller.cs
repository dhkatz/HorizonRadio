using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// Downloads the latest yt-dlp.exe from the official GitHub releases.
/// Single-file: no archive, no extraction. Atomic-replace via download
/// to <c>yt-dlp.exe.new</c> in the same directory followed by
/// <see cref="File.Move(string,string,bool)"/>.
/// </summary>
public sealed class YtDlpInstaller : IToolInstaller
{
    public string Kind => ToolKind.YtDlp;
    public string DisplayName => "yt-dlp";
    public string Description => "Resolves YouTube URLs (and many other sites) into direct audio streams.";

    private const string LatestUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    // SHA2-256SUMS is a multi-asset sums file: one line per release
    // artifact, format "<hash>  <filename>". We match the yt-dlp.exe row.
    private const string SumsUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    public async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        ToolsPaths.EnsureDir(Kind);
        var dest = ToolsPaths.ExeFor(Kind);
        var tmp = dest + ".new";

        progress?.Report(new ToolInstallProgress("Connecting to GitHub…"));

        // SocketsHttpHandler with PreAuthenticate=false (the default) is
        // fine; GitHub's CDN doesn't require auth. We don't bother with
        // resume / range support — yt-dlp.exe is ~13 MB.
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
            DefaultRequestHeaders = { { "User-Agent", "HorizonRadio-Tools/1.0" } },
        };

        using var response = await http.GetAsync(
            LatestUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long got = 0;
            while (true)
            {
                int n;
                try { n = await src.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false); }
                catch when (ct.IsCancellationRequested) { TryDelete(tmp); throw; }
                if (n <= 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                got += n;
                if (total is long t && t > 0)
                {
                    progress?.Report(new ToolInstallProgress(
                        $"Downloading yt-dlp.exe ({got / 1024} / {t / 1024} KB)",
                        Fraction: (double)got / t));
                }
                else
                {
                    progress?.Report(new ToolInstallProgress(
                        $"Downloading yt-dlp.exe ({got / 1024} KB)"));
                }
            }
        }

        // Fetch the upstream sums file. It can occasionally be missing
        // right after a release publish (race between the .exe and the
        // sums upload); we accept null and skip verification rather
        // than failing the install. The sidecar simply isn't written.
        progress?.Report(new ToolInstallProgress("Verifying download…"));
        var expected = await HashVerification
            .FetchExpectedSha256Async(http, SumsUrl, matchFilename: "yt-dlp.exe", ct)
            .ConfigureAwait(false);
        var actual = await HashVerification.ComputeFileSha256Async(tmp, ct)
            .ConfigureAwait(false);

        if (expected != null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(tmp);
            throw new InvalidOperationException(
                $"yt-dlp.exe SHA-256 mismatch.\nExpected: {expected}\nGot:      {actual}");
        }

        progress?.Report(new ToolInstallProgress("Installing…"));
        // File.Move overwrite=true is atomic on NTFS for same-volume
        // moves, which is what we have here (both paths under tools\).
        File.Move(tmp, dest, overwrite: true);

        // Write the sidecar with the hash we actually saw (expected if
        // we verified, otherwise the computed local hash). Registry
        // surfaces this on the card; mismatched-with-expected installs
        // never make it past the throw above.
        HashVerification.WriteSidecar(dest, expected ?? actual);

        progress?.Report(new ToolInstallProgress("Done", Fraction: 1.0));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

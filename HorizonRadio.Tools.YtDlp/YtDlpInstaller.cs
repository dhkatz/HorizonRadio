using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.Tools.YtDlp;

/// <summary>
/// Downloads the latest yt-dlp.exe from the official GitHub releases.
/// Single-file: no archive, no extraction — the shared
/// <see cref="ToolInstallerBase.DownloadVerifyInstallAsync"/> handles the
/// download → verify → atomic-move → sidecar sequence. Freshness baseline
/// is the upstream SHA2-256SUMS row for yt-dlp.exe (latest policy).
/// </summary>
public sealed class YtDlpInstaller : ToolInstallerBase
{
    public override string Kind => ToolKind.YtDlp;
    public override string DisplayName => "yt-dlp";
    public override string Description => "Resolves YouTube URLs (and many other sites) into direct audio streams.";

    private const string LatestUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    // SHA2-256SUMS is a multi-asset sums file: one line per release
    // artifact, format "<hash>  <filename>". We match the yt-dlp.exe row.
    private const string SumsUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    public override async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        // ~13 MB; no resume/range support needed.
        using var http = CreateHttpClient(TimeSpan.FromMinutes(5));
        await DownloadVerifyInstallAsync(http, LatestUrl, "yt-dlp.exe", progress, ct).ConfigureAwait(false);
    }

    public override Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct) =>
        HashVerification.FetchExpectedSha256Async(http, SumsUrl, matchFilename: "yt-dlp.exe", ct);
}

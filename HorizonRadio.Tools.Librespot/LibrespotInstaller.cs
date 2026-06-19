using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.Tools.Librespot;

/// <summary>
/// Installs librespot.exe. Unlike yt-dlp/ffmpeg (which track their upstream's "latest"), librespot
/// is one WE build, pinned to the rev this app was tested against — so we download the exact asset
/// and verify it against our own SHA-256, not a source-supplied sums file. The pinned URL + hash
/// live here (the tool owns its own provisioning) rather than in a shared manifest.
///
/// Single-file, like yt-dlp: the shared base handles download → verify → atomic move → sidecar.
/// </summary>
public sealed class LibrespotInstaller : ToolInstallerBase
{
    public override string Kind => ToolKind.Librespot;
    public override string DisplayName => "librespot";
    public override string Description => "Spotify Connect client. Cast from your Spotify app to play through the in-game radio.";

    // Pinned to the librespot build this app was tested against. Bump both together on update.
    private const string Url =
        "https://github.com/dhkatz/HorizonRadio/releases/download/tools/librespot-33bf3a77ed4b-x86_64-pc-windows-msvc.exe";
    private const string Sha256 =
        "a7175f4e2df83489c01da71122b4fb685d08ae0d0ee65d4b166722bda13ec1c3";

    public override async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        using var http = CreateHttpClient(TimeSpan.FromMinutes(5));
        await DownloadVerifyInstallAsync(http, Url, "librespot.exe", progress, ct).ConfigureAwait(false);
    }

    /// <summary>The freshness baseline is our pinned hash, read offline (never the network) —
    /// comparing to upstream librespot would be the maintainer's CI job, not the app's.</summary>
    public override Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct)
        => Task.FromResult<string?>(Sha256);
}

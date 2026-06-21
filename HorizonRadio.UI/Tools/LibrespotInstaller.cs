using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// Installs librespot.exe from the pinned blobstore asset named in the
/// embedded <see cref="ToolManifest"/>. Unlike yt-dlp/ffmpeg (which track
/// their upstream's "latest"), librespot is one WE build, pinned to the
/// rev this app was tested against — so we download the exact asset the
/// manifest points at and verify it against the manifest's own SHA-256
/// (our expectation), not a source-supplied sums file.
///
/// Single-file, like yt-dlp: the shared base handles download → verify →
/// atomic move → sidecar.
/// </summary>
public sealed class LibrespotInstaller : ToolInstallerBase
{
    public override string Kind => ToolKind.Librespot;
    public override string DisplayName => "librespot";
    public override string Description => "Spotify Connect client. Cast from your Spotify app to play through the in-game radio.";

    private readonly ToolManifest _manifest;

    public LibrespotInstaller() : this(ToolManifest.Current) { }

    // Injectable for tests / alternate manifests.
    public LibrespotInstaller(ToolManifest manifest) => _manifest = manifest;

    public override async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        var platform = ResolvePinnedPlatform(_manifest, Kind, "librespot");
        using var http = CreateHttpClient(TimeSpan.FromMinutes(5));
        await DownloadVerifyInstallAsync(http, platform.Url, "librespot.exe", progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The freshness baseline is the manifest's pinned hash — read offline
    /// from the embedded manifest, never the network. Comparing the
    /// installed librespot to upstream librespot would be upstream-drift
    /// detection, which is the maintainer's CI job, not the app's. An
    /// empty pin (post-bump bootstrap window) yields null → Unknown.
    /// </summary>
    public override Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct)
        => Task.FromResult(PinnedHash(_manifest, Kind));
}

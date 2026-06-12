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
        var platform = ResolvePlatform();
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
    {
        var sha = _manifest.For(Kind)?.Platform(ToolManifest.CurrentRid)?.Sha256;
        return Task.FromResult(string.IsNullOrWhiteSpace(sha) ? null : sha);
    }

    private ToolPlatform ResolvePlatform()
    {
        var entry = _manifest.For(Kind)
            ?? throw new InvalidOperationException(
                "tools.manifest.json has no 'librespot' entry.");
        if (!entry.IsPinned)
            throw new InvalidOperationException(
                $"librespot manifest policy is '{entry.Policy}', expected 'pinned'.");

        var platform = entry.Platform(ToolManifest.CurrentRid)
            ?? throw new InvalidOperationException(
                $"tools.manifest.json has no librespot build for '{ToolManifest.CurrentRid}'.");
        if (string.IsNullOrWhiteSpace(platform.Url))
            throw new InvalidOperationException(
                $"tools.manifest.json librespot '{ToolManifest.CurrentRid}' has an empty URL.");

        return platform;
    }
}

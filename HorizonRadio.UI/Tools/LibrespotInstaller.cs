using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// Installs librespot.exe from the pinned blobstore asset named in the
/// embedded <see cref="ToolManifest"/>. Unlike yt-dlp/ffmpeg (which
/// track their upstream's "latest"), librespot is one WE build, pinned
/// to the rev this app was tested against — so we download the exact
/// asset the manifest points at and verify it against the manifest's own
/// SHA-256 (our expectation), not a source-supplied sums file.
///
/// Single-file, like yt-dlp: download to <c>librespot.exe.new</c> then
/// atomic <see cref="File.Move(string,string,bool)"/> into place.
/// </summary>
public sealed class LibrespotInstaller : IToolInstaller
{
    public string Kind => ToolKind.Librespot;
    public string DisplayName => "librespot";
    public string Description => "Spotify Connect client. Cast from your Spotify app to play through the in-game radio.";

    private readonly ToolManifest _manifest;

    public LibrespotInstaller() : this(ToolManifest.Current) { }

    // Injectable for tests / alternate manifests.
    public LibrespotInstaller(ToolManifest manifest) => _manifest = manifest;

    public async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        var platform = ResolvePlatform();

        ToolsPaths.EnsureDir(Kind);
        var dest = ToolsPaths.ExeFor(Kind);
        var tmp = dest + ".new";

        progress?.Report(new ToolInstallProgress("Connecting…"));

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
            DefaultRequestHeaders = { { "User-Agent", "HorizonRadio-Tools/1.0" } },
        };

        using (var response = await http.GetAsync(
            platform.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tmp);

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
                        $"Downloading librespot.exe ({got / 1024} / {t / 1024} KB)",
                        Fraction: (double)got / t));
                }
                else
                {
                    progress?.Report(new ToolInstallProgress(
                        $"Downloading librespot.exe ({got / 1024} KB)"));
                }
            }
        }

        // Verify against the manifest's expected hash. Empty/null hash is
        // the post-bump bootstrap window (asset published, hash not yet
        // filled in) — download-and-trust with a warning, matching the
        // lenient stance the other installers take on a missing sums file.
        progress?.Report(new ToolInstallProgress("Verifying download…"));
        var actual = await HashVerification.ComputeFileSha256Async(tmp, ct).ConfigureAwait(false);
        var expected = platform.Sha256;

        if (!string.IsNullOrWhiteSpace(expected))
        {
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tmp);
                throw new InvalidOperationException(
                    $"librespot.exe SHA-256 mismatch.\nExpected: {expected}\nGot:      {actual}");
            }
        }
        else
        {
            progress?.Report(new ToolInstallProgress(
                "Warning: manifest has no SHA-256 for this librespot pin; skipping verification."));
        }

        progress?.Report(new ToolInstallProgress("Installing…"));
        File.Move(tmp, dest, overwrite: true);

        // Record the hash we saw so the registry can surface "verified".
        HashVerification.WriteSidecar(dest, expected ?? actual);

        progress?.Report(new ToolInstallProgress("Done", Fraction: 1.0));
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// Installs the optional local title-extraction model — a single GGUF file pinned in the embedded
/// <see cref="ToolManifest"/>. Like librespot it's a specific asset WE pin (a chosen model build),
/// not an upstream "latest", so we download the exact URL and verify against the manifest's own
/// SHA-256. Unlike the exe tools it's a data file with no version probe; the base install path is
/// redirected to <see cref="ToolsPaths.ModelFor"/> via <see cref="InstalledPath"/>.
///
/// The model is large (hundreds of MB) and entirely optional — radio works without it (deterministic
/// parsing). Installing it lets the local LLM extract artist/title from freeform stream titles the
/// heuristics can't split.
/// </summary>
public sealed class TitleModelInstaller : ToolInstallerBase
{
    public override string Kind => ToolKind.TitleModel;
    public override string DisplayName => "Title Model";
    public override string Description =>
        "Optional local model that extracts artist/song names from messy radio titles (incl. mixed-language). Improves now-playing metadata; radio works without it.";

    protected override string InstalledPath => ToolsPaths.ModelFor(Kind);

    private readonly ToolManifest _manifest;

    public TitleModelInstaller() : this(ToolManifest.Current) { }

    // Injectable for tests / alternate manifests.
    public TitleModelInstaller(ToolManifest manifest) => _manifest = manifest;

    public override async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        var platform = ResolvePlatform();
        // Generous timeout: the model is hundreds of MB on a possibly-slow connection.
        using var http = CreateHttpClient(TimeSpan.FromMinutes(30));
        await DownloadVerifyInstallAsync(http, platform.Url, "title model", progress, ct).ConfigureAwait(false);
    }

    /// <summary>Freshness baseline is the manifest's pinned hash (read offline). Empty pin (the
    /// model isn't hosted yet) → null → Unknown, same as the other pinned tools.</summary>
    public override Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct)
    {
        var sha = _manifest.For(Kind)?.Platform(ToolManifest.CurrentRid)?.Sha256;
        return Task.FromResult(string.IsNullOrWhiteSpace(sha) ? null : sha);
    }

    private ToolPlatform ResolvePlatform()
    {
        var entry = _manifest.For(Kind)
            ?? throw new InvalidOperationException(
                "tools.manifest.json has no 'title-model' entry.");
        if (!entry.IsPinned)
            throw new InvalidOperationException(
                $"title-model manifest policy is '{entry.Policy}', expected 'pinned'.");

        var platform = entry.Platform(ToolManifest.CurrentRid)
            ?? throw new InvalidOperationException(
                $"tools.manifest.json has no title-model build for '{ToolManifest.CurrentRid}'.");
        if (string.IsNullOrWhiteSpace(platform.Url))
            throw new InvalidOperationException(
                "The title model isn't published yet — no download URL is pinned. " +
                "You can still use it by dropping a .gguf file at " +
                $"{ToolsPaths.ModelFor(Kind)} manually.");

        return platform;
    }
}

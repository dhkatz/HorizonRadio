using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// Installs the optional local title-extraction model — a single GGUF file. Like librespot it's a
/// specific asset WE pin (a chosen model build), not an upstream "latest", so we download the exact
/// URL and verify against our own SHA-256 (both pinned here — the tool owns its provisioning).
/// Unlike the exe tools it's a data file with no version probe; the base resolves its path via
/// <see cref="ToolsPaths.PathFor"/> (a .gguf, per the tool descriptor).
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

    // Pinned model build (a chosen Qwen GGUF). Bump both together on update.
    private const string Url =
        "https://huggingface.co/bartowski/Qwen2.5-0.5B-Instruct-GGUF/resolve/41ba88dbac95fed2528c92514c131d73eb5a174b/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf";
    private const string Sha256 =
        "6eb923e7d26e9cea28811e1a8e852009b21242fb157b26149d3b188f3a8c8653";

    public override async Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct)
    {
        // Generous timeout: the model is hundreds of MB on a possibly-slow connection.
        using var http = CreateHttpClient(TimeSpan.FromMinutes(30));
        await DownloadVerifyInstallAsync(http, Url, "title model", progress, ct).ConfigureAwait(false);
    }

    /// <summary>Freshness baseline is our pinned hash, read offline.</summary>
    public override Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct)
        => Task.FromResult<string?>(Sha256);
}

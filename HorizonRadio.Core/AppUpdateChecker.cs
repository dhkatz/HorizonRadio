using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core;

public enum UpdateStatus
{
    /// <summary>Couldn't determine — offline, API error, or dev build.</summary>
    Unknown,

    /// <summary>Running the newest build for this channel.</summary>
    UpToDate,

    /// <summary>A newer build is available.</summary>
    UpdateAvailable,
}

/// <summary>
/// Result of an app-update check. <see cref="ZipAssetUrl"/> /
/// <see cref="Sha256AssetUrl"/> are the download targets for the in-place
/// updater; <see cref="ReleasePageUrl"/> is the human-facing fallback.
/// </summary>
public sealed record AppUpdateResult(
    UpdateStatus Status,
    string? LatestVersion = null,
    string? ReleasePageUrl = null,
    string? ZipAssetUrl = null,
    string? Sha256AssetUrl = null);

/// <summary>
/// Checks GitHub for a newer app build, per channel:
/// <list type="bullet">
/// <item><b>stable</b> → <c>releases/latest</c> (prereleases excluded by the
/// API), SemVer-compare the tag to the running version.</item>
/// <item><b>nightly</b> → the rolling <c>nightly</c> prerelease, compare its
/// recorded commit to the embedded one.</item>
/// <item><b>dev</b> → never checks (returns <see cref="UpdateStatus.UpToDate"/>).</item>
/// </list>
/// Any failure resolves to <see cref="UpdateStatus.Unknown"/> — the UI stays
/// silent rather than nagging, exactly like the tool-freshness check.
/// </summary>
public static class AppUpdateChecker
{
    private const string ApiBase = "https://api.github.com/repos/dhkatz/HorizonRadio";

    public static async Task<AppUpdateResult> CheckAsync(BuildInfo build, HttpClient http, CancellationToken ct)
    {
        try
        {
            return build.Channel switch
            {
                ReleaseChannel.Stable => await CheckStableAsync(build, http, ct).ConfigureAwait(false),
                ReleaseChannel.Nightly => await CheckNightlyAsync(build, http, ct).ConfigureAwait(false),
                _ => new AppUpdateResult(UpdateStatus.UpToDate), // dev: nothing to update to
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine external cancellation — propagate
        }
        catch
        {
            // Anything else — including an HttpClient *timeout*, which also
            // surfaces as OperationCanceledException but with ct unsignalled
            // — means "couldn't determine", not an error to bubble up.
            return new AppUpdateResult(UpdateStatus.Unknown);
        }
    }

    private static async Task<AppUpdateResult> CheckStableAsync(BuildInfo build, HttpClient http, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(http, $"{ApiBase}/releases/latest", ct).ConfigureAwait(false);
        if (doc is null)
            return new AppUpdateResult(UpdateStatus.Unknown);

        var root = doc.RootElement;
        var tag = GetString(root, "tag_name");
        if (!TryParseVersion(tag, out var latest) || !TryParseVersion(build.Version, out var current))
            return new AppUpdateResult(UpdateStatus.Unknown);

        if (latest <= current)
            return new AppUpdateResult(UpdateStatus.UpToDate);

        var (zip, sha) = FindAssets(root);
        return new AppUpdateResult(
            UpdateStatus.UpdateAvailable, TrimLeadingV(tag), GetString(root, "html_url"), zip, sha);
    }

    private static async Task<AppUpdateResult> CheckNightlyAsync(BuildInfo build, HttpClient http, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(http, $"{ApiBase}/releases/tags/nightly", ct).ConfigureAwait(false);
        if (doc is null)
            return new AppUpdateResult(UpdateStatus.Unknown);

        var root = doc.RootElement;
        // The nightly release body records the source commit it was built
        // from (see nightly.yml); compare against the embedded commit.
        var remoteCommit = ParseCommit(GetString(root, "body"));
        if (string.IsNullOrWhiteSpace(remoteCommit) || string.IsNullOrWhiteSpace(build.CommitSha))
            return new AppUpdateResult(UpdateStatus.Unknown);

        if (CommitsMatch(remoteCommit!, build.CommitSha!))
            return new AppUpdateResult(UpdateStatus.UpToDate);

        var (zip, sha) = FindAssets(root);
        return new AppUpdateResult(
            UpdateStatus.UpdateAvailable, GetString(root, "name"), GetString(root, "html_url"), zip, sha);
    }

    // -- helpers --

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // GitHub's API 403s without a User-Agent; set both headers per-request
        // so we don't depend on how the caller configured the HttpClient.
        req.Headers.UserAgent.ParseAdd("HorizonRadio/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Find the .zip and its .zip.sha256 asset download URLs.</summary>
    private static (string? Zip, string? Sha256) FindAssets(JsonElement release)
    {
        string? zip = null, sha = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var url = GetString(asset, "browser_download_url");
                if (name is null || url is null) continue;
                if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip = url;
            }
        }
        return (zip, sha);
    }

    private static string? ParseCommit(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        // Matches a "commit: <hex>" line in the rolling-nightly release body.
        var m = Regex.Match(body, @"commit:\s*([0-9a-fA-F]{7,40})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool CommitsMatch(string a, string b)
        => a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse the numeric core (X.Y.Z) of a tag/version, ignoring a
    /// leading 'v' and any -prerelease/+build suffix.</summary>
    private static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = TrimLeadingV(raw)!;
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out version!);
    }

    private static string? TrimLeadingV(string? s)
        => s is null ? null : (s.StartsWith('v') || s.StartsWith('V') ? s[1..] : s);

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

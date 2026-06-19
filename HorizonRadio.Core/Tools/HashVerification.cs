using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Tools;

/// <summary>
/// SHA-256 utilities for tool downloads. Two operations: compute the
/// hash of a local file, and fetch the expected hash from the
/// upstream's sums file. Sidecar convention: <c>{exe}.sha256</c> next
/// to the binary holds the verified hex hash so the registry can
/// surface it without rehashing on every startup.
/// </summary>
public static class HashVerification
{
    public static string SidecarPathFor(string exePath) => exePath + ".sha256";

    /// <summary>Compute the SHA-256 of <paramref name="path"/> as a
    /// lowercase hex string. Streams from disk; 4 KB buffer is plenty
    /// since SHA itself is the bottleneck.</summary>
    public static async Task<string> ComputeFileSha256Async(
        string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var hasher = SHA256.Create();
        var hash = await hasher.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Fetch a remote text resource and pull a 64-char hex hash out of
    /// it. Tolerates the common formats: bare hex, "hash  filename"
    /// (BSD/openssl), or a multi-line sums file in which case
    /// <paramref name="matchFilename"/> picks the right line.
    /// </summary>
    public static async Task<string?> FetchExpectedSha256Async(
        HttpClient http, string url, string? matchFilename, CancellationToken ct)
    {
        string body;
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch { return null; }

        return ParseSha256FromText(body, matchFilename);
    }

    /// <summary>
    /// Parse a SHA-256 hex digest out of a sums-file body. If
    /// <paramref name="matchFilename"/> is supplied, only lines that
    /// mention that filename are considered — that's how multi-asset
    /// sums files like yt-dlp's SHA2-256SUMS are disambiguated.
    /// </summary>
    public static string? ParseSha256FromText(string body, string? matchFilename)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        // SHA-256 hex digests are 64 hex characters. Some upstreams add
        // a "SHA256:" prefix or trailing whitespace; we just look for
        // any 64-hex run.
        var hexRx = new Regex("\\b([0-9a-fA-F]{64})\\b", RegexOptions.Compiled);

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            // When a filename filter is set, the line must reference it.
            if (matchFilename != null &&
                !trimmed.Contains(matchFilename, StringComparison.OrdinalIgnoreCase))
                continue;

            var m = hexRx.Match(trimmed);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        }

        // No filename match (or none requested) — fall back to the first
        // hex run in the whole document. Handles bare-hash files.
        var any = hexRx.Match(body);
        return any.Success ? any.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static void WriteSidecar(string exePath, string sha256)
        => File.WriteAllText(SidecarPathFor(exePath), sha256);

    /// <summary>Read the sidecar hash, returning null when missing or
    /// malformed. Used by the registry on scan so cards can show
    /// "verified" without redoing the hash.</summary>
    public static string? ReadSidecar(string exePath)
    {
        var p = SidecarPathFor(exePath);
        if (!File.Exists(p)) return null;
        try
        {
            var text = File.ReadAllText(p).Trim();
            return ParseSha256FromText(text, matchFilename: null);
        }
        catch { return null; }
    }
}

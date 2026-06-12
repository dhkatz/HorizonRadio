using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// Thin wrapper over yt-dlp.exe. We use it for two things:
///   1. Resolving a user-supplied URL into a flat list of video entries
///      (handles single videos AND playlists with one call).
///   2. Resolving each entry, just-in-time, into a direct HTTPS audio
///      stream URL plus metadata. Done per-track because YouTube's
///      googlevideo URLs carry a signed `expire` query param that's
///      usually ~6 hours from issue; resolving up-front for a long
///      playlist would let URLs go stale before we play them.
///
/// JSON-line protocol: yt-dlp -J emits a single playlist object;
/// -j --no-playlist emits a single video object on stdout. We don't
/// stream-parse; resolving one video is fast enough (~1-2s) to just
/// buffer and parse once.
/// </summary>
public static class YtDlpClient
{
    public sealed record Entry(
        string Id,
        string WebpageUrl,
        string Title,
        string Uploader);

    public sealed record Resolved(
        string StreamUrl,
        string Title,
        string Uploader,
        string? ThumbnailUrl,
        string? Album,
        TimeSpan? Duration,
        // Populated when YouTube recognizes the video as music ("Music in this
        // video" / YouTube Music): the canonical song title + artist, distinct
        // from the video Title and the channel Uploader.
        string? Track = null,
        string? Artist = null,
        int? ReleaseYear = null);

    /// <summary>
    /// Resolves <paramref name="url"/> into a flat list of entries.
    /// Single-video URL → one entry; playlist URL → one entry per item.
    /// Uses <c>--flat-playlist</c> so we don't trigger per-video format
    /// probing here — that happens lazily in <see cref="ResolveAsync"/>.
    /// </summary>
    public static async Task<IReadOnlyList<Entry>> EnumerateAsync(
        string ytDlpPath, string url, CancellationToken ct)
    {
        // --flat-playlist: don't resolve individual video formats yet,
        // just list ids/titles. Cuts a 100-item playlist enumerate from
        // ~minutes down to seconds.
        // --dump-single-json: one JSON blob on stdout regardless of
        // whether the URL is a single video or a playlist.
        var stdout = await RunCapture(ytDlpPath,
            new[] { "--flat-playlist", "--dump-single-json", "--no-warnings", url },
            ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        var list = new List<Entry>();

        // Two shapes: playlist (has "entries" array) or video (no entries,
        // top-level has id/title/webpage_url). yt-dlp also uses _type
        // = "playlist" / "video" but is occasionally absent on bare videos.
        if (root.TryGetProperty("entries", out var entries) &&
            entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray())
            {
                var entry = TryReadEntry(e);
                if (entry != null) list.Add(entry);
            }
        }
        else
        {
            var entry = TryReadEntry(root);
            if (entry != null) list.Add(entry);
        }

        return list;
    }

    /// <summary>Metadata-only fields for a video (no stream URL). Used to enrich
    /// upcoming queue rows ahead of play without warming a short-lived stream URL.</summary>
    public sealed record Meta(
        string Title,
        string Uploader,
        string? ThumbnailUrl,
        string? Album,
        string? Track,
        string? Artist,
        int? ReleaseYear);

    /// <summary>
    /// Fetch a video's metadata only — <c>--skip-download</c>, no format selection,
    /// no stream URL. Cheap-ish (one extractor call) and safe to run ahead of play
    /// for upcoming queue items, since nothing here expires.
    /// </summary>
    public static async Task<Meta?> ResolveMetadataAsync(
        string ytDlpPath, string videoUrl, CancellationToken ct)
    {
        var stdout = await RunCapture(ytDlpPath,
            new[] { "-j", "--no-playlist", "--skip-download", "--no-warnings", videoUrl },
            ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        return new Meta(
            Title: ReadString(root, "title") ?? "(unknown)",
            Uploader: ReadString(root, "uploader") ?? ReadString(root, "channel") ?? "",
            ThumbnailUrl: ReadString(root, "thumbnail"),
            Album: ReadString(root, "album"),
            Track: ReadString(root, "track"),
            Artist: FirstArtist(ReadString(root, "artist") ?? ReadString(root, "creator")),
            ReleaseYear: ReadInt(root, "release_year") ?? YearFromDate(ReadString(root, "release_date")));
    }

    /// <summary>
    /// Resolves one entry into a fresh direct-stream URL. Always called
    /// immediately before playback, never cached, because the returned
    /// URL is signed with a short-lived expiry.
    /// </summary>
    public static async Task<Resolved> ResolveAsync(
        string ytDlpPath, string videoUrl, CancellationToken ct)
    {
        // -f bestaudio: pick the highest-bitrate audio-only format
        //   (typically opus 160k from YouTube; falls back to 128k m4a
        //   when opus isn't offered). We avoid "best" because that
        //   often returns muxed video+audio, forcing ffmpeg to demux.
        // --no-playlist: defensive — even with a video URL, yt-dlp will
        //   expand to a playlist if a list= param is present.
        // -j (== --dump-json): metadata JSON for the chosen format on
        //   one stdout line, including the resolved direct URL.
        var stdout = await RunCapture(ytDlpPath,
            new[] { "-f", "bestaudio", "--no-playlist", "-j", "--no-warnings", videoUrl },
            ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        var streamUrl = root.GetProperty("url").GetString()
                        ?? throw new InvalidOperationException(
                            "yt-dlp returned no `url` field for " + videoUrl);

        string title = ReadString(root, "title") ?? "(unknown)";
        string uploader = ReadString(root, "uploader") ?? ReadString(root, "channel") ?? "";
        string? thumb = ReadString(root, "thumbnail");
        string? album = ReadString(root, "album"); // populated for Music-style entries
        // "duration" is the video length in (possibly fractional) seconds;
        // absent on some live/streamed entries → null (progress bar hides).
        TimeSpan? duration = ReadDouble(root, "duration") is { } secs && secs > 0
            ? TimeSpan.FromSeconds(secs)
            : null;

        // Canonical song metadata, present only when YouTube tagged the video as
        // music. "artist" can be a comma-joined list; take the first credited name.
        string? track = ReadString(root, "track");
        string? artist = FirstArtist(ReadString(root, "artist") ?? ReadString(root, "creator"));
        int? releaseYear = ReadInt(root, "release_year") ?? YearFromDate(ReadString(root, "release_date"));

        return new Resolved(streamUrl, title, uploader, thumb, album, duration, track, artist, releaseYear);
    }

    private static Entry? TryReadEntry(JsonElement e)
    {
        var id = ReadString(e, "id");
        if (string.IsNullOrEmpty(id)) return null;

        var url = ReadString(e, "webpage_url")
                  ?? ReadString(e, "url")
                  ?? $"https://www.youtube.com/watch?v={id}";

        var title = ReadString(e, "title") ?? "(unknown)";
        var uploader = ReadString(e, "uploader") ?? ReadString(e, "channel") ?? "";
        return new Entry(id, url, title, uploader);
    }

    private static string? ReadString(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? ReadDouble(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static int? ReadInt(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : null;

    // yt-dlp's "artist" is sometimes "A, B, C" — keep the first non-empty credit.
    private static string? FirstArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return null;
        foreach (var part in artist.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return null;
    }

    // "release_date" is YYYYMMDD; pull the year.
    private static int? YearFromDate(string? date)
        => date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;

    private static async Task<string> RunCapture(
        string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Diagnostics.ProcessConsole.Append("yt-dlp", "$ yt-dlp " + string.Join(' ', args));

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException($"failed to spawn {exe}");

        // Drain stderr in parallel so a verbose run can't deadlock on a
        // full stderr pipe while we're reading stdout.
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                return await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return "";
            }
        }, ct);

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        // yt-dlp writes progress/warnings/errors to stderr; surface them
        // in the Console tab whether or not the run succeeded.
        Diagnostics.ProcessConsole.AppendBlock("yt-dlp", stderr);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"yt-dlp exited {proc.ExitCode}: {Truncate(stderr, 400)}");
        }

        return stdout;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

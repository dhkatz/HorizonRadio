using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Diagnostics;

/// <summary>
/// Opt-in, structured diagnostics for the metadata pipeline. When enabled, every song the radio
/// observes and every resolve pass writes one JSON line to a file under the diagnostics directory,
/// building a full per-song picture: the raw ICY title, the deterministic parse, the model's
/// extraction + latency, each provider's contribution for each interpretation tried, and the final
/// resolved track. A concise line per song also goes to the in-app Console tab.
///
/// Off by default and inert when off (a single bool check on each capture call). Enabled either by
/// the toggle in the About tab (persisted to <c>diagnostics/settings.json</c>) or the
/// <c>HZN_META_TRACE</c> environment variable, so a user hitting a metadata bug can switch it on,
/// reproduce, and attach the file to a report.
///
/// Deliberately static, mirroring <see cref="ProcessConsole"/>: the producers (the radio source,
/// the resolver) are created deep in Core without a logging dependency.
/// </summary>
public static class MetadataTrace
{
    private const string ConsoleTag = "metadata-trace";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Keep CJK titles readable in the log rather than \uXXXX-escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        // The attempt records below use public fields, which STJ skips by default.
        IncludeFields = true,
        // Uniform camelCase across the field-based attempt records and the anonymous ones.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    /// <summary>True while a trace file is open and capturing.</summary>
    public static bool Enabled { get; private set; }

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HorizonRadio", "diagnostics");

    private static string SettingsPath => Path.Combine(Directory, "settings.json");

    /// <summary>Apply the persisted setting (and the <c>HZN_META_TRACE</c> override) at startup.
    /// The env var wins when set to anything other than 0/false/off (useful for headless runs).</summary>
    public static void RestoreFromSettings()
    {
        var env = Environment.GetEnvironmentVariable("HZN_META_TRACE")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(env))
        {
            // Any value except an explicit falsy one turns capture on (case/whitespace-insensitive,
            // so "False"/" OFF " disable as intended rather than accidentally enabling).
            if (env is not ("0" or "false" or "off" or "no")) { SetEnabled(true, persist: false); return; }
            return; // explicitly disabled via env → don't fall through to the persisted setting
        }
        if (ReadPersistedEnabled()) SetEnabled(true, persist: false);
    }

    /// <summary>Turn capture on/off. Opening a session starts a fresh timestamped file; the choice
    /// is persisted so it survives a restart (unless <paramref name="persist"/> is false).</summary>
    public static void SetEnabled(bool on, bool persist = true)
    {
        lock (Gate)
        {
            if (on && _writer is null)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    var path = Path.Combine(Directory, $"metadata-trace-{stamp}.jsonl");
                    _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                    Enabled = true;
                    ProcessConsole.Append(ConsoleTag, $"capture started → {path}");
                }
                catch (Exception ex)
                {
                    ProcessConsole.Append(ConsoleTag, $"could not start capture: {ex.Message}");
                    _writer = null;
                    Enabled = false;
                }
            }
            else if (!on && _writer is not null)
            {
                try { _writer.Flush(); _writer.Dispose(); } catch { }
                _writer = null;
                Enabled = false;
                ProcessConsole.Append(ConsoleTag, "capture stopped");
            }
        }
        if (persist) WritePersistedEnabled(on);
    }

    // -- Song / model capture (from the radio source) --

    /// <summary>One observed ICY title: the raw string and the deterministic parse.</summary>
    public static void Song(string station, string raw, string? parsedArtist, string parsedTitle,
        string confidence, IReadOnlyList<TitleCandidate>? candidates, string modelMode, bool modelWillRun)
    {
        if (!Enabled) return;
        WriteLine(new
        {
            t = "song",
            ts = Now(),
            station,
            raw,
            parse = new { artist = parsedArtist, title = parsedTitle, confidence },
            candidates = Cands(candidates),
            model = new { mode = modelMode, willRun = modelWillRun },
        });
        ProcessConsole.Append(ConsoleTag, $"song: \"{raw}\" → {parsedArtist} / {parsedTitle} [{confidence}]");
    }

    /// <summary>The model's extraction for a raw title, with inference latency.</summary>
    public static void Model(string raw, long latencyMs, IReadOnlyList<TitleCandidate> extracted, bool applied)
    {
        if (!Enabled) return;
        WriteLine(new
        {
            t = "model",
            ts = Now(),
            raw,
            latencyMs,
            applied,
            extracted = Cands(extracted),
        });
        var top = extracted.Count > 0 ? $"{extracted[0].Artist} / {extracted[0].Title}" : "(none)";
        ProcessConsole.Append(ConsoleTag, $"model: \"{raw}\" → {top} ({latencyMs} ms)");
    }

    // -- Resolve capture (from MetadataResolver). One scope per ResolveAsync, isolated per async
    //    flow so concurrent list-enrichment resolves don't interleave. --

    private sealed class ResolveScope
    {
        public string? SeedArtist;
        public string SeedTitle = "";
        public string? ExternalId;
        public List<TitleCandidate>? Candidates;
        public List<AttemptRec> Attempts = new();
    }

    private sealed class AttemptRec
    {
        public string? InterpArtist;
        public string InterpTitle = "";
        public bool Matched;
        public List<ProviderRec> Providers = new();
    }

    // One contributor's record for an attempt: the final contribution it produced (from the
    // resolver) merged with the raw catalog results it examined (from the provider's own search).
    private sealed class ProviderRec
    {
        public string Id = "";
        public bool Matched;
        public string? Artist;
        public string? Title;
        public string? Album;
        public int ArtBytes;
        // The cleaned query the provider sent the catalog, and every result it scored (null score =
        // rejected by the match guard). Present only for the text-search path, not cache hits.
        public string? Query;
        public List<object>? Considered;
    }

    /// <summary>One raw catalog result a provider considered for an interpretation, with the score
    /// <see cref="HorizonRadio.Core.Metadata.SearchTerms.MatchScore"/> gave it (null = rejected by
    /// the guard). Captured so a trace line can be replayed offline — feed the attempt's interp
    /// (title/artist) and each candidate back through MatchScore to test a scoring change without
    /// re-hitting the network. Providers build these only while <see cref="Enabled"/>.</summary>
    public readonly record struct CatalogCandidate(string? Title, string? Artist, string? Album, double? Score);

    private static readonly AsyncLocal<ResolveScope?> Scope = new();

    public static void BeginResolve(Track seed)
    {
        if (!Enabled) { Scope.Value = null; return; }
        Scope.Value = new ResolveScope
        {
            SeedArtist = seed.Artist,
            SeedTitle = seed.Title,
            ExternalId = seed.ExternalId,
            Candidates = seed.Candidates is { Count: > 0 } c ? new List<TitleCandidate>(c) : null,
        };
    }

    public static void BeginAttempt(string? interpArtist, string interpTitle)
    {
        var s = Scope.Value;
        s?.Attempts.Add(new AttemptRec { InterpArtist = interpArtist, InterpTitle = interpTitle });
    }

    public static void Provider(string id, bool matched, string? artist, string? title, string? album, int artBytes)
    {
        var s = Scope.Value;
        if (s is null || s.Attempts.Count == 0) return;
        var attempt = s.Attempts[^1];
        var rec = GetOrAddProvider(attempt, id);
        rec.Matched = matched;
        rec.Artist = artist;
        rec.Title = title;
        rec.Album = album;
        rec.ArtBytes = artBytes;
        if (matched) attempt.Matched = true;
    }

    /// <summary>A capture list for a provider's considered results, or null when not capturing — so a
    /// provider can write <c>var capture = MetadataTrace.NewCapture(); capture?.Add(...)</c> without
    /// repeating the <see cref="Enabled"/> check, then hand it straight to <see cref="ProviderSearch"/>.</summary>
    public static List<CatalogCandidate>? NewCapture() => Enabled ? new List<CatalogCandidate>() : null;

    /// <summary>The raw catalog results a provider examined for the current interpretation, with the
    /// query it sent. Called from inside the provider during its search (the resolve scope flows in
    /// via <see cref="AsyncLocal{T}"/>); merged into that provider's record for the attempt. This is
    /// the data that makes a trace line replayable — see <see cref="CatalogCandidate"/>. A null
    /// <paramref name="considered"/> (capture was off) is a no-op.</summary>
    public static void ProviderSearch(string id, string query, IReadOnlyList<CatalogCandidate>? considered)
    {
        if (considered is null) return;
        var s = Scope.Value;
        if (s is null || s.Attempts.Count == 0) return;
        var rec = GetOrAddProvider(s.Attempts[^1], id);
        rec.Query = query;
        rec.Considered = considered.Count == 0
            ? null
            : considered.Select(c => (object)new { c.Title, c.Artist, c.Album, score = c.Score }).ToList();
    }

    // One provider record per id within an attempt: the resolver's Provider(...) and the provider's
    // own ProviderSearch(...) target the same record and fill different fields.
    private static ProviderRec GetOrAddProvider(AttemptRec attempt, string id)
    {
        foreach (var p in attempt.Providers)
            if (p.Id == id) return p;
        var rec = new ProviderRec { Id = id };
        attempt.Providers.Add(rec);
        return rec;
    }

    public static void EndResolve(Track final)
    {
        var s = Scope.Value;
        Scope.Value = null;
        if (s is null || !Enabled) return;
        WriteLine(new
        {
            t = "resolve",
            ts = Now(),
            externalId = s.ExternalId,
            seed = new { artist = s.SeedArtist, title = s.SeedTitle },
            candidates = Cands(s.Candidates),
            attempts = s.Attempts,
            final = new
            {
                artist = final.Artist,
                title = final.Title,
                album = final.Album,
                artBytes = final.AlbumArt?.Length ?? 0,
            },
        });
        ProcessConsole.Append(ConsoleTag,
            $"resolved: {final.Artist} / {final.Title} · {final.Album ?? "(no album)"} · art {(final.AlbumArt?.Length ?? 0)}B");
    }

    // -- helpers --

    private static List<object>? Cands(IReadOnlyList<TitleCandidate>? cands) =>
        cands is null ? null : new List<object>(System.Linq.Enumerable.Select(cands, c => (object)new { artist = c.Artist, title = c.Title }));

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static void WriteLine(object record)
    {
        lock (Gate)
        {
            if (_writer is null) return;
            try { _writer.WriteLine(JsonSerializer.Serialize(record, JsonOpts)); }
            catch { /* never let diagnostics break playback */ }
        }
    }

    private static bool ReadPersistedEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static void WritePersistedEnabled(bool enabled)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { enabled }, JsonOpts));
        }
        catch { /* best-effort */ }
    }
}

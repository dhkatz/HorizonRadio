using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Settings;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;

namespace HorizonRadio.Core.Diagnostics;

/// <summary>
/// Opt-in, structured diagnostics for the metadata pipeline. When enabled, every song the radio
/// observes and every resolve pass writes one JSON line to a file under the diagnostics directory,
/// building a full per-song picture: the raw ICY title, the deterministic parse, the model's
/// extraction + latency, each provider's contribution for each interpretation tried, and the final
/// resolved track. A concise line per song also goes to the in-app Console tab.
///
/// Off by default and inert when off (a single bool check on each capture call). Enabled either by
/// the toggle in the About tab (kept as <see cref="EnabledSetting"/>) or the
/// <c>HZN_META_TRACE</c> environment variable, so a user hitting a metadata bug can switch it on,
/// reproduce, and attach the file to a report.
///
/// One per host, from the container: it's the host's <see cref="IMetadataTrace"/> (what sources and
/// providers record) and the resolver's record of each lookup (<see cref="BeginResolve"/> …
/// <see cref="EndResolve"/>). The files go in the app's <c>diagnostics</c> folder.
/// </summary>
public sealed class MetadataTrace : IMetadataTrace, IDisposable
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

    private readonly Lock _gate = new();
    private readonly SettingsStore _settings;
    private readonly IPluginLog _log;
    private StreamWriter? _writer;

    /// <summary>Whether capture is on, kept across restarts.</summary>
    public static readonly Setting<bool> EnabledSetting = new("diagnostics.metadataTrace", SettingScope.Device, false);

    /// <summary>Starts capturing if the user left it on (or <c>HZN_META_TRACE</c> says so), and keeps
    /// <paramref name="settings"/> to remember later changes in.</summary>
    public MetadataTrace(SettingsStore settings, IAppFolders folders, IPluginLog log)
    {
        _settings = settings;
        _log = log;
        Directory = folders.Data("diagnostics");
        RestoreFromSettings();
    }

    /// <summary>True while a trace file is open and capturing.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Where the trace files go.</summary>
    public string Directory { get; }

    // Apply the persisted setting (and the HZN_META_TRACE override). The env var wins when set to
    // anything other than 0/false/off (useful for headless runs).
    private void RestoreFromSettings()
    {
        var env = Environment.GetEnvironmentVariable("HZN_META_TRACE")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(env))
        {
            // Any value except an explicit falsy one turns capture on (case/whitespace-insensitive,
            // so "False"/" OFF " disable as intended rather than accidentally enabling).
            if (env is not ("0" or "false" or "off" or "no")) { SetEnabled(true, persist: false); return; }
            return; // explicitly disabled via env → don't fall through to the persisted setting
        }
        if (_settings.Get(EnabledSetting)) SetEnabled(true, persist: false);
    }

    /// <summary>Turn capture on/off. Opening a session starts a fresh timestamped file; the choice
    /// is persisted so it survives a restart (unless <paramref name="persist"/> is false).</summary>
    public void SetEnabled(bool on, bool persist = true)
    {
        lock (_gate)
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
                    _log.Write(ConsoleTag, $"capture started → {path}");
                }
                catch (Exception ex)
                {
                    _log.Write(ConsoleTag, $"could not start capture: {ex.Message}");
                    _writer = null;
                    Enabled = false;
                }
            }
            else if (!on && _writer is not null)
            {
                try { _writer.Flush(); _writer.Dispose(); } catch { }
                _writer = null;
                Enabled = false;
                _log.Write(ConsoleTag, "capture stopped");
            }
        }
        if (persist) _settings.Set(EnabledSetting, on);
    }

    // -- Song / model capture (from the radio source) --

    /// <summary>One observed ICY title: the raw string and the deterministic parse.</summary>
    public void Song(string station, string raw, string? parsedArtist, string parsedTitle,
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
        _log.Write(ConsoleTag, $"song: \"{raw}\" → {parsedArtist} / {parsedTitle} [{confidence}]");
    }

    /// <summary>The model's extraction for a raw title, with inference latency.</summary>
    public void Model(string raw, long latencyMs, IReadOnlyList<TitleCandidate> extracted, bool applied)
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
        _log.Write(ConsoleTag, $"model: \"{raw}\" → {top} ({latencyMs} ms)");
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
        // How long the provider took to answer, including its rate-limit wait. Null = not asked
        // (an earlier interpretation already matched, or the provider was skipped).
        public long? Ms;
        // Why the provider couldn't answer (an HTTP error, a timeout, not signed in), when it
        // couldn't. A failure isn't a miss: nothing was cached, and it's retried next time.
        public string? Error;
        public string? Artist;
        public string? Title;
        public string? Album;
        public int ArtBytes;
        // The cleaned query the provider sent the catalog, and every result it scored (null score =
        // rejected by the match guard). Present only for the text-search path, not cache hits.
        public string? Query;
        public List<object>? Considered;
    }

    private readonly AsyncLocal<ResolveScope?> _scope = new();

    public void BeginResolve(Track seed)
    {
        if (!Enabled) { _scope.Value = null; return; }
        _scope.Value = new ResolveScope
        {
            SeedArtist = seed.Artist,
            SeedTitle = seed.Title,
            ExternalId = seed.ExternalId,
            Candidates = seed.Candidates is { Count: > 0 } c ? new List<TitleCandidate>(c) : null,
        };
    }

    /// <summary>Start recording one interpretation. <paramref name="providerIds"/> lists the
    /// providers about to be asked, in priority order, so the record reads in that order even
    /// though they answer in parallel.</summary>
    public void BeginAttempt(string? interpArtist, string interpTitle, IEnumerable<string>? providerIds = null)
    {
        var s = _scope.Value;
        if (s is null) return;
        var attempt = new AttemptRec { InterpArtist = interpArtist, InterpTitle = interpTitle };
        if (providerIds != null)
            foreach (var id in providerIds) attempt.Providers.Add(new ProviderRec { Id = id });
        lock (s) s.Attempts.Add(attempt);
    }

    public void Provider(string id, bool matched, string? artist, string? title, string? album, int artBytes,
                                long? elapsedMs = null, string? error = null)
    {
        var s = _scope.Value;
        if (s is null) return;
        // Providers answer in parallel, so records are written from several threads.
        lock (s)
        {
            if (s.Attempts.Count == 0) return;
            var attempt = s.Attempts[^1];
            var rec = GetOrAddProvider(attempt, id);
            rec.Matched = matched;
            rec.Artist = artist;
            rec.Title = title;
            rec.Album = album;
            rec.ArtBytes = artBytes;
            rec.Ms = elapsedMs;
            rec.Error = error;
            if (matched) attempt.Matched = true;
        }
    }

    /// <summary>A capture list for a provider's considered results, or null when not capturing — so a
    /// provider can write <c>var capture = MetadataTrace.NewCapture(); capture?.Add(...)</c> without
    /// repeating the <see cref="Enabled"/> check, then hand it straight to <see cref="ProviderSearch"/>.</summary>
    public List<CatalogCandidate>? NewCapture() => Enabled ? new List<CatalogCandidate>() : null;

    /// <summary>The raw catalog results a provider examined for the current interpretation, with the
    /// query it sent. Called from inside the provider during its search (the resolve scope flows in
    /// via <see cref="AsyncLocal{T}"/>); merged into that provider's record for the attempt. This is
    /// the data that makes a trace line replayable — see <see cref="CatalogCandidate"/>. A null
    /// <paramref name="considered"/> (capture was off) is a no-op.</summary>
    public void ProviderSearch(string providerId, string query, IReadOnlyList<CatalogCandidate>? considered)
    {
        if (considered is null) return;
        var s = _scope.Value;
        if (s is null) return;
        lock (s)
        {
            if (s.Attempts.Count == 0) return;
            var rec = GetOrAddProvider(s.Attempts[^1], providerId);
            rec.Query = query;
            rec.Considered = considered.Count == 0
                ? null
                : considered.Select(c => (object)new { c.Title, c.Artist, c.Album, score = c.Score }).ToList();
        }
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

    /// <param name="elapsedMs">How long the whole resolve took, every interpretation included.</param>
    public void EndResolve(Track final, long? elapsedMs = null)
    {
        var s = _scope.Value;
        _scope.Value = null;
        if (s is null || !Enabled) return;
        WriteLine(new
        {
            t = "resolve",
            ts = Now(),
            ms = elapsedMs,
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
        _log.Write(ConsoleTag,
            $"resolved: {final.Artist} / {final.Title} · {final.Album ?? "(no album)"} · art {(final.AlbumArt?.Length ?? 0)}B" +
            (elapsedMs is { } ms ? $" · {ms} ms" : ""));
    }

    // -- helpers --

    private static List<object>? Cands(IReadOnlyList<TitleCandidate>? cands) =>
        cands is null ? null : new List<object>(System.Linq.Enumerable.Select(cands, c => (object)new { artist = c.Artist, title = c.Title }));

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private void WriteLine(object record)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try { _writer.WriteLine(JsonSerializer.Serialize(record, JsonOpts)); }
            catch { /* never let diagnostics break playback */ }
        }
    }

    /// <summary>Closes the trace file (the host's container disposes it as the app exits).</summary>
    public void Dispose() => SetEnabled(false, persist: false);
}

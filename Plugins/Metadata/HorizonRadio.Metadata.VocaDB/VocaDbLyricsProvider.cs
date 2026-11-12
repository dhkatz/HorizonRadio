using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Metadata.VocaDb;

/// <summary>
/// Lyrics from VocaDB: the original lyrics of a Vocaloid / UTAU / SynthV song, usually with a
/// romanization and translations, which the listener can switch between. They're never timed, so
/// they're used when Unison and LRCLIB have nothing synced. The song is found the same way the
/// VocaDB metadata provider finds it (artist-scoped, cross-script).
/// </summary>
public sealed class VocaDbLyricsProvider : ILyricsProvider
{
    public string Id => "vocadb";
    public int SortOrder => 40;

    private readonly VocaDbProvider _vocadb;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly VocaloidLyricsWiki _wiki;

    public VocaDbLyricsProvider(VocaDbProvider vocadb, HttpClient? http = null)
    {
        _vocadb = vocadb;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // Miraheze, like Wikimedia, asks API clients to say who they are.
        var ua = $"HorizonRadio/{BuildInfo.Current.Version} (https://github.com/dhkatz/HorizonRadio)";
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(ua))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HorizonRadio");
        _wiki = new VocaloidLyricsWiki(_http);
    }

    public async Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        using var lookup = CatalogLookup.Begin();
        var match = await _vocadb.FindSongAsync(query.Title, query.Artist, capture: null, ct).ConfigureAwait(false);

        SongLyrics? lyrics = null;
        if (match is { Id: > 0 })
        {
            lyrics = await LyricsOfAsync(match.Id, ct).ConfigureAwait(false);
            // A remix or cover often has no lyrics entry of its own, but sings the original's.
            if (lyrics == null && match.OriginalVersionId is { } original)
                lyrics = await LyricsOfAsync(original, ct).ConfigureAwait(false);

            // VocaDB has the song but not its lyrics: the Vocaloid Lyrics Wiki often does.
            lyrics ??= await FromWikiAsync(match.Id, ct).ConfigureAwait(false);
            if (lyrics == null && match.OriginalVersionId is { } originalId)
                lyrics = await FromWikiAsync(originalId, ct).ConfigureAwait(false);
        }

        if (lyrics == null && lookup.Failed) throw lookup.Unavailable(Id);
        return lyrics;
    }

    private async Task<SongLyrics?> FromWikiAsync(int songId, CancellationToken ct)
    {
        try
        {
            return await _wiki.FindAsync(songId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            CatalogLookup.ReportFailure($"Vocaloid Lyrics Wiki: {ex.GetType().Name}");
            return null;
        }
    }

    private async Task<SongLyrics?> LyricsOfAsync(int songId, CancellationToken ct)
    {
        var song = await _vocadb.GetSongAsync(songId, "Lyrics", ct).ConfigureAwait(false);
        return song is { } s ? SelectLyrics(s) : null;
    }

    /// <summary>The original lyrics, with the romanization and translations as variants. Pure
    /// JSON-in for unit testing.</summary>
    internal static SongLyrics? SelectLyrics(JsonElement song)
    {
        if (!song.TryGetProperty("lyrics", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        var entries = new List<(string Type, string Culture, IReadOnlyList<LyricLine> Lines)>();
        foreach (var l in list.EnumerateArray())
        {
            var text = l.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var lines = ToLines(text);
            if (lines.Count == 0) continue;
            var type = l.TryGetProperty("translationType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            entries.Add((type, Culture(l), lines));
        }
        if (entries.Count == 0) return null;

        // The original first; a song with only a translation still shows that.
        var original = entries.FirstOrDefault(e => e.Type == "Original");
        if (original.Lines == null) original = entries[0];

        var variants = new List<LyricsVariant>();
        var labels = new HashSet<string>(StringComparer.Ordinal) { "Original" };
        // The romanization next to the original, then translations.
        foreach (var e in entries.OrderBy(e => e.Type == "Romanized" ? 0 : 1))
        {
            if (ReferenceEquals(e.Lines, original.Lines)) continue;
            var label = e.Type == "Romanized"
                ? (original.Culture == "ja" ? "Romaji" : "Romanized")
                : LanguageName(e.Culture);
            if (labels.Add(label)) variants.Add(new LyricsVariant(label, e.Lines)); // one per language
        }

        return new SongLyrics("vocadb", false, original.Lines, "Lyrics from VocaDB", variants.Count > 0 ? variants : null);
    }

    // Lines as written, keeping one blank line between verses so the sheet reads like the lyrics.
    private static List<LyricLine> ToLines(string? text)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(text)) return lines;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Text.Length == 0)) continue;
            lines.Add(new LyricLine(TimeSpan.Zero, line));
        }
        while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // VocaDB lists "cultureCodes" (newer) or a single "cultureCode".
    private static string Culture(JsonElement lyric)
    {
        if (lyric.TryGetProperty("cultureCodes", out var codes) && codes.ValueKind == JsonValueKind.Array)
            foreach (var c in codes.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(c.GetString())) return c.GetString()!;
        return lyric.TryGetProperty("cultureCode", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString() ?? "" : "";
    }

    private static string LanguageName(string culture)
    {
        if (culture.Length == 0 || culture == "und") return "Translation";
        try
        {
            var name = CultureInfo.GetCultureInfo(culture).EnglishName;
            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? name[..paren] : name;
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp) _http.Dispose();
        return _vocadb.DisposeAsync();
    }
}

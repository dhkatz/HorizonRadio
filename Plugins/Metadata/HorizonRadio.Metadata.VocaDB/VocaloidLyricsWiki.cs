using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Sdk.Lyrics;

namespace HorizonRadio.Metadata.VocaDb;

/// <summary>
/// The Vocaloid Lyrics Wiki (vocaloidlyrics.miraheze.org): lyrics for many Vocaloid songs whose
/// VocaDB entry has none, usually with romaji and an English translation side by side. A page is
/// found by the VocaDB song it links to (<c>{{VDB|S/127584}}</c>), so it's this exact song, not
/// one with a similar name. The lyrics are one wiki table, a column per version.
/// Content is CC BY-SA 4.0: the credit names the wiki and the translator.
/// </summary>
internal sealed partial class VocaloidLyricsWiki(HttpClient http)
{
    private const string Api = "https://vocaloidlyrics.miraheze.org/w/api.php";

    /// <summary>The lyrics on the wiki page for VocaDB song <paramref name="vocadbSongId"/>, or
    /// null when there's no such page. Throws when the wiki couldn't be reached.</summary>
    public async Task<SongLyrics?> FindAsync(int vocadbSongId, CancellationToken ct)
    {
        // The search matches "S/1399" inside "S/13990" too: check the link on each page.
        foreach (var title in await SearchAsync($"insource:\"S/{vocadbSongId}\"", ct).ConfigureAwait(false))
        {
            var wikitext = await WikitextAsync(title, ct).ConfigureAwait(false);
            if (wikitext != null && LinksSong(wikitext, vocadbSongId)) return Parse(wikitext);
        }
        return null;
    }

    private async Task<IReadOnlyList<string>> SearchAsync(string search, CancellationToken ct)
    {
        var url = $"{Api}?action=query&list=search&srlimit=5&format=json&formatversion=2&srsearch={Uri.EscapeDataString(search)}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("search", out var hits)) return [];
        return [.. hits.EnumerateArray()
            .Select(h => h.TryGetProperty("title", out var t) ? t.GetString() : null)
            .OfType<string>()];
    }

    private async Task<string?> WikitextAsync(string title, CancellationToken ct)
    {
        var url = $"{Api}?action=parse&prop=wikitext&redirects=1&format=json&formatversion=2&page={Uri.EscapeDataString(title)}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("parse", out var p) && p.TryGetProperty("wikitext", out var w)
            ? w.GetString()
            : null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
    }

    /// <summary>Whether the page links VocaDB song <paramref name="id"/> (and not one whose id
    /// merely starts with it).</summary>
    internal static bool LinksSong(string wikitext, int id) =>
        Regex.IsMatch(wikitext, $@"\bS/{id}(?!\d)");

    [GeneratedRegex(@"\{\{\s*lyrics toggle\s*\|([^}]*)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Toggle();

    [GeneratedRegex(@"\{\{\s*Translator\s*\|([^}|]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Translator();

    [GeneratedRegex(@"\{\{\s*ruby\s*\|([^|}]*)\|[^}]*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Ruby();

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex Template();

    [GeneratedRegex(@"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"'{2,}")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"colspan\s*=\s*""?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Colspan();

    // ASCII whitespace only: a full-width space between Japanese phrases is part of the lyrics.
    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();

    /// <summary>The page's lyrics: the first column as the original, the others (romaji, English)
    /// as versions to switch to. Null when the page has no lyrics table. Pure, for unit testing.</summary>
    internal static SongLyrics? Parse(string wikitext)
    {
        var start = wikitext.IndexOf("==Lyrics", StringComparison.OrdinalIgnoreCase);
        var table = wikitext.IndexOf("{|", start < 0 ? 0 : start, StringComparison.Ordinal);
        if (table < 0) return null;
        var end = wikitext.IndexOf("\n|}", table, StringComparison.Ordinal);
        var body = wikitext[table..(end < 0 ? wikitext.Length : end)];

        // Column names, in order: {{lyrics toggle|jp:Japanese|rom:Romaji|eng:English}}.
        var toggle = Toggle().Match(wikitext, start < 0 ? 0 : start);
        var labels = toggle.Success
            ? toggle.Groups[1].Value.Split('|').Select(p => p.Contains(':') ? p[(p.IndexOf(':') + 1)..].Trim() : p.Trim()).ToList()
            : [];

        var rows = new List<List<string?>>();
        List<string?>? row = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("|-", StringComparison.Ordinal))
            {
                row = new List<string?>();
                rows.Add(row);
                continue;
            }
            if (line.StartsWith('!')) { row = null; continue; } // the header row
            if (row == null || !line.StartsWith('|') || line.StartsWith("|}", StringComparison.Ordinal)) continue;

            foreach (var cell in line[1..].Split("||"))
            {
                var (text, span) = Cell(cell);
                for (int i = 0; i < span; i++) row.Add(text);
            }
        }

        int columns = Math.Max(labels.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        if (columns == 0) return null;

        var lines = Enumerable.Range(0, columns).Select(_ => new List<LyricLine>()).ToList();
        foreach (var r in rows.Where(r => r.Count > 0))
        {
            for (int c = 0; c < columns; c++)
            {
                // A short row (one cell spanning all, or a verse break) fills every column.
                var text = c < r.Count ? r[c] : r[^1];
                var col = lines[c];
                if (text == null)
                {
                    if (col.Count > 0 && col[^1].Text.Length > 0) col.Add(new LyricLine(TimeSpan.Zero, ""));
                }
                else
                {
                    col.Add(new LyricLine(TimeSpan.Zero, text));
                }
            }
        }
        foreach (var col in lines)
            while (col.Count > 0 && col[^1].Text.Length == 0) col.RemoveAt(col.Count - 1);
        if (lines[0].Count == 0) return null;

        var variants = new List<LyricsVariant>();
        for (int c = 1; c < columns; c++)
        {
            if (lines[c].Count == 0) continue;
            var label = c < labels.Count && labels[c].Length > 0 ? labels[c] : $"Version {c + 1}";
            if (variants.All(v => v.Label != label)) variants.Add(new LyricsVariant(label, lines[c]));
        }

        var translator = Translator().Match(wikitext) is { Success: true } m ? Clean(m.Groups[1].Value) : null;
        var credit = "Lyrics from Vocaloid Lyrics Wiki (CC BY-SA 4.0)"
                     + (string.IsNullOrEmpty(translator) ? "" : $" · translated by {translator}");
        return new SongLyrics("vocadb", false, lines[0], credit, variants.Count > 0 ? variants : null);
    }

    // One table cell: its text (null for a verse break) and how many columns it spans.
    // "colspan="2" | text" carries attributes before a single "|".
    private static (string? Text, int Span) Cell(string cell)
    {
        int span = 1;
        var bar = cell.IndexOf('|');
        if (bar > 0 && !cell[..bar].Contains("[[") && !cell[..bar].Contains("{{") && cell[..bar].Contains('='))
        {
            if (Colspan().Match(cell[..bar]) is { Success: true } cs && int.TryParse(cs.Groups[1].Value, out var n)) span = Math.Clamp(n, 1, 8);
            cell = cell[(bar + 1)..];
        }
        var text = Clean(LineBreak().Replace(cell, " "));
        return (text.Length == 0 ? null : text, span);
    }

    private static string Clean(string s)
    {
        s = Ruby().Replace(s, "$1"); // {{ruby|漢字|かんじ}} → 漢字
        for (int i = 0; i < 3 && s.Contains("{{", StringComparison.Ordinal); i++) s = Template().Replace(s, "");
        s = WikiLink().Replace(s, "$1");
        s = Emphasis().Replace(s, "");
        s = HtmlTag().Replace(s, "");
        s = System.Net.WebUtility.HtmlDecode(s);
        return Whitespace().Replace(s, " ").Trim(' ');
    }
}

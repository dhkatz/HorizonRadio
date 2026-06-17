using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// A small, dependency-free Japanese <em>kana</em> romanizer (Hepburn-ish), plus a fuzzy compare.
/// It exists to verify the cross-script artist bridge in <see cref="SearchTerms.MatchScore"/>: a
/// romaji broadcast name ("Bunmyaku") should match the same act written in kana, but NOT an
/// unrelated kana-named artist who merely covered the same-titled song (the "くろくも vs HachiojiP"
/// false match). We romanize the kana name and check it actually sounds like the query.
///
/// Kana only — kanji readings need a dictionary (and a 50 MB+ dependency), and a kanji name's
/// reading often differs from the artist's chosen romanization anyway. <see cref="TryRomanize"/>
/// returns null when the text isn't purely kana, so the caller leaves kanji names on their prior
/// (unverified-but-accepted) behavior rather than rejecting them.
/// </summary>
public static class Romaji
{
    // Accept a match when the romanizations are this close (1 - editDistance/maxLen). Loose enough
    // to bridge Hepburn/Nippon and long-vowel spelling wobble ("shi"/"si", "ou"/"o"), tight enough
    // that two different names ("kurokumo" vs "hachiojip") fall well short.
    private const double SimilarityThreshold = 0.7;

    /// <summary>Romanize a purely-kana string (hiragana/katakana, spaces/·/long-marks allowed), or
    /// null if it contains kanji or any other non-kana letter — i.e. "can't fully read this".</summary>
    public static string? TryRomanize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        var sb = new StringBuilder();
        var sokuon = false;     // pending っ/ッ → double the next consonant
        char lastVowel = '\0';
        var sawKana = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c is '・' or '･' or '\'') continue; // skip separators / apostrophes

            // Long-vowel marks repeat the previous vowel.
            if (c is 'ー' or '〜' or '～' or 'ｰ')
            {
                if (lastVowel != '\0') { sb.Append(lastVowel); }
                continue;
            }

            // Fold katakana onto hiragana (same readings); leave hiragana as-is.
            var h = c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;

            if (h is 'っ') { sokuon = true; sawKana = true; continue; }

            // Try a two-kana digraph (き+ゃ → kya) first, else a single kana.
            string? r = null;
            if (i + 1 < s.Length)
            {
                var n = s[i + 1] is >= 'ァ' and <= 'ヶ' ? (char)(s[i + 1] - 0x60) : s[i + 1];
                if (n is 'ゃ' or 'ゅ' or 'ょ' && Digraphs.TryGetValue(new string(new[] { h, n }), out var dg))
                {
                    r = dg;
                    i++;
                }
            }
            r ??= Mono.TryGetValue(h, out var mono) ? mono : null;

            if (r is null) return null; // kanji or some non-kana letter → can't fully romanize
            sawKana = true;

            if (sokuon && r.Length > 0 && !IsVowel(r[0]) && r[0] != 'n')
            {
                sb.Append(r[0]); // double the leading consonant (って → tte)
            }
            sokuon = false;

            sb.Append(r);
            if (r.Length > 0 && IsVowel(r[^1])) lastVowel = r[^1];
        }

        var result = sb.ToString();
        return sawKana && result.Length > 0 ? result : null;
    }

    /// <summary>True when two romanized names are the same artist allowing for romanization
    /// variants (Hepburn vs Nippon, long-vowel spelling, sokuon), via a normalized edit distance.</summary>
    public static bool SoundsLike(string? a, string? b)
    {
        var ca = Canonicalize(a);
        var cb = Canonicalize(b);
        if (ca.Length == 0 || cb.Length == 0) return false;
        if (ca == cb) return true;
        // A clearly-contained name (one is a prefix/substring of the other) is the same act with a
        // suffix like a producer "P" — but only when both are long enough to be meaningful.
        if (ca.Length >= 3 && cb.Length >= 3 && (ca.Contains(cb) || cb.Contains(ca))) return true;

        var dist = Levenshtein(ca, cb);
        var sim = 1.0 - (double)dist / Math.Max(ca.Length, cb.Length);
        return sim >= SimilarityThreshold;
    }

    // Fold romaji to a canonical shape so Hepburn/Nippon and long-vowel/sokuon spellings of the
    // same sound compare equal: lowercase alnum, unify digraphs, drop long vowels, collapse doubles.
    private static string Canonicalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        foreach (var (from, to) in DigraphFolds) t = t.Replace(from, to);
        t = t.Replace("ou", "o").Replace("oo", "o").Replace("uu", "u");
        // Collapse any doubled letter (long vowels "aa", sokuon "tt", "kk", …) to a single.
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
            if (sb.Length == 0 || sb[^1] != ch) sb.Append(ch);
        return sb.ToString();
    }

    private static bool IsVowel(char c) => c is 'a' or 'i' or 'u' or 'e' or 'o';

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }

    // Hepburn → a single canonical form (Nippon-style) so both spellings of one sound collapse.
    private static readonly (string From, string To)[] DigraphFolds =
    {
        ("sha", "sya"), ("shu", "syu"), ("sho", "syo"), ("shi", "si"),
        ("cha", "tya"), ("chu", "tyu"), ("cho", "tyo"), ("chi", "ti"),
        ("tsu", "tu"), ("ja", "zya"), ("ju", "zyu"), ("jo", "zyo"), ("ji", "zi"),
        ("fu", "hu"),
    };

    private static readonly Dictionary<char, string> Mono = new()
    {
        ['あ'] = "a",
        ['い'] = "i",
        ['う'] = "u",
        ['え'] = "e",
        ['お'] = "o",
        ['か'] = "ka",
        ['き'] = "ki",
        ['く'] = "ku",
        ['け'] = "ke",
        ['こ'] = "ko",
        ['が'] = "ga",
        ['ぎ'] = "gi",
        ['ぐ'] = "gu",
        ['げ'] = "ge",
        ['ご'] = "go",
        ['さ'] = "sa",
        ['し'] = "shi",
        ['す'] = "su",
        ['せ'] = "se",
        ['そ'] = "so",
        ['ざ'] = "za",
        ['じ'] = "ji",
        ['ず'] = "zu",
        ['ぜ'] = "ze",
        ['ぞ'] = "zo",
        ['た'] = "ta",
        ['ち'] = "chi",
        ['つ'] = "tsu",
        ['て'] = "te",
        ['と'] = "to",
        ['だ'] = "da",
        ['ぢ'] = "ji",
        ['づ'] = "zu",
        ['で'] = "de",
        ['ど'] = "do",
        ['な'] = "na",
        ['に'] = "ni",
        ['ぬ'] = "nu",
        ['ね'] = "ne",
        ['の'] = "no",
        ['は'] = "ha",
        ['ひ'] = "hi",
        ['ふ'] = "fu",
        ['へ'] = "he",
        ['ほ'] = "ho",
        ['ば'] = "ba",
        ['び'] = "bi",
        ['ぶ'] = "bu",
        ['べ'] = "be",
        ['ぼ'] = "bo",
        ['ぱ'] = "pa",
        ['ぴ'] = "pi",
        ['ぷ'] = "pu",
        ['ぺ'] = "pe",
        ['ぽ'] = "po",
        ['ま'] = "ma",
        ['み'] = "mi",
        ['む'] = "mu",
        ['め'] = "me",
        ['も'] = "mo",
        ['や'] = "ya",
        ['ゆ'] = "yu",
        ['よ'] = "yo",
        ['ら'] = "ra",
        ['り'] = "ri",
        ['る'] = "ru",
        ['れ'] = "re",
        ['ろ'] = "ro",
        ['わ'] = "wa",
        ['ゐ'] = "wi",
        ['ゑ'] = "we",
        ['を'] = "wo",
        ['ん'] = "n",
        ['ゔ'] = "vu",
        ['ぁ'] = "a",
        ['ぃ'] = "i",
        ['ぅ'] = "u",
        ['ぇ'] = "e",
        ['ぉ'] = "o",
        ['ゃ'] = "ya",
        ['ゅ'] = "yu",
        ['ょ'] = "yo",
        ['ゎ'] = "wa",
    };

    private static readonly Dictionary<string, string> Digraphs = new()
    {
        ["きゃ"] = "kya",
        ["きゅ"] = "kyu",
        ["きょ"] = "kyo",
        ["ぎゃ"] = "gya",
        ["ぎゅ"] = "gyu",
        ["ぎょ"] = "gyo",
        ["しゃ"] = "sha",
        ["しゅ"] = "shu",
        ["しょ"] = "sho",
        ["じゃ"] = "ja",
        ["じゅ"] = "ju",
        ["じょ"] = "jo",
        ["ちゃ"] = "cha",
        ["ちゅ"] = "chu",
        ["ちょ"] = "cho",
        ["ぢゃ"] = "ja",
        ["ぢゅ"] = "ju",
        ["ぢょ"] = "jo",
        ["にゃ"] = "nya",
        ["にゅ"] = "nyu",
        ["にょ"] = "nyo",
        ["ひゃ"] = "hya",
        ["ひゅ"] = "hyu",
        ["ひょ"] = "hyo",
        ["びゃ"] = "bya",
        ["びゅ"] = "byu",
        ["びょ"] = "byo",
        ["ぴゃ"] = "pya",
        ["ぴゅ"] = "pyu",
        ["ぴょ"] = "pyo",
        ["みゃ"] = "mya",
        ["みゅ"] = "myu",
        ["みょ"] = "myo",
        ["りゃ"] = "rya",
        ["りゅ"] = "ryu",
        ["りょ"] = "ryo",
    };
}

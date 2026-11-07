using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// The names of popular singing-synthesizer voices (Vocaloid, UTAU, CeVIO, Synthesizer V, VOICEROID…),
/// romanized and native. Stations often credit a song to its voice alone ("Hatsune Miku - Melt",
/// "【Hatsune Miku】Song"), and a voice sings thousands of songs, so that credit can't confirm a match
/// the way a producer's can: it only says who sings. See <see cref="IsVoicebankCredit"/>.
/// </summary>
public static class Voicebanks
{
    // A version or tone of the voice ("V4X", "English", "Append (Dark)"), not part of its name.
    private static readonly Regex Qualifier = new(
        @"\b(?:v\d+x?|nt|ai|sv|english|eng|append|native|sweet|dark|soft|solid|power|whisper|hard|light|vivid|warm|cold|original|unknown|act\d|ver)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The same, glued to a native name ("巡音ルカV4X").
    private static readonly Regex TrailingQualifier = new(
        @"(?<=[^\x00-\x7F])(?:v\d+x?|nt|sv|ai|english|append)+$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Separators between credited names: "Luka x Len", "Rin & Len", "GUMI, IA", "初音ミク・GUMI".
    private static readonly Regex Separator = new(
        @"\s+(?:x|×|&|and|with|feat\.?|ft\.?|featuring)\s+|[,，、/／&×・+]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Declared after the patterns Key uses: static fields initialize in order.
    private static readonly HashSet<string> Names = new(new[]
    {
        "hatsune miku", "初音ミク", "miku",
        "kagamine rin", "鏡音リン", "kagamine len", "鏡音レン", "kagamine rin len", "鏡音リンレン",
        "megurine luka", "巡音ルカ", "luka", "rin", "len",
        "kaito", "meiko", "gumi", "megpoid", "ia", "ia rocks",
        "kasane teto", "重音テト", "teto",
        "yuzuki yukari", "結月ゆかり", "yukari",
        "flower", "v flower", "kaai yuki", "歌愛ユキ", "otomachi una", "音街ウナ",
        "kafu", "可不", "星界", "裏命", "狐子", "羽累",
        "kamui gakupo", "神威がくぽ", "gackpoid", "gakupo",
        "utatane piko", "歌手音ピコ", "nekomura iroha", "猫村いろは", "sf a2 miki",
        "hiyama kiyoteru", "氷山キヨテル", "lily", "mayu", "cul", "fukase", "galaco",
        "kizuna akari", "紲星あかり", "kotonoha akane", "琴葉茜", "kotonoha aoi", "琴葉葵",
        "tohoku kiritan", "東北きりたん", "tohoku zunko", "東北ずん子", "zundamon", "ずんだもん",
        "akita neru", "亜北ネル", "yowane haku", "弱音ハク", "momone momo", "桃音モモ",
        "sato sasara", "さとうささら", "chis a",
    }.Select(Key), StringComparer.Ordinal);


    /// <summary>True when every name the credit lists is a synthesizer voice: "Hatsune Miku",
    /// "Gumi English", "Luka x Len", "巡音ルカV4X". False for a producer, for a credit that names a
    /// producer too ("ryo feat. 初音ミク"), and for an empty credit.</summary>
    public static bool IsVoicebankCredit(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return false;
        var names = Separator.Split(artist).Select(Key).Where(k => k.Length > 0).ToList();
        return names.Count > 0 && names.All(Names.Contains);
    }

    /// <summary>The voices a <see cref="IsVoicebankCredit">voice-only credit</see> names, without
    /// their version or tone ("Gumi English" → "gumi"), for looking them up in a catalog. Empty
    /// when the credit isn't voice-only.</summary>
    public static IReadOnlyList<string> Voices(string? artist) =>
        IsVoicebankCredit(artist) ? [.. Separator.Split(artist!).Select(Key).Where(k => k.Length > 0).Distinct()] : [];

    // Lower-case letters and digits with single spaces, qualifiers and brackets dropped.
    private static string Key(string name)
    {
        var s = Regex.Replace(name, @"[\(\)\[\]（）【】]", " ");
        s = Qualifier.Replace(s, " ");
        s = new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return TrailingQualifier.Replace(s, "").Trim();
    }
}

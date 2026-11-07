using System.Text.Json;
using HorizonRadio.Core.Metadata.VocaDb;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// A title-only VocaDB search often returns one song many times: its original plus the covers,
/// remixes, and PVs VocaDB links to it. That's one song, the original; same-titled songs that
/// don't share an original stay ambiguous.
/// </summary>
public class VocaDbFamilyTests
{
    private const string Original = """{ "id": 1389, "name": "エレクトリック・ラブ", "artistString": "八王子P feat. 初音ミク" }""";

    private static string Version(int id, string artist, int original) =>
        $$"""{ "id": {{id}}, "name": "エレクトリック・ラブ", "artistString": "{{artist}}", "originalVersionId": {{original}} }""";

    private static JsonElement Results(params string[] songs) =>
        JsonDocument.Parse($$"""{ "items": [ {{string.Join(",", songs)}} ] }""").RootElement;

    [Fact]
    public void A_title_shared_by_one_songs_versions_is_that_original()
    {
        // A station credited its uploader, so only the title can be searched: many artists, one song.
        var json = Results(Version(11499, "わかむらP, 八王子P feat. 初音ミク", 1389), Original,
            Version(235536, "SilentlyFreezing feat. 筆音クミ", 1389), Version(68402, "TAKA feat. 初音ミク", 1389));

        var m = VocaDbProvider.SelectMatch(json, "エレクトリック・ラブ", null, artistConfirmed: false, sink: null, out var fetch);

        Assert.Equal(1389, m?.Id);
        Assert.Equal("八王子P feat. 初音ミク", m?.Artist);
        Assert.Null(fetch);
    }

    [Fact]
    public void An_original_missing_from_the_results_is_fetched()
    {
        var json = Results(Version(11499, "わかむらP, 八王子P feat. 初音ミク", 1389), Version(235536, "SilentlyFreezing", 1389));

        var m = VocaDbProvider.SelectMatch(json, "エレクトリック・ラブ", null, artistConfirmed: false, sink: null, out var fetch);

        Assert.Null(m);
        Assert.Equal(1389, fetch);
    }

    [Fact]
    public void Same_titled_different_songs_stay_ambiguous()
    {
        var json = Results(Original, Version(235536, "SilentlyFreezing", 1389), Version(777, "Someone Else", 555));

        var m = VocaDbProvider.SelectMatch(json, "エレクトリック・ラブ", null, artistConfirmed: false, sink: null, out var fetch);

        Assert.Null(m);
        Assert.Null(fetch);
    }
}

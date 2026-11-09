using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>How a mix entry shows in a list: what it is, by whom, how big, and its cover.</summary>
/// <param name="Title">A track's title, a playlist's/album's/folder's/station's name.</param>
/// <param name="Subtitle">A track's artist, a playlist's owner, an album's artist, a folder's
/// location, a station's country and genre; null when there's nothing to say.</param>
/// <param name="Kind">What the entry is.</param>
/// <param name="ItemCount">How many tracks the entry plays, when known.</param>
/// <param name="Art">Cover image bytes, when there is one.</param>
public sealed record MixEntryInfo(
    string Title,
    string? Subtitle,
    ContentKind Kind,
    int? ItemCount,
    byte[]? Art);

/// <summary>
/// Turns a mix entry (a source + a locator) into what the Mixes tab shows for it, so a list of
/// links reads as the songs and playlists they are. A collection its source can name (a Spotify
/// playlist, a YouTube playlist, a folder, a station) is described by the source
/// (<see cref="IContentDescriber"/>); anything else is enumerated, and a single track is resolved
/// the way the queue resolves its rows: the source's own metadata, then the metadata catalogs for
/// the canonical title, artist, and cover. Descriptions are kept in a <see cref="MixEntryInfoStore"/>
/// so a mix shows at once next time, without a yt-dlp run or a Web API call per entry.
/// </summary>
public sealed class MixEntryDescriber(
    MixContentResolver content,
    MetadataResolver? metadata = null,
    MixEntryInfoStore? store = null)
{
    /// <summary>How long a kept description is shown without looking it up again: playlists
    /// grow and catalogs get better, but not by the minute.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    /// <summary>The kept description of <paramref name="entry"/>, if there is one, and whether
    /// it's old enough to look up again.</summary>
    public (MixEntryInfo Info, bool Stale)? Cached(ContentRef entry) =>
        store?.Get(entry) is { } kept ? (kept.Info, DateTimeOffset.UtcNow - kept.Updated > RefreshAfter) : null;

    /// <summary>
    /// Look <paramref name="entry"/> up and keep the answer. Throws
    /// <see cref="InvalidOperationException"/> with a user-facing message when the entry can't be
    /// opened (an unknown source, a missing tool, a link that doesn't resolve).
    /// </summary>
    public async Task<MixEntryInfo> DescribeAsync(ContentRef entry, CancellationToken ct)
    {
        var info = await LookUpAsync(entry, ct).ConfigureAwait(false);
        store?.Put(entry, info);
        return info;
    }

    /// <summary>Keep what's already known about an entry (a song added from where it was showing,
    /// with its title and cover), so a mix shows it at once instead of looking it up.</summary>
    public void Remember(ContentRef entry, MixEntryInfo info) => store?.Put(entry, info);

    /// <summary>Forget what's kept for any entry not in <paramref name="entries"/> (every
    /// entry of every mix), so removed entries don't linger on disk.</summary>
    public void KeepOnly(IEnumerable<ContentRef> entries) => store?.Prune(entries);

    private async Task<MixEntryInfo> LookUpAsync(ContentRef entry, CancellationToken ct)
    {
        var player = content.ResolvePlayer(entry);

        if (player is IContentDescriber describer &&
            await describer.DescribeAsync(entry, ct).ConfigureAwait(false) is { } summary)
        {
            var art = summary.Art;
            if (art is null && summary.ArtUrl is { Length: > 0 } url)
                art = await ImageDownload.TryGetAsync(ImageDownload.Shared, url, ct).ConfigureAwait(false);
            return new MixEntryInfo(summary.Title, Blank(summary.Subtitle), summary.Kind, summary.ItemCount, art);
        }

        var items = await player.EnumerateAsync(entry, ct).ConfigureAwait(false);
        if (items.Count == 0) throw new InvalidOperationException("There's nothing to play there.");

        if (items.Count > 1)
        {
            // A collection its source can't name: say how big it is, with the first track's cover.
            var first = await items[0].TryGetMetadataAsync(ct).ConfigureAwait(false) ?? items[0].Metadata;
            var title = Blank(entry.DisplayName) ?? Blank(first.Album) ?? "Playlist";
            return new MixEntryInfo(title, Blank(first.Artist), ContentKind.Playlist, items.Count,
                first.AlbumArt ?? first.FallbackArt);
        }

        var item = items[0];
        var seed = await item.TryGetMetadataAsync(ct).ConfigureAwait(false) ?? item.Metadata;
        var track = metadata is { HasContributors: true }
            ? await metadata.ResolveAsync(seed, ct, ResolvePriority.Background).ConfigureAwait(false)
            : seed;
        return new MixEntryInfo(
            Blank(track.Title) ?? Blank(entry.DisplayName) ?? entry.Locator,
            Blank(track.Artist),
            ContentKind.Track,
            1,
            track.AlbumArt ?? track.FallbackArt ?? seed.AlbumArt ?? seed.FallbackArt);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

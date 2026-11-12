
namespace HorizonRadio.Sdk.Sources;

/// <summary>What a <see cref="ContentRef"/> points at, as a list row shows it.</summary>
public enum ContentKind
{
    Track,
    Playlist,
    Album,
    Folder,
    Station,
}

/// <summary>
/// How to show a <see cref="ContentRef"/> in a list (a mix entry): a title, a subtitle (the
/// artist, a playlist's owner, a folder's location), what kind of thing it is, how many tracks it
/// holds, and its cover, as an <see cref="ArtUrl"/> to fetch or the <see cref="Art"/> itself.
/// </summary>
public sealed record ContentSummary(
    string Title,
    string? Subtitle,
    ContentKind Kind,
    int? ItemCount = null,
    string? ArtUrl = null,
    byte[]? Art = null);

/// <summary>
/// Optional on an <see cref="IContentPlayer"/>: describes a collection (a playlist, an album, a
/// folder, a station) as a whole, with the name only its source knows, without expanding it.
/// Return <see langword="null"/> for anything better shown as its one track: the host then
/// enumerates it and resolves the track's metadata the way the queue does.
/// </summary>
public interface IContentDescriber
{
    /// <summary>Describe <paramref name="content"/>, or <see langword="null"/> when it's a single
    /// track. Throws <see cref="System.InvalidOperationException"/> with a user-facing message when
    /// the locator doesn't resolve, like <see cref="IContentPlayer.EnumerateAsync"/>.</summary>
    Task<ContentSummary?> DescribeAsync(ContentRef content, CancellationToken ct);
}

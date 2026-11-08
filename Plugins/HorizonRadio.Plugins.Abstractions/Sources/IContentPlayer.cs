namespace HorizonRadio.Core.Sources;

/// <summary>
/// A content-addressable playback engine. Configured once from environment +
/// behavior (tool paths, normalization) and holds no content of its own;
/// <see cref="EnumerateAsync"/> turns a <see cref="ContentRef"/> into the
/// <see cref="PlayableItem"/>s the queue plays.
///
/// This is the reusable unit the queue and mixes drive: the engine is built once
/// per source kind, then asked repeatedly for each locator's items. Self-driven
/// sources (Spotify Connect, the test tone) have no ref to open and so are not
/// content players — that boundary is why they can't be queued or appear in mixes.
/// </summary>
public interface IContentPlayer
{
    /// <summary>
    /// Expand <paramref name="content"/> into its ordered <see cref="PlayableItem"/>s
    /// — a folder/M3U into its files, a YouTube playlist URL into its videos, a
    /// single file/video into one item. This is the unit the queue sequences;
    /// each item resolves and pumps itself independently. Throws
    /// <see cref="System.InvalidOperationException"/> with a user-facing message
    /// when the locator is empty or doesn't resolve.
    /// </summary>
    Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct);
}

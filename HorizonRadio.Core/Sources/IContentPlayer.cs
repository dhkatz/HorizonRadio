namespace HorizonRadio.Core.Sources;

/// <summary>
/// A content-addressable playback engine. Configured once from environment +
/// behavior (tool paths, normalization) and holds no content of its own;
/// <see cref="Open"/> turns a <see cref="ContentRef"/> into a runnable
/// <see cref="IAudioSource"/>.
///
/// This is the reusable unit a mix drives: the engine is built once per source
/// kind, then opened repeatedly for each entry. Self-driven sources (Spotify
/// Connect, the test tone) have no ref to open and so are not content players —
/// that boundary is why they can't appear as mix entries.
/// </summary>
public interface IContentPlayer
{
    /// <summary>
    /// Open a playable source for <paramref name="content"/>. Throws
    /// <see cref="System.InvalidOperationException"/> with a user-facing message
    /// when the locator is empty or doesn't resolve — the same contract the
    /// old <c>IAudioSourceFactory.Create</c> had, so the failure text the UI
    /// surfaces is unchanged.
    /// </summary>
    IAudioSource Open(ContentRef content);

    /// <summary>
    /// Expand <paramref name="content"/> into its ordered <see cref="PlayableItem"/>s
    /// — a folder/M3U into its files, a YouTube playlist URL into its videos, a
    /// single file/video into one item. This is the unit the mix engine sequences;
    /// each item resolves and pumps itself independently. Throws
    /// <see cref="System.InvalidOperationException"/> on an empty/unresolvable
    /// locator, matching <see cref="Open"/>.
    /// </summary>
    Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct);
}

using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Marks a source factory as content-addressable: it can build a content-free
/// <see cref="IContentPlayer"/> from environment/behavior config and names the
/// one schema field that carries the content locator.
///
/// Self-driven sources (Spotify Connect, the test tone) implement only
/// <see cref="IAudioSourceFactory"/>, never this — they have no locator to give.
/// Code that needs "can this source be a mix entry?" tests
/// <c>factory is IContentSourceFactory</c>; that single check is the
/// content-addressable vs. self-driven discriminator.
///
/// <see cref="IAudioSourceFactory.Create"/> remains the single-start entry point
/// and, for these factories, is implemented in terms of the split: read the
/// content field, build a one-off <see cref="ContentRef"/>, and
/// <see cref="IContentPlayer.Open"/> it. The mix engine instead calls
/// <see cref="CreatePlayer"/> once and opens many refs against it.
/// </summary>
public interface IContentSourceFactory : IAudioSourceFactory
{
    /// <summary>
    /// Schema key whose value is the content locator (e.g. the URL or folder).
    /// The single source of truth for "which field is content" — distinct from
    /// the environment fields (tool paths) and behavior fields the player reads.
    /// </summary>
    string ContentKey { get; }

    /// <summary>
    /// Build the content-free engine from environment + behavior values (tool
    /// paths, normalization). Ignores the <see cref="ContentKey"/> field. Throws
    /// <see cref="System.InvalidOperationException"/> if a required environment
    /// value (a tool path) is missing, matching the old Create behavior.
    /// </summary>
    IContentPlayer CreatePlayer(ConfigValues values);
}

using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Sdk.Sources;

/// <summary>
/// Marks a source factory as content-addressable: it can build a content-free
/// <see cref="IContentPlayer"/> from environment/behavior config and names the
/// one schema field that carries the content locator.
///
/// Self-driven sources (Spotify Connect, the test tone) implement
/// <see cref="ISelfDrivenSourceFactory"/>, never this — they have no locator to give.
/// Code that needs "can this source be queued or be a mix entry?" tests
/// <c>factory is IContentSourceFactory</c>; that single check is the
/// content-addressable vs. self-driven discriminator.
///
/// A content source is never started on its own: every way of playing one (the
/// Sources tab, quick play, search, history, mixes) goes through the queue, which
/// calls <see cref="CreatePlayer"/> and enumerates the locator's items.
/// </summary>
public interface IContentSourceFactory : IAudioSourceFactory
{
    /// <summary>
    /// Schema key whose value is the content locator (e.g. the URL or folder).
    /// The single source of truth for "which field is content" — distinct from
    /// the environment fields (tool paths) and behavior fields the player reads.
    /// </summary>
    string ContentKey { get; }

    /// <summary>Placeholder/example for a content locator (a URL, folder, …),
    /// shown in quick-play and mix-entry inputs. Lives here so the hint has one
    /// home rather than being duplicated per input surface.</summary>
    string LocatorHint { get; }

    /// <summary>
    /// Whether <paramref name="locator"/> is one of this source's own (a link to its service, a
    /// path it plays), so a pasted locator can pick its source by itself. Only claim what's
    /// unambiguous: a generic <c>https://</c> URL that several sources could play is no one's.
    /// </summary>
    bool Recognizes(string locator) => false;

    /// <summary>
    /// Build the content-free engine from environment + behavior values (tool
    /// paths, normalization). Ignores the <see cref="ContentKey"/> field. Throws
    /// <see cref="System.InvalidOperationException"/> if a required environment
    /// value (a tool path) is missing.
    /// </summary>
    IContentPlayer CreatePlayer(ConfigValues values);
}

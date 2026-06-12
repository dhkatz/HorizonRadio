using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Helpers for one-off "quick play" — playing a transient locator (URL / folder
/// / file) through a content source without saving a mix.
/// </summary>
public static class ContentQuickPlay
{
    /// <summary>
    /// Layer a transient quick-play <paramref name="locator"/> onto a content
    /// source's start config — trimmed, under the source's
    /// <see cref="IContentSourceFactory.ContentKey"/>. The one place the ad-hoc
    /// "what to play" value is attached, so the Sources-tab box and the
    /// player-bar quick-play dialog can't drift on trimming/keying.
    /// </summary>
    public static ConfigValues WithLocator(this ConfigValues values, IContentSourceFactory factory, string locator)
    {
        values.Set(factory.ContentKey, locator.Trim());
        return values;
    }
}

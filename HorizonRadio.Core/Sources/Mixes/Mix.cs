using System.Collections.Generic;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// A saved cross-source playlist: an ordered list of <see cref="ContentRef"/>
/// entries played back-to-back through one continuous stream. Each entry can be
/// a single track or a collection (a YouTube playlist URL, a local folder/M3U)
/// that expands to many items when played — so "nesting" is just an entry whose
/// player enumerates to more than one item.
///
/// A mix supersedes the old single-source "profile": a one-entry mix is exactly
/// what a content-addressable profile used to be. Self-driven sources (Spotify
/// Connect, the test tone) aren't mixable — they have no <see cref="ContentRef"/>.
/// </summary>
public sealed record Mix(
    string Id,
    string Name,
    IReadOnlyList<ContentRef> Entries,
    string? Station = null)
{
    /// <summary>
    /// Which in-game station this mix replaces: its own override if set,
    /// otherwise the app-wide default. Null means "replace whatever's active"
    /// (the same meaning the global default carries).
    /// </summary>
    public string? EffectiveStation(string? globalDefault) => Station ?? globalDefault;
}

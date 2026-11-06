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
///
/// A mix is just music: which station (if any) plays it is a game's setting (FH6's Stations page),
/// not part of the mix.
/// </summary>
public sealed record Mix(
    string Id,
    string Name,
    IReadOnlyList<ContentRef> Entries);

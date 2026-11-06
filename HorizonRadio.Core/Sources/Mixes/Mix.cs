using System.Collections.Generic;
using System.Linq;

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
/// <param name="Games">Per-game settings for this mix (e.g. FH6's station override), keyed by game
/// plugin id. The host stores them but never interprets them; the game plugin edits and applies them
/// (see <c>IGameMixSettings</c>).</param>
public sealed record Mix(
    string Id,
    string Name,
    IReadOnlyList<ContentRef> Entries,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Games = null)
{
    /// <summary>This mix's settings for the game plugin <paramref name="gameId"/>, or
    /// <see langword="null"/> if it has none.</summary>
    public IReadOnlyDictionary<string, string>? SettingsFor(string gameId) =>
        Games != null && Games.TryGetValue(gameId, out var settings) ? settings : null;

    /// <summary>A copy with <paramref name="gameId"/>'s settings replaced (removed when
    /// <see langword="null"/> or empty); other games' settings are kept.</summary>
    public Mix WithSettingsFor(string gameId, IReadOnlyDictionary<string, string>? settings)
    {
        var games = (Games ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
            .Where(g => g.Key != gameId)
            .ToDictionary(g => g.Key, g => g.Value, StringComparer.Ordinal);
        if (settings is { Count: > 0 })
            games[gameId] = settings;
        return this with { Games = games.Count > 0 ? games : null };
    }
}

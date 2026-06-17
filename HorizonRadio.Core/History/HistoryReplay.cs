using System;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.Spotify;
using HorizonRadio.Core.Sources.YouTube;

namespace HorizonRadio.Core.History;

/// <summary>
/// Turns a played <see cref="Track"/>'s stable id back into a <em>song-level</em> replay handle:
/// a queueable source id + a <see cref="Sources.ContentRef"/>-ready locator the existing
/// enqueue path can resolve. The mapping is per-source because each source namespaces its id
/// differently and a few don't round-trip at all:
///
///   • Spotify  — the <c>spotify:track:…</c> uri is itself the locator; it replays through the
///     driven "spotify-driven" factory whether it originally played there or via the zero-config
///     receiver (whose id "spotify" isn't queueable).
///   • YouTube  — <c>youtube:&lt;id&gt;</c> rebuilds the watch URL.
///   • Local    — <c>local:&lt;path&gt;</c> is the file path (the local player accepts one file).
///   • Radio    — a live stream's song isn't re-addressable; returns null so replay re-searches.
/// </summary>
public static class HistoryReplay
{
    private const string YouTubeIdPrefix = "youtube:";
    private const string SpotifyUriPrefix = "spotify:";

    /// <summary>Derive the (queueable source id, locator) that replays this song, or
    /// (null, null) when the origin can't be re-addressed from its id alone.</summary>
    public static (string? SourceId, string? Locator) DeriveOrigin(string? externalId)
    {
        if (string.IsNullOrEmpty(externalId)) return (null, null);

        if (externalId.StartsWith(SpotifyUriPrefix, StringComparison.Ordinal))
            return (SpotifyContentSourceFactory.SourceId, externalId);

        if (externalId.StartsWith(YouTubeIdPrefix, StringComparison.Ordinal))
        {
            var id = externalId[YouTubeIdPrefix.Length..];
            return string.IsNullOrEmpty(id)
                ? (null, null)
                : (YouTubeSourceFactory.SourceId, $"https://www.youtube.com/watch?v={id}");
        }

        if (externalId.StartsWith(LocalPlayableItem.ExternalIdPrefix, StringComparison.Ordinal))
        {
            var path = externalId[LocalPlayableItem.ExternalIdPrefix.Length..];
            return string.IsNullOrEmpty(path) ? (null, null) : ("local", path);
        }

        return (null, null);
    }
}

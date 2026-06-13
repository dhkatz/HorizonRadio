using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using SpotifyAPI.Web;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Content-free Spotify engine: resolves a Spotify locator (track/playlist/album,
/// URI or web link) into <see cref="SpotifyPlayableItem"/>s via the Web API, and
/// opens a sequential <see cref="IAudioSource"/> for the direct Sources-tab play.
/// Playback itself runs through the shared <see cref="SpotifyPlaybackService"/>;
/// this only does the catalog lookups (which need the authed Web API client).
/// </summary>
public sealed class SpotifyContentPlayer(SpotifyConnection connection, SpotifyPlaybackService playback) : IContentPlayer
{
    public IAudioSource Open(ContentRef content)
    {
        if (!SpotifyLinks.TryParse(content.Locator, out _))
            throw new InvalidOperationException(InvalidLocatorMessage);
        return new SpotifyContentSource(content, this);
    }

    public async Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct)
    {
        if (!SpotifyLinks.TryParse(content.Locator, out var link))
            throw new InvalidOperationException(InvalidLocatorMessage);

        var client = await connection.GetClientAsync(ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException(
                         "Spotify isn't connected. Connect your account in the Sources tab.");

        var kind = link.Kind.ToString().ToLowerInvariant();
        try
        {
            var infos = link.Kind switch
            {
                SpotifyLinkKind.Track => await TrackInfosAsync(client, link.Id, ct).ConfigureAwait(false),
                SpotifyLinkKind.Playlist => await PlaylistInfosAsync(client, link.Id, ct).ConfigureAwait(false),
                SpotifyLinkKind.Album => await AlbumInfosAsync(client, link.Id, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException(InvalidLocatorMessage),
            };

            return [.. infos.Select(i => (PlayableItem)new SpotifyPlayableItem(i, playback))];
        }
        catch (APIException ex)
        {
            var status = (int?)ex.Response?.StatusCode;
            // Log the full reason (Spotify's bare messages aren't shown in the toast).
            Log($"{kind} {link.Id} failed: HTTP {status} — {ex.Message} | body: {ex.Response?.Body}");

            throw new InvalidOperationException(status switch
            {
                403 => $"Spotify denied access to that {kind} (403). In Development Mode the Web API can't " +
                       "read some playlists — especially Spotify's own editorial/algorithmic ones (Discover " +
                       "Weekly, Daily Mix, Radio, …). Try a track, an album, or a playlist you created.",
                404 => $"Spotify couldn't find that {kind} (404) — check the link, or it may be private.",
                429 => "Spotify is rate-limiting requests (429). Wait a moment and try again.",
                _ => $"Spotify couldn't open that {kind}: {ex.Message}",
            }, ex);
        }
    }

    private const string InvalidLocatorMessage =
        "Spotify: paste a track, playlist, or album link (spotify:… or open.spotify.com/…).";

    private static void Log(string msg)
    {
        Debug.WriteLine($"[hzn-spotify-content] {msg}");
        Diagnostics.ProcessConsole.Append("spotify", msg);
    }

    private static async Task<IReadOnlyList<SpotifyTrackInfo>> TrackInfosAsync(
        SpotifyClient client, string id, CancellationToken ct)
    {
        var track = await client.Tracks.Get(id, ct).ConfigureAwait(false);
        return [FromFullTrack(track)];
    }

    private static async Task<IReadOnlyList<SpotifyTrackInfo>> PlaylistInfosAsync(
        SpotifyClient client, string id, CancellationToken ct)
    {
        // GetPlaylistItems hits the post-March-2026 /playlists/{id}/items endpoint;
        // the old GetItems (/playlists/{id}/tracks) was removed by Spotify and 403s for
        // Development-Mode apps (it's [Obsolete] in SpotifyAPI.Web 7.4+).
        var first = await client.Playlists.GetPlaylistItems(id, ct).ConfigureAwait(false);
        var all = await client.PaginateAll(first, cancellationToken: ct).ConfigureAwait(false);

        var infos = new List<SpotifyTrackInfo>(all.Count);
        foreach (var pt in all)
        {
            // The new /items endpoint fills PlaylistTrack.Item ("item"); the old /tracks
            // one filled .Track ("track"). 7.4.2 exposes both, so prefer Item and fall
            // back to Track. A playlist can hold episodes and local tracks; keep only
            // streamable catalog tracks (a local track has no playable Spotify URI).
            if ((pt.Item ?? pt.Track) is FullTrack { IsLocal: false } ft && !string.IsNullOrEmpty(ft.Uri))
                infos.Add(FromFullTrack(ft));
        }
        return infos;
    }

    private static async Task<IReadOnlyList<SpotifyTrackInfo>> AlbumInfosAsync(
        SpotifyClient client, string id, CancellationToken ct)
    {
        var album = await client.Albums.Get(id, ct).ConfigureAwait(false);
        var art = BestImage(album.Images);
        var year = ParseYear(album.ReleaseDate);

        var all = await client.PaginateAll(album.Tracks, cancellationToken: ct).ConfigureAwait(false);
        return [.. all.Select(t => FromSimpleTrack(t, album.Name, art, year))];
    }

    private static SpotifyTrackInfo FromFullTrack(FullTrack t) => new(
        Uri: t.Uri,
        Title: t.Name,
        Artist: JoinArtists(t.Artists),
        Album: t.Album?.Name,
        ArtUrl: BestImage(t.Album?.Images),
        Duration: TimeSpan.FromMilliseconds(t.DurationMs),
        Year: ParseYear(t.Album?.ReleaseDate));

    private static SpotifyTrackInfo FromSimpleTrack(SimpleTrack t, string? albumName, string? art, int? year) => new(
        Uri: t.Uri,
        Title: t.Name,
        Artist: JoinArtists(t.Artists),
        Album: albumName,
        ArtUrl: art,
        Duration: TimeSpan.FromMilliseconds(t.DurationMs),
        Year: year);

    private static string JoinArtists(IEnumerable<SimpleArtist>? artists) =>
        artists is null ? "" : string.Join(", ", artists.Select(a => a.Name));

    // Spotify returns images widest-first; the first is the highest-res square cover.
    private static string? BestImage(IEnumerable<Image>? images) =>
        images?.FirstOrDefault()?.Url;

    private static int? ParseYear(string? releaseDate)
    {
        if (releaseDate is { Length: >= 4 } &&
            int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            return y;
        return null;
    }
}

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>What a Spotify locator points at.</summary>
public enum SpotifyLinkKind
{
    Track,
    Playlist,
    Album,
}

/// <summary>A parsed Spotify locator: its kind and bare base62 id.</summary>
public readonly record struct SpotifyLink(SpotifyLinkKind Kind, string Id)
{
    /// <summary>The canonical <c>spotify:&lt;kind&gt;:&lt;id&gt;</c> URI.</summary>
    public string Uri => $"spotify:{Kind.ToString().ToLowerInvariant()}:{Id}";
}

/// <summary>
/// Parses the Spotify locators a user can paste into Quick Play or a mix entry —
/// both the <c>spotify:track:…</c> URI form and the <c>open.spotify.com/track/…</c>
/// web form (including the localized <c>/intl-xx/</c> path and the <c>?si=</c>
/// share query) — into a <see cref="SpotifyLink"/>. Tracks, playlists, and albums
/// only; anything else (artist, show, episode, garbage) fails to parse.
/// </summary>
public static class SpotifyLinks
{
    public static bool TryParse(string? input, out SpotifyLink link)
    {
        link = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = input.Trim();

        // URI form: spotify:track:ID (ignore any trailing ?query).
        if (s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = StripQuery(s).Split(':');
            // parts[0]="spotify"; the kind/id are the last two segments so a
            // user:…:playlist:ID style URI still resolves.
            if (parts.Length >= 3 && TryKind(parts[^2], out var kind))
                return Build(kind, parts[^1], out link);
            return false;
        }

        // Web form: https://open.spotify.com[/intl-xx]/track/ID?si=…
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 1 < segs.Length; i++)
            {
                if (TryKind(segs[i], out var kind))
                    return Build(kind, segs[i + 1], out link);
            }
        }

        return false;
    }

    private static bool Build(SpotifyLinkKind kind, string rawId, out SpotifyLink link)
    {
        link = default;
        var id = StripQuery(rawId);
        if (id.Length == 0) return false;
        link = new SpotifyLink(kind, id);
        return true;
    }

    private static string StripQuery(string s)
    {
        var q = s.IndexOf('?');
        return q >= 0 ? s[..q] : s;
    }

    private static bool TryKind(string s, out SpotifyLinkKind kind)
    {
        switch (s.ToLowerInvariant())
        {
            case "track": kind = SpotifyLinkKind.Track; return true;
            case "playlist": kind = SpotifyLinkKind.Playlist; return true;
            case "album": kind = SpotifyLinkKind.Album; return true;
            default: kind = default; return false;
        }
    }
}

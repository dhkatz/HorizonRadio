namespace HorizonRadio.Source.YouTube;

/// <summary>Recognises YouTube links among pasted locators, and which of them are playlists.</summary>
public static class YouTubeLinks
{
    private static readonly string[] Hosts = ["youtube.com", "youtu.be", "youtube-nocookie.com"];

    /// <summary>Whether <paramref name="locator"/> is a link to YouTube (or YouTube Music).</summary>
    public static bool IsYouTube(string? locator) => TryParse(locator) is not null;

    /// <summary>
    /// Whether <paramref name="locator"/> expands to more than one video: a playlist (a
    /// <c>list=</c> parameter, which yt-dlp follows even on a watch link) or a channel's uploads.
    /// A plain video link isn't.
    /// </summary>
    public static bool IsCollection(string? locator)
    {
        if (TryParse(locator) is not { } uri) return false;
        if (HasQueryKey(uri.Query, "list")) return true;
        var path = uri.AbsolutePath;
        return path.StartsWith("/playlist", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/@", StringComparison.Ordinal)
            || path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/c/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/user/", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? TryParse(string? locator)
    {
        if (string.IsNullOrWhiteSpace(locator)) return null;
        var s = locator.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        var host = uri.Host;
        return Hosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)
                              || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;
    }

    private static bool HasQueryKey(string query, string key) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => p.Split('=', 2)[0].Equals(key, StringComparison.OrdinalIgnoreCase)
                      && p.Split('=', 2) is [_, { Length: > 0 }]);
}

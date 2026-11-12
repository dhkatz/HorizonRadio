using HorizonRadio.Sdk.Audio;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Source.Local;

/// <summary>
/// Content-free local-files engine: expands a <see cref="ContentRef"/> locator — a
/// folder (recursive), an M3U, or a single file — into one
/// <see cref="LocalPlayableItem"/> per file, via <see cref="Playlist.ResolvePaths"/>.
/// Has no environment/behavior of its own today; it exists so local playback plugs
/// into the same <see cref="IContentPlayer"/> seam as YouTube.
/// </summary>
public sealed class LocalContentPlayer : IContentPlayer, IContentDescriber
{
    // Cover images a folder of music commonly carries, in the order they're preferred.
    private static readonly string[] CoverNames = ["cover", "folder", "front", "album", "albumart"];
    private static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct)
    {
        var path = content.Locator;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Local Files: pick a music folder first.");

        if (!Directory.Exists(path) && !File.Exists(path))
            throw new InvalidOperationException($"Local Files: path doesn't exist: {path}");

        var paths = Playlist.ResolvePaths(path);
        if (paths.Count == 0)
            throw new InvalidOperationException($"Local Files: no audio files found under {path}");

        IReadOnlyList<PlayableItem> items = [.. paths.Select(p => (PlayableItem)new LocalPlayableItem(p))];
        return Task.FromResult(items);
    }

    /// <summary>A folder or an M3U: its name, where it is, how many files it plays, and the
    /// folder's cover image when it has one. A single file is left to the host (null), which
    /// reads its tags.</summary>
    public Task<ContentSummary?> DescribeAsync(ContentRef content, CancellationToken ct)
    {
        var path = content.Locator?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Local Files: pick a music folder first.");

        ContentSummary? summary;
        if (Directory.Exists(path))
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var name = Path.GetFileName(full);
            summary = new ContentSummary(
                Title: string.IsNullOrEmpty(name) ? full : name,
                Subtitle: Path.GetDirectoryName(full),
                Kind: ContentKind.Folder,
                ItemCount: Playlist.ResolvePaths(full).Count,
                Art: ReadCover(full));
        }
        else if (File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".m3u" or ".m3u8")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            summary = new ContentSummary(
                Title: Path.GetFileNameWithoutExtension(path),
                Subtitle: dir,
                Kind: ContentKind.Playlist,
                ItemCount: Playlist.ResolvePaths(path).Count,
                Art: dir is null ? null : ReadCover(dir));
        }
        else if (File.Exists(path))
        {
            summary = null;
        }
        else
        {
            throw new InvalidOperationException($"Local Files: path doesn't exist: {path}");
        }
        return Task.FromResult(summary);
    }

    private static byte[]? ReadCover(string dir)
    {
        try
        {
            foreach (var name in CoverNames)
                foreach (var ext in CoverExtensions)
                {
                    var file = Path.Combine(dir, name + ext);
                    if (File.Exists(file)) return File.ReadAllBytes(file);
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}

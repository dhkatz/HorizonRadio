using HorizonRadio.Core.Audio;

namespace HorizonRadio.Core.Sources.Local;

/// <summary>
/// Content-free local-files engine: expands a <see cref="ContentRef"/> locator — a
/// folder (recursive), an M3U, or a single file — into one
/// <see cref="LocalPlayableItem"/> per file, via <see cref="Playlist.ResolvePaths"/>.
/// Has no environment/behavior of its own today; it exists so local playback plugs
/// into the same <see cref="IContentPlayer"/> seam as YouTube.
/// </summary>
public sealed class LocalContentPlayer : IContentPlayer
{
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
}

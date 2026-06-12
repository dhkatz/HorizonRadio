using HorizonRadio.Core.Audio;

namespace HorizonRadio.Core.Sources.Local;

/// <summary>
/// Content-free local-files engine: opens a <see cref="LocalFileSource"/> over
/// whatever <see cref="Playlist.FromPath"/> resolves for a
/// <see cref="ContentRef"/> locator — a folder (recursive), an M3U, or a single
/// file. Has no environment/behavior of its own today; it exists so local
/// playback plugs into the same <see cref="IContentPlayer"/> seam as YouTube.
/// </summary>
public sealed class LocalContentPlayer : IContentPlayer
{
    public IAudioSource Open(ContentRef content)
    {
        var path = content.Locator;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Local Files: pick a music folder first.");

        if (!Directory.Exists(path) && !File.Exists(path))
            throw new InvalidOperationException($"Local Files: path doesn't exist: {path}");

        var playlist = Playlist.FromPath(path);
        if (playlist.Count == 0)
            throw new InvalidOperationException($"Local Files: no audio files found under {path}");

        return new LocalFileSource(playlist);
    }
}

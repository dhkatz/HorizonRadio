using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Local;

/// <summary>
/// Factory for <see cref="LocalFileSource"/>. Exposes one field —
/// the directory or file to load — so the UI auto-renders a path
/// picker. Future fields (shuffle, recursive, extension filter) just
/// extend the schema; the form picks them up automatically.
/// </summary>
public sealed class LocalFileSourceFactory : IAudioSourceFactory
{
    public const string KeyPath = "path";

    public string Id => "local";
    public string DisplayName => "Local Files";
    public string? Description => "Play through a folder of local audio files (mp3, wav, flac, ogg).";

    public IReadOnlyList<ConfigField> Schema { get; } =
    [
        new DirectoryField(
            Key: KeyPath,
            Label: "Music folder",
            Description: "Folder to scan recursively for audio files. An M3U file is also accepted.")
    ];

    public IAudioSource Create(ConfigValues values)
    {
        var path = values.GetString(KeyPath);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Local Files: pick a music folder first.");

        // Playlist.FromPath handles directory (recursive walk), M3U,
        // and single file. Returns an empty playlist for unknown paths;
        // we treat that as user error rather than silently starting.
        if (!Directory.Exists(path) && !File.Exists(path))
            throw new InvalidOperationException($"Local Files: path doesn't exist: {path}");

        var playlist = Playlist.FromPath(path);
        if (playlist.Count == 0)
            throw new InvalidOperationException($"Local Files: no audio files found under {path}");

        return new LocalFileSource(playlist);
    }
}

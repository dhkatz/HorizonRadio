using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Local;

/// <summary>
/// Factory for <see cref="LocalFileSource"/>. Exposes one field —
/// the directory or file to load — so the UI auto-renders a path
/// picker. Future fields (shuffle, recursive, extension filter) just
/// extend the schema; the form picks them up automatically.
/// </summary>
public sealed class LocalFileSourceFactory : IContentSourceFactory
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

    /// <summary>The folder/file path is the content locator; the local engine
    /// has no environment or behavior config of its own.</summary>
    public string ContentKey => KeyPath;

    public string LocatorHint => @"Folder, M3U, or file (e.g. C:\Music)";

    public IContentPlayer CreatePlayer(ConfigValues values) => new LocalContentPlayer();

    // Single-start path: the local engine carries no config, so this just opens
    // the one path the form holds. Path validation (exists, non-empty, has audio)
    // lives in LocalContentPlayer.Open so the mix engine gets it per entry too.
    public IAudioSource Create(ConfigValues values)
        => CreatePlayer(values).Open(new ContentRef(Id, values.GetString(ContentKey) ?? ""));
}

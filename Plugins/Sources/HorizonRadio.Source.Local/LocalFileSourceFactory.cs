using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Sources.Local;

/// <summary>
/// Factory for the local-files source. Its one field — the directory, M3U, or
/// file to play — is the content locator; <see cref="LocalContentPlayer"/>
/// expands it into <see cref="LocalPlayableItem"/>s for the queue.
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

    // A full path (C:\…, \\server\…), whether or not it exists: a mistyped one should say the
    // path doesn't exist, not go to another source. A relative one could be anything.
    public bool Recognizes(string locator) => Path.IsPathFullyQualified(locator.Trim().Trim('"'));

    public IContentPlayer CreatePlayer(ConfigValues values) => new LocalContentPlayer();
}

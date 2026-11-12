namespace HorizonRadio.Sdk;

/// <summary>
/// Finds the external tools plugins run (<c>ToolKind</c> ids: "yt-dlp", "ffmpeg"), whether the user
/// pointed at one, installed it from the Tools tab, or it ships next to the app.
/// </summary>
public interface IToolLocator
{
    /// <summary>The tool to run: <paramref name="configuredPath"/> when it's set and exists, else the
    /// copy installed from the Tools tab or shipped with the app. Null when there's none (the caller
    /// says how to install it).</summary>
    string? Find(string kind, string? configuredPath = null);

    /// <summary>Where the Tools tab installs <paramref name="kind"/>, whether or not it's there.</summary>
    string InstallPath(string kind);
}

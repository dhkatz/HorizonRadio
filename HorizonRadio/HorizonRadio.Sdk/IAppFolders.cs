namespace HorizonRadio.Sdk;

/// <summary>
/// Where the app keeps its files. Everything a plugin saves (logins, caches, state) goes under
/// <see cref="Data"/>, which follows the profile the app was started with (<c>--profile</c>), so two
/// instances on one machine keep apart. Tools are found through <see cref="IToolLocator"/>, not here:
/// downloaded tools are shared between profiles.
/// </summary>
public interface IAppFolders
{
    /// <summary>The profile this instance runs as, or null for the default one.</summary>
    string? Profile { get; }

    /// <summary>A path under the app's data folder, from its parts (<c>Data("spotify", "auth.json")</c>).
    /// The folder isn't created.</summary>
    string Data(params string[] parts);

    /// <summary>A name shown outside the app (a window title, a Spotify Connect device), marked with
    /// the profile when there is one: "Horizon Radio [test]".</summary>
    string Label(string name);
}

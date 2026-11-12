using System.IO;
using HorizonRadio.Sdk;

namespace HorizonRadio.Core.IO;

/// <summary>
/// The host's <see cref="IAppFolders"/>: everything an instance keeps, under one folder. The app runs
/// on <see cref="ForProfile"/> (<c>%LOCALAPPDATA%\HorizonRadio</c>, or the profile's folder under it,
/// <see cref="AppPaths"/>); a test runs the same engine on a temporary folder.
/// </summary>
public sealed class AppFolders(string root, string? profile = null) : IAppFolders
{
    /// <summary>The folder everything goes under.</summary>
    public string Root { get; } = root;

    public string? Profile { get; } = profile;

    public string Data(params string[] parts) => Path.Combine([Root, .. parts]);

    public string Label(string name) => Profile == null ? name : $"{name} [{Profile}]";

    /// <summary>The folders of the profile this instance was started as (<see cref="AppPaths.UseProfile"/>).</summary>
    public static AppFolders ForProfile() => new(AppPaths.Root, AppPaths.Profile);
}

using System;
using System.Collections.Generic;
using System.IO;

namespace HorizonRadio.Core.IO;

/// <summary>
/// Where the app keeps its files: one root, <c>%LOCALAPPDATA%\HorizonRadio</c>, for every store,
/// cache, login and log. Started with <c>--profile &lt;name&gt;</c>, the app keeps them in that
/// profile's folder instead (<c>HorizonRadio\profiles\&lt;name&gt;</c>), so a second instance with its
/// own settings, history and logins can run beside the first (two "users" on one machine, for
/// testing multiplayer). Downloaded tools are shared by every profile (<see cref="SharedRoot"/>).
/// </summary>
public static class AppPaths
{
    public const string ProfileArgument = "--profile";

    /// <summary>The longest profile name: it's a folder name and part of the Spotify device names.</summary>
    public const int MaxProfileLength = 32;

    /// <summary>The folder every profile shares: <c>%LOCALAPPDATA%\HorizonRadio</c>.</summary>
    public static string SharedRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HorizonRadio");

    /// <summary>The profile this instance runs as, or null for the default one.</summary>
    public static string? Profile { get; private set; }

    /// <summary>This instance's folder: <see cref="SharedRoot"/>, or the profile's folder under it.</summary>
    public static string Root => RootFor(Profile);

    /// <summary>A file or folder in this instance's <see cref="Root"/>.</summary>
    public static string In(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Run as <paramref name="profile"/> (null: the default one). Called once at startup,
    /// before anything reads a path.</summary>
    public static void UseProfile(string? profile)
    {
        if (profile != null && !IsValidProfileName(profile))
            throw new ArgumentException($"'{profile}' isn't a valid profile name.", nameof(profile));
        Profile = profile;
    }

    public static string RootFor(string? profile) =>
        profile == null ? SharedRoot : Path.Combine(SharedRoot, "profiles", profile);

    /// <summary>A name shown to others (a Spotify Connect device) marked with the profile, so two
    /// instances' devices can be told apart: "Horizon Radio" → "Horizon Radio [bob]".</summary>
    public static string Label(string name) => Profile == null ? name : $"{name} [{Profile}]";

    /// <summary>Letters, digits, <c>-</c> and <c>_</c>, and not a name Windows reserves for a device
    /// (<c>con</c>, <c>nul</c>, <c>com1</c>…): a valid folder name that can't climb out of the
    /// profiles folder.</summary>
    public static bool IsValidProfileName(string name)
    {
        if (name.Length is 0 or > MaxProfileLength) return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        }
        return !IsReservedDeviceName(name);
    }

    private static bool IsReservedDeviceName(string name) =>
        name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        || (name.Length == 4 && name.ToUpperInvariant() is ['C', 'O', 'M', >= '1' and <= '9'] or ['L', 'P', 'T', >= '1' and <= '9']);

    /// <summary>
    /// The profile named on the command line (<c>--profile bob</c> or <c>--profile=bob</c>), or null
    /// when there's none. Returns false, with the reason, for a missing or invalid name.
    /// </summary>
    public static bool TryParseProfile(IReadOnlyList<string> args, out string? profile, out string? error)
    {
        profile = null;
        error = null;
        for (var i = 0; i < args.Count; i++)
        {
            string? value;
            if (args[i] == ProfileArgument)
            {
                value = i + 1 < args.Count ? args[++i] : null;
            }
            else if (args[i].StartsWith(ProfileArgument + "=", StringComparison.Ordinal))
            {
                value = args[i][(ProfileArgument.Length + 1)..];
            }
            else
            {
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                error = $"{ProfileArgument} needs a name, e.g. {ProfileArgument} test.";
                return false;
            }
            if (!IsValidProfileName(value))
            {
                error = $"'{value}' isn't a valid profile name: use up to {MaxProfileLength} letters, digits, '-' or '_'.";
                return false;
            }
            profile = value;
        }
        return true;
    }
}

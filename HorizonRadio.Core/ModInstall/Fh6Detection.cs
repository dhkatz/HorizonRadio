using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HorizonRadio.Core.ModInstall;

/// <summary>
/// Tries to find a Forza Horizon 6 installation on disk so the user
/// doesn't have to browse manually. v1 covers Steam — the registry
/// gives us Steam's primary install, and steamapps/libraryfolders.vdf
/// enumerates the extra library locations the user added (e.g. a games
/// SSD on a different drive).
///
/// Xbox / Game Pass installs land in WindowsApps with a hashed folder
/// name and are write-protected unless the user opts the package into
/// "Modifiable" — out of scope for v1, we'd need a separate path.
/// </summary>
public static class Fh6Detection
{
    /// <summary>Folder names Steam uses for the game. The actual name
    /// depends on how the developer set it up in Steamworks; including
    /// both common shapes covers the obvious bases.</summary>
    private static readonly string[] FolderCandidates = new[]
    {
        "ForzaHorizon6",
        "Forza Horizon 6",
    };

    public static IReadOnlyList<DetectedInstall> Detect()
    {
        var hits = new List<DetectedInstall>();
        try { TryDetectSteam(hits); }
        catch (System.Exception ex) { Debug.WriteLine($"[hzn-detect] steam: {ex.Message}"); }
        return hits;
    }

    [SupportedOSPlatform("windows")]
    private static void TryDetectSteam(List<DetectedInstall> hits)
    {
        foreach (var lib in EnumerateSteamLibraries())
        {
            foreach (var name in FolderCandidates)
            {
                var path = Path.Combine(lib, "steamapps", "common", name);
                if (Directory.Exists(path))
                    hits.Add(new DetectedInstall("Steam", path));
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        // Steam's install path. WOW6432Node because Steam is 32-bit.
        string? steamRoot = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            steamRoot = key?.GetValue("InstallPath") as string;
        }
        catch { /* registry inaccessible; fall back to common paths */ }

        if (string.IsNullOrEmpty(steamRoot))
        {
            // Common manual Steam locations as a fallback.
            foreach (var p in new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"D:\Steam",
                @"E:\Steam",
            })
            {
                if (Directory.Exists(p)) { steamRoot = p; break; }
            }
        }

        if (string.IsNullOrEmpty(steamRoot)) yield break;
        yield return steamRoot;

        // libraryfolders.vdf lists every secondary library the user
        // added. Format (KeyValue VDF):
        //   "libraryfolders"
        //   {
        //     "0" { "path" "C:\\Program Files (x86)\\Steam" ... }
        //     "1" { "path" "E:\\Games\\Steam" ... }
        //   }
        // We don't bother with a real VDF parser; the only field we
        // need is "path", which is always on its own line.
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
        {
            // VDF escapes backslashes as `\\` — un-escape before use.
            var raw  = m.Groups[1].Value.Replace(@"\\", @"\");
            if (!string.Equals(raw, steamRoot, System.StringComparison.OrdinalIgnoreCase))
                yield return raw;
        }
    }
}

/// <summary>Where the detector found a Forza Horizon 6 install.
/// Source explains the provenance ("Steam", future "Xbox", etc.) and
/// is shown in the picker so the user can disambiguate when they have
/// multiple copies.</summary>
public sealed record DetectedInstall(string Source, string Path);

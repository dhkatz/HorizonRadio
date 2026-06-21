using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace HorizonRadio.Core.ModInstall;

/// <summary>
/// Installs / uninstalls the version.dll proxy mod into a Forza
/// Horizon 6 install directory. "Installing" means dropping our
/// bundled version.dll next to the game's exe; Windows's DLL search
/// order then loads our copy before the real system version.dll, and
/// the game gets the mod.
///
/// Status detection is hash-based: compare the SHA-256 of the bundled
/// DLL (next to HorizonRadio.UI.exe) against any version.dll already
/// in the game folder. This catches every realistic case — a stale
/// install from a previous build shows up as "Different version
/// installed" so the user knows to update, and an unrelated version.dll
/// (another mod, a vanilla copy from a misbehaved tool) doesn't get
/// blindly overwritten without a backup.
/// </summary>
public sealed class ModInstaller
{
    /// <summary>Path to the version.dll bundled with the UI exe (the
    /// csproj copies it from the C++ build output on every build).</summary>
    public string BundledDllPath { get; }

    /// <summary>SHA-256 of the bundled DLL, or empty when not present
    /// (fresh checkout without a C++ build). Computed once at
    /// construction since the file doesn't change at runtime.</summary>
    public string BundledDllHash { get; }

    public bool HasBundledDll => !string.IsNullOrEmpty(BundledDllHash);

    /// <summary>Dev-only: the C++ build output (<c>build/Release/version.dll</c>)
    /// found by walking up from the app dir, or null when not running from a
    /// source tree (e.g. a published install).</summary>
    public string? BuildOutputDllPath { get; }

    /// <summary>Dev-only: true when a C++ build output exists and differs from
    /// the bundled DLL — i.e. the DLL was rebuilt but the app wasn't, so the
    /// bundle (and any install made from it) is stale. The csproj re-copies the
    /// DLL on build, so the fix is to rebuild the UI. Always false for a
    /// published install (no build output beside it).</summary>
    public bool BundledDllStale { get; }

    public ModInstaller()
    {
        BundledDllPath = Path.Combine(AppContext.BaseDirectory, "version.dll");
        BundledDllHash = File.Exists(BundledDllPath) ? ComputeHash(BundledDllPath) : "";

        BuildOutputDllPath = FindBuildOutputDll(AppContext.BaseDirectory);
        if (HasBundledDll && BuildOutputDllPath != null)
        {
            try { BundledDllStale = ComputeHash(BuildOutputDllPath) != BundledDllHash; }
            catch (Exception ex) { Debug.WriteLine($"[hzn-install] build-output hash failed: {ex.Message}"); }
        }
    }

    private const string TargetDllName = "version.dll";

    /// <summary>Walk up from <paramref name="startDir"/> looking for the C++
    /// build output at <c>build/Release/version.dll</c>. Returns null outside a
    /// source tree. The depth cap covers bin/{Config}/{tfm} back to the repo root.</summary>
    private static string? FindBuildOutputDll(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        for (var i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "build", "Release", TargetDllName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    private const string BackupSuffix = ".horizon-backup";

    /// <summary>Inspect a candidate FH6 directory and report what's
    /// there. Doesn't modify anything.</summary>
    public InstallationStatus Check(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            return InstallationStatus.PathInvalid;

        var target = Path.Combine(gamePath, TargetDllName);
        if (!File.Exists(target)) return InstallationStatus.NotInstalled;

        string hash;
        try { hash = ComputeHash(target); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-install] hash failed: {ex.Message}");
            return InstallationStatus.PathInvalid;
        }

        if (HasBundledDll && hash == BundledDllHash) return InstallationStatus.InstalledMatch;
        return InstallationStatus.InstalledOther;
    }

    /// <summary>Copy the bundled DLL into <paramref name="gamePath"/>.
    /// If an unrelated version.dll already exists there, it's renamed
    /// to version.dll.horizon-backup so Uninstall can restore it. A
    /// pre-existing backup is left alone — the first non-ours DLL we
    /// ever saw is the one worth preserving.</summary>
    public InstallResult Install(string gamePath)
    {
        if (!HasBundledDll)
            return InstallResult.Fail("No bundled version.dll found next to the UI. Build the C++ side first (HorizonRadio.slnx).");
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            return InstallResult.Fail("Game folder doesn't exist or wasn't selected.");

        var target = Path.Combine(gamePath, TargetDllName);
        var backup = Path.Combine(gamePath, TargetDllName + BackupSuffix);

        if (File.Exists(target))
        {
            string existingHash;
            try { existingHash = ComputeHash(target); }
            catch { existingHash = ""; }

            if (existingHash == BundledDllHash)
            {
                return InstallResult.Ok("Already up to date.");
            }

            if (!File.Exists(backup))
            {
                // First time replacing a non-ours DLL — preserve it.
                try { File.Move(target, backup); }
                catch (Exception ex) { return InstallResult.Fail($"Backup failed: {ex.Message}"); }
            }
            else
            {
                // Stale install from a previous build, or a third mod.
                // The original is already preserved as .horizon-backup
                // from the first install; just remove the current.
                try { File.Delete(target); }
                catch (Exception ex) { return InstallResult.Fail($"Remove of stale DLL failed: {ex.Message}"); }
            }
        }

        try { File.Copy(BundledDllPath, target, overwrite: false); }
        catch (UnauthorizedAccessException)
        {
            return InstallResult.Fail("Permission denied writing to the game folder. Close FH6 and try again, or run as administrator.");
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32) // ERROR_SHARING_VIOLATION
        {
            return InstallResult.Fail("Game folder is in use (FH6 may be running). Close FH6 and try again.");
        }
        catch (Exception ex)
        {
            return InstallResult.Fail($"Copy failed: {ex.Message}");
        }

        return InstallResult.Ok("Installed.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as an instance method to match the installer API used by the UI.")]
    public InstallResult Uninstall(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            return InstallResult.Fail("Game folder doesn't exist or wasn't selected.");

        var target = Path.Combine(gamePath, TargetDllName);
        var backup = Path.Combine(gamePath, TargetDllName + BackupSuffix);

        if (!File.Exists(target) && !File.Exists(backup))
            return InstallResult.Ok("Already uninstalled.");

        try
        {
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(backup)) File.Move(backup, target);
            return InstallResult.Ok(File.Exists(target)
                ? "Uninstalled and restored prior version.dll."
                : "Uninstalled.");
        }
        catch (UnauthorizedAccessException)
        {
            return InstallResult.Fail("Permission denied. Close FH6 and try again, or run as administrator.");
        }
        catch (Exception ex)
        {
            return InstallResult.Fail($"Uninstall failed: {ex.Message}");
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public enum InstallationStatus
{
    /// <summary>Selected path doesn't exist or isn't readable.</summary>
    PathInvalid,
    /// <summary>No version.dll in the game folder — vanilla state.</summary>
    NotInstalled,
    /// <summary>version.dll present and SHA-256 matches our bundled
    /// copy — current install, ready to use.</summary>
    InstalledMatch,
    /// <summary>version.dll present but hash differs. Could be a stale
    /// older build of ours, a different mod, or a vanilla Windows
    /// version.dll dropped in by another tool. Install will back it up.</summary>
    InstalledOther,
}

public sealed record InstallResult(bool Success, string Message)
{
    public static InstallResult Ok(string m) => new(true, m);
    public static InstallResult Fail(string m) => new(false, m);
}

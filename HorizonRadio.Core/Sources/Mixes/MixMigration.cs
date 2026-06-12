using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// One-time migration of the pre-mixes <c>profiles.json</c> into mixes. Each
/// content-addressable profile (Local/YouTube) becomes a one-entry mix — its
/// content field is the entry's locator. Self-driven profiles (Spotify Connect,
/// the test tone) are dropped: they were no-op "switch to a receiver" presets
/// and aren't mixable. Reads the legacy file as raw JSON so it doesn't depend on
/// the removed profile types.
/// </summary>
public static class MixMigration
{
    private static string DefaultProfilesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio",
            "profiles.json");

    private static void Log(string msg) => Debug.WriteLine($"[hzn-mix-migrate] {msg}");

    /// <summary>
    /// If <paramref name="store"/> has no mixes yet, populate it from the legacy
    /// profiles file (if present) and persist. No-op once any mix exists, so it
    /// runs at most once. Returns the number of mixes migrated.
    /// </summary>
    public static int MaybeMigrate(MixStore store, string? profilesPath = null)
    {
        if (store.All.Count > 0) return 0;

        var migrated = FromLegacyProfiles(profilesPath ?? DefaultProfilesPath);
        if (migrated.Count == 0) return 0;

        foreach (var mix in migrated) store.AddOrUpdate(mix);
        store.SaveToDisk();
        Log($"migrated {migrated.Count} profile(s) to mixes");
        return migrated.Count;
    }

    public static IReadOnlyList<Mix> FromLegacyProfiles(string profilesPath)
    {
        var mixes = new List<Mix>();
        if (!File.Exists(profilesPath)) return mixes;

        try
        {
            using var stream = File.OpenRead(profilesPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("profiles", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return mixes;

            foreach (var p in arr.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;

                var id = ReadString(p, "id");
                var name = ReadString(p, "name");
                var source = ReadString(p, "source");
                if (id is null || name is null || source is null) continue;

                // Only content-addressable sources carry a playable locator; a
                // self-driven or unknown source can't become a mix entry.
                if (SourceCatalog.Find(source) is not IContentSourceFactory factory) continue;

                string? locator = null;
                if (p.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
                    content.TryGetProperty(factory.ContentKey, out var loc) && loc.ValueKind == JsonValueKind.String)
                    locator = loc.GetString();

                if (string.IsNullOrWhiteSpace(locator)) continue;

                mixes.Add(new Mix(id, name, [new ContentRef(source, locator!)]));
            }
        }
        catch (Exception ex)
        {
            Log($"migration read failed: {ex.Message}");
        }

        return mixes;
    }

    private static string? ReadString(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

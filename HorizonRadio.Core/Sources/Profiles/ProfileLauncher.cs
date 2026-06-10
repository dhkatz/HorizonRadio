using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Profiles;

/// <summary>
/// Turns a <see cref="SourceProfile"/> into a runnable (factory, values) pair.
/// The effective config is the global per-source config (tool paths + schema
/// defaults from <see cref="SourceConfigStore"/>) with the profile's content
/// overlaid on top — so content comes from the profile, environment from global.
/// </summary>
public static class ProfileLauncher
{
    /// <summary>A field a profile captures: per-preset content, i.e. everything
    /// that isn't environment config. Tool paths and other environment fields
    /// (see <see cref="ConfigField.IsEnvironment"/>) stay global. Shared by the
    /// profile editor (which fields to show) and anyone snapshotting content.</summary>
    public static bool IsContentField(ConfigField field) =>
        field is not ToolField && !field.IsEnvironment;

    /// <summary>True for an environment field that the global config must supply.</summary>
    private static bool IsEnvironmentField(ConfigField field) =>
        field is ToolField || field.IsEnvironment;

    /// <summary>Resolve a profile to the source factory and the full config to
    /// start it with, or null if the profile's source id is no longer known.</summary>
    public static (IAudioSourceFactory Factory, ConfigValues Values)? Resolve(
        SourceProfile profile, SourceConfigStore configStore)
    {
        var factory = SourceCatalog.Find(profile.SourceId);
        if (factory == null) return null;

        // Start from the global config (environment + schema defaults), then
        // overlay the profile's content. Skip null/empty content values so a
        // blank field falls back to the default instead of clobbering it.
        var values = configStore.Load(profile.SourceId, factory.Schema);
        foreach (var (key, value) in profile.Content)
        {
            if (value is null || (value is string s && s.Length == 0)) continue;
            values.Set(key, value);
        }
        return (factory, values);
    }

    /// <summary>The label of the first environment field (tool path, etc.) that
    /// has no value in <paramref name="values"/>, or null if all are set. Lets
    /// the UI point the user at the global Sources config before a doomed start.</summary>
    public static string? FirstUnsetEnvironmentField(IAudioSourceFactory factory, ConfigValues values)
    {
        foreach (var f in factory.Schema)
            if (IsEnvironmentField(f) && string.IsNullOrWhiteSpace(values.GetString(f.Key)))
                return f.Label;
        return null;
    }
}

using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Sources;

/// <summary>Settings about sources as a whole (each source's own form is in
/// <see cref="Config.SourceConfigStore"/>).</summary>
public static class SourceSettings
{
    /// <summary>The source last open on the Sources tab.</summary>
    public static readonly Setting<string?> LastSelected = new("sources.lastSelected", SettingScope.Device, null);

    /// <summary>Source ids in the order unified search prefers them: a merged result's Play uses the
    /// first source it has (a row's picker overrides it). Empty: catalog order.</summary>
    public static readonly Setting<string[]> SearchPriority = new("search.sourcePriority", SettingScope.Synced, []);
}

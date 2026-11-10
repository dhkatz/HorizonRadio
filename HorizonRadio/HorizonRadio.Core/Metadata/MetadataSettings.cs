using System.Collections.Generic;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Metadata;

/// <summary>Which metadata providers enrich what's playing, and how (the Metadata tab).</summary>
public static class MetadataSettings
{
    /// <summary>The enabled providers, highest priority first (the source is implicit and always
    /// ahead of them, unless a field is forced to a provider). Unset, the keyless defaults.</summary>
    public static readonly Setting<string[]> Order = new("metadata.order", SettingScope.Synced, [.. MetadataCatalog.DefaultEnabledOrder]);

    /// <summary>The providers already offered. A newly shipped default provider is enabled once for
    /// an existing user, then recorded here, so disabling it later sticks.</summary>
    public static readonly Setting<string[]> Introduced = new("metadata.introduced", SettingScope.Synced, []);

    /// <summary>A provider a field always comes from ("Album Art from Spotify"), by field.</summary>
    public static readonly Setting<Dictionary<MetadataField, string>> Forced = new("metadata.forced", SettingScope.Synced, []);

    /// <summary>When the local title-extraction model runs. No effect until a model is installed
    /// from the Tools tab.</summary>
    public static readonly Setting<TitleModelMode> TitleModel = new("metadata.titleModel", SettingScope.Synced, TitleModelMode.Escalate);
}

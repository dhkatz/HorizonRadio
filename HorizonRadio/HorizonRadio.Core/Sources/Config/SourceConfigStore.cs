using System.Collections.Generic;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Sources.Config;

/// <summary>
/// Each source's settings form (its <see cref="ConfigField"/> schema), kept in the
/// <see cref="SettingsStore"/> as one setting per field: <c>source.&lt;source id&gt;.&lt;field key&gt;</c>
/// (<see cref="ConfigFormSettings"/>). A secret field is a <see cref="SettingScope.Secret"/>; the rest
/// are <see cref="SettingScope.Device"/>, since a form is mostly paths and names that belong to this
/// machine.
/// </summary>
public sealed class SourceConfigStore
{
    private const string Prefix = "source.";

    /// <summary>The scope of a field that isn't a secret.</summary>
    public const SettingScope FieldScope = SettingScope.Device;

    private readonly ConfigFormSettings _forms;

    /// <summary>A store kept in memory only (tests, the designer).</summary>
    public SourceConfigStore() : this(SettingsStore.CreateInMemory())
    {
    }

    public SourceConfigStore(SettingsStore settings) => _forms = new ConfigFormSettings(settings, Prefix, FieldScope);

    /// <summary>The settings the forms are kept in, for the app-level settings beside them.</summary>
    public SettingsStore Settings => _forms.Settings;

    /// <summary>The setting a source's field is kept under.</summary>
    public static string KeyFor(string sourceId, string fieldKey) => $"{Prefix}{sourceId}.{fieldKey}";

    /// <inheritdoc cref="ConfigFormSettings.Load"/>
    public ConfigValues Load(string sourceId, IReadOnlyList<ConfigField> schema) => _forms.Load(sourceId, schema);

    /// <inheritdoc cref="ConfigFormSettings.Save"/>
    public void Save(string sourceId, ConfigValues values) => _forms.Save(sourceId, values);
}

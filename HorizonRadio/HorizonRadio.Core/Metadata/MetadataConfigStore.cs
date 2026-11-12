using HorizonRadio.Core.Settings;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// The metadata providers' configuration, kept in the <see cref="SettingsStore"/>: the order and
/// overrides (<see cref="MetadataSettings"/>), and each provider's form, one setting per field
/// (<c>metadata.provider.&lt;provider id&gt;.&lt;field key&gt;</c>, device-only; a credential such as
/// Spotify's client secret is a <see cref="SettingScope.Secret"/>).
/// </summary>
public sealed class MetadataConfigStore
{
    private const string Prefix = "metadata.provider.";

    /// <summary>The scope of a provider's field that isn't a secret.</summary>
    public const SettingScope FieldScope = SettingScope.Device;

    private readonly ConfigFormSettings _forms;

    /// <summary>A store kept in memory only, with no providers (the designer).</summary>
    public MetadataConfigStore() : this(SettingsStore.CreateInMemory(), new MetadataCatalog([]))
    {
    }

    /// <summary>Opens the configuration in <paramref name="settings"/>, enabling any newly shipped
    /// default provider among <paramref name="catalog"/>'s (see <see cref="MetadataSettings.Introduced"/>).</summary>
    public MetadataConfigStore(SettingsStore settings, MetadataCatalog catalog)
    {
        _forms = new ConfigFormSettings(settings, Prefix, FieldScope);
        Catalog = catalog;
        EnableNewDefaultProviders();
    }

    /// <summary>The providers this configuration is for.</summary>
    public MetadataCatalog Catalog { get; }

    public SettingsStore Settings => _forms.Settings;

    /// <summary>The setting a provider's field is kept under.</summary>
    public static string KeyFor(string providerId, string fieldKey) => $"{Prefix}{providerId}.{fieldKey}";

    /// <inheritdoc cref="MetadataSettings.Order"/>
    public IReadOnlyList<string> Order
    {
        get => Settings.Get(MetadataSettings.Order);
        set => Settings.Set(MetadataSettings.Order, [.. value]);
    }

    /// <inheritdoc cref="MetadataSettings.Forced"/>
    public IReadOnlyDictionary<MetadataField, string> Forced
    {
        get => Settings.Get(MetadataSettings.Forced);
        set => Settings.Set(MetadataSettings.Forced, new Dictionary<MetadataField, string>(value));
    }

    /// <inheritdoc cref="MetadataSettings.TitleModel"/>
    public TitleModelMode TitleModelMode => Settings.Get(MetadataSettings.TitleModel);

    /// <inheritdoc cref="ConfigFormSettings.Load"/>
    public ConfigValues Load(string providerId, IReadOnlyList<ConfigField> schema) => _forms.Load(providerId, schema);

    /// <inheritdoc cref="ConfigFormSettings.Save"/>
    public void Save(string providerId, ConfigValues values) => _forms.Save(providerId, values);

    // A default provider shipped since the user last saw the list is added at the end (lowest
    // priority), so their own order is never disturbed; every known provider is then recorded as
    // offered, so disabling one later sticks.
    private void EnableNewDefaultProviders()
    {
        var introduced = Settings.Get(MetadataSettings.Introduced);
        var order = Order;
        var added = MetadataCatalog.DefaultEnabledOrder.Where(id => !introduced.Contains(id) && !order.Contains(id)).ToList();
        if (added.Count > 0) Order = [.. order, .. added];

        var offered = Catalog.All.Select(f => f.Id).Where(id => !introduced.Contains(id)).ToList();
        if (offered.Count > 0) Settings.Set(MetadataSettings.Introduced, [.. introduced, .. offered]);
    }
}

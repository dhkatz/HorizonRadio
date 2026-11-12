namespace HorizonRadio.Sdk;

/// <summary>
/// A plugin's own persisted settings: flat string key/value pairs the host stores for it (e.g. the
/// FH6 plugin's station assignments). Writes are saved by the host shortly after. Keys are the plugin's
/// business; values are opaque to the host.
/// </summary>
public interface IPluginSettings
{
    /// <summary>The stored value, or <see langword="null"/> if unset.</summary>
    string? GetValue(string key);

    /// <summary>Store <paramref name="value"/>, or remove the key when <see langword="null"/>.</summary>
    void SetValue(string key, string? value);

    /// <summary>A key changed: written here, or (a synced key, see
    /// <see cref="IPluginSettingsStore.ForPlugin"/>) on another of the
    /// user's PCs. Raised on any thread.</summary>
    event System.Action<string>? Changed
    {
        add { }
        remove { }
    }
}

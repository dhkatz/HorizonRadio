namespace HorizonRadio.Plugins.Abstractions;

/// <summary>
/// A plugin's own persisted settings: flat string key/value pairs the host stores for it (e.g. the
/// FH6 plugin's default target station). Writes are saved immediately. Keys are the plugin's
/// business; values are opaque to the host.
/// </summary>
public interface IPluginSettings
{
    /// <summary>The stored value, or <see langword="null"/> if unset.</summary>
    string? GetValue(string key);

    /// <summary>Store <paramref name="value"/>, or remove the key when <see langword="null"/>.</summary>
    void SetValue(string key, string? value);
}

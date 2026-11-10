using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Settings;

/// <summary>
/// Settings forms (an owner's <see cref="ConfigField"/> schema: a source, a metadata provider) kept
/// in the <see cref="SettingsStore"/> as one setting per field: <c>&lt;prefix&gt;&lt;owner id&gt;.&lt;field key&gt;</c>.
/// A secret field (<see cref="TextField.IsSecret"/>) is stored as a <see cref="SettingScope.Secret"/>
/// (encrypted on disk); the rest take <see cref="FieldScope"/>.
///
/// Values are JSON primitives (string, bool, number); <see cref="ConfigValues"/>'s typed accessors
/// handle the boxing.
/// </summary>
public sealed class ConfigFormSettings
{
    private readonly string _prefix;

    // Secret fields per owner, from the schemas seen on Load, so Save stores them as secrets even
    // before one has a value.
    private readonly Dictionary<string, HashSet<string>> _secretKeys = new(StringComparer.Ordinal);

    /// <param name="prefix">The keys' start, ending in a dot: <c>source.</c>.</param>
    public ConfigFormSettings(SettingsStore settings, string prefix, SettingScope fieldScope)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (fieldScope == SettingScope.Secret) throw new ArgumentException("Only a secret field is a secret.", nameof(fieldScope));
        Settings = settings;
        _prefix = prefix;
        FieldScope = fieldScope;
    }

    public SettingsStore Settings { get; }

    /// <summary>The scope of a field that isn't a secret.</summary>
    public SettingScope FieldScope { get; }

    /// <summary>The setting an owner's field is kept under.</summary>
    public string KeyFor(string ownerId, string fieldKey) => $"{_prefix}{ownerId}.{fieldKey}";

    /// <summary>All values stored for <paramref name="ownerId"/>, with the schema's defaults for
    /// the rest, so callers always get a usable bag. A secret field stored in the clear (imported
    /// from an older file) is stored as a secret from here on.</summary>
    public ConfigValues Load(string ownerId, IReadOnlyList<ConfigField> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var secrets = schema.OfType<TextField>().Where(f => f.IsSecret).Select(f => f.Key).ToList();
        lock (_secretKeys)
        {
            if (!_secretKeys.TryGetValue(ownerId, out var keys)) _secretKeys[ownerId] = keys = new(StringComparer.Ordinal);
            keys.UnionWith(secrets);
        }

        var values = new ConfigValues();
        var prefix = KeyFor(ownerId, "");
        foreach (var key in Settings.KeysWithPrefix(prefix))
        {
            if (!Settings.TryGetRaw(key, out var raw)) continue;
            var field = key[prefix.Length..];
            if (secrets.Contains(field) && Settings.ScopeOf(key) != SettingScope.Secret && raw.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                Settings.SetRaw(key, raw, SettingScope.Secret);
            values.Set(field, ToObject(raw));
        }
        values.ApplyDefaults(schema);
        return values;
    }

    /// <summary>Replace the stored values for one owner: each value is set, and a field no longer
    /// in <paramref name="values"/> is removed (except a secret this user can't decrypt, which
    /// <see cref="Load"/> leaves out).</summary>
    public void Save(string ownerId, ConfigValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var given = values.AsReadOnly();
        var prefix = KeyFor(ownerId, "");
        foreach (var key in Settings.KeysWithPrefix(prefix))
        {
            // A secret this user can't decrypt never reached the bag; it stays until replaced.
            if (!given.ContainsKey(key[prefix.Length..]) && Settings.TryGetRaw(key, out _)) Settings.Remove(key);
        }
        foreach (var (field, value) in given)
        {
            var key = KeyFor(ownerId, field);
            var secret = IsSecretField(ownerId, field) || Settings.ScopeOf(key) == SettingScope.Secret;
            var json = secret && value is not (string or null)
                ? JsonSerializer.SerializeToElement(Convert.ToString(value, CultureInfo.InvariantCulture))
                : JsonSerializer.SerializeToElement(value);
            Settings.SetRaw(key, json, secret ? SettingScope.Secret : FieldScope);
        }
    }

    private bool IsSecretField(string ownerId, string field)
    {
        lock (_secretKeys) return _secretKeys.TryGetValue(ownerId, out var keys) && keys.Contains(field);
    }

    private static object? ToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? whole : value.GetDouble(),
        JsonValueKind.Null => null,
        _ => value.GetRawText(),
    };
}

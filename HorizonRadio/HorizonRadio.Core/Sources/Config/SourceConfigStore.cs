using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Sources.Config;

/// <summary>
/// Each source's settings form (its <see cref="ConfigField"/> schema), kept in the
/// <see cref="SettingsStore"/> as one setting per field: <c>source.&lt;source id&gt;.&lt;field key&gt;</c>.
/// A secret field (<see cref="TextField.IsSecret"/>) is stored as a <see cref="SettingScope.Secret"/>
/// (encrypted on disk); the rest are <see cref="SettingScope.Device"/>, since a form is mostly paths
/// and names that belong to this machine.
///
/// Values are JSON primitives (string, bool, number); <see cref="ConfigValues"/>'s typed accessors
/// handle the boxing.
/// </summary>
public sealed class SourceConfigStore
{
    private const string Prefix = "source.";

    /// <summary>The scope of a field that isn't a secret.</summary>
    public const SettingScope FieldScope = SettingScope.Device;

    // Secret fields per source, from the schemas seen on Load, so Save stores them as secrets even
    // before one has a value.
    private readonly Dictionary<string, HashSet<string>> _secretKeys = new(StringComparer.Ordinal);

    /// <summary>A store kept in memory only (tests, the designer).</summary>
    public SourceConfigStore() : this(SettingsStore.CreateInMemory())
    {
    }

    public SourceConfigStore(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
    }

    /// <summary>The settings the forms are kept in, for the app-level settings beside them.</summary>
    public SettingsStore Settings { get; }

    /// <summary>The setting a source's field is kept under.</summary>
    public static string KeyFor(string sourceId, string fieldKey) => $"{Prefix}{sourceId}.{fieldKey}";

    /// <summary>All values stored for <paramref name="sourceId"/>, with the schema's defaults for
    /// the rest, so callers always get a usable bag.</summary>
    public ConfigValues Load(string sourceId, IReadOnlyList<ConfigField> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_secretKeys)
        {
            foreach (var secret in schema.OfType<TextField>().Where(f => f.IsSecret))
            {
                if (!_secretKeys.TryGetValue(sourceId, out var keys)) _secretKeys[sourceId] = keys = new(StringComparer.Ordinal);
                keys.Add(secret.Key);
            }
        }

        var values = new ConfigValues();
        var prefix = KeyFor(sourceId, "");
        foreach (var key in Settings.KeysWithPrefix(prefix))
        {
            if (Settings.TryGetRaw(key, out var raw)) values.Set(key[prefix.Length..], ToObject(raw));
        }
        values.ApplyDefaults(schema);
        return values;
    }

    /// <summary>Replace the stored values for one source: each value is set, and a field no longer
    /// in <paramref name="values"/> is removed (except a secret this user can't decrypt, which
    /// <see cref="Load"/> leaves out).</summary>
    public void Save(string sourceId, ConfigValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var given = values.AsReadOnly();
        var prefix = KeyFor(sourceId, "");
        foreach (var key in Settings.KeysWithPrefix(prefix))
        {
            // A secret this user can't decrypt never reached the bag; it stays until replaced.
            if (!given.ContainsKey(key[prefix.Length..]) && Settings.TryGetRaw(key, out _)) Settings.Remove(key);
        }
        foreach (var (field, value) in given)
        {
            var key = KeyFor(sourceId, field);
            var secret = IsSecretField(sourceId, field) || Settings.ScopeOf(key) == SettingScope.Secret;
            var json = secret && value is not (string or null)
                ? JsonSerializer.SerializeToElement(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))
                : JsonSerializer.SerializeToElement(value);
            Settings.SetRaw(key, json, secret ? SettingScope.Secret : FieldScope);
        }
    }

    private bool IsSecretField(string sourceId, string field)
    {
        lock (_secretKeys) return _secretKeys.TryGetValue(sourceId, out var keys) && keys.Contains(field);
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

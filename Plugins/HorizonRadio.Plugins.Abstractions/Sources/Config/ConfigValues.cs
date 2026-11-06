using System.Collections.Generic;

namespace HorizonRadio.Core.Sources.Config;

/// <summary>
/// Typed bag of user-supplied values for a source's configuration,
/// keyed by <see cref="ConfigField.Key"/>. Round-trips through JSON
/// for persistence (see the host's <c>SourceConfigStore</c>), so values
/// should be JSON-friendly primitives: string, bool, double, int.
///
/// Factories read out of this via the typed accessors; missing keys
/// return null/default rather than throwing, so a partially-filled
/// schema still produces a usable source.
/// </summary>
public sealed class ConfigValues
{
    private readonly Dictionary<string, object?> _values;

    public ConfigValues() { _values = new(); }
    public ConfigValues(IDictionary<string, object?> initial) { _values = new(initial); }

    public IReadOnlyDictionary<string, object?> AsReadOnly() => _values;

    public void Set(string key, object? value) => _values[key] = value;

    public string? GetString(string key)
        => _values.TryGetValue(key, out var v) ? v as string : null;

    public bool GetBool(string key, bool fallback = false)
        => _values.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    /// <summary>Read a number, tolerating both int and double in storage
    /// (JSON deserialization may surface either depending on the value).</summary>
    public double GetDouble(string key, double fallback = 0)
    {
        if (!_values.TryGetValue(key, out var v) || v is null) return fallback;
        return v switch
        {
            double d => d,
            int i => i,
            long l => l,
            _ => fallback,
        };
    }

    /// <summary>Seed defaults from a schema for any keys not yet set.
    /// Useful when first opening a source with no persisted config.</summary>
    public void ApplyDefaults(IEnumerable<ConfigField> schema)
    {
        foreach (var f in schema)
        {
            if (!_values.ContainsKey(f.Key) && f.DefaultValue != null)
                _values[f.Key] = f.DefaultValue;
        }
    }
}

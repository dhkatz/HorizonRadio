using System;

namespace HorizonRadio.Core.Settings;

/// <summary>Where a setting belongs, which decides what syncing your devices does with it.</summary>
public enum SettingScope
{
    /// <summary>A preference that follows you to your other machines (lyrics, shuffle, search order).</summary>
    Synced,

    /// <summary>About this machine only (a path, an audio device, the game installed here): never leaves it.</summary>
    Device,

    /// <summary>A credential: encrypted for this Windows user (<see cref="IO.SecretText"/>) and never
    /// leaves the machine.</summary>
    Secret,
}

/// <summary>A setting's key and scope, without its type: what <see cref="SettingsStore"/> works with.</summary>
public abstract class Setting
{
    private protected Setting(string key, SettingScope scope, Type valueType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (scope == SettingScope.Secret && valueType != typeof(string))
            throw new ArgumentException($"A secret setting holds text; '{key}' is {valueType.Name}.", nameof(scope));
        Key = key;
        Scope = scope;
    }

    /// <summary>Dotted and lower camel case, owner first: <c>lyrics.karaoke</c>, <c>plugin.fh6.switchMode</c>.</summary>
    public string Key { get; }

    public SettingScope Scope { get; }

    public override string ToString() => Key;
}

/// <summary>
/// One setting, declared by the part of the app that owns it:
/// <code>public static readonly Setting&lt;bool&gt; Karaoke = new("lyrics.karaoke", SettingScope.Synced, true);</code>
/// Read and written through <see cref="SettingsStore.Get{T}"/> / <see cref="SettingsStore.Set{T}"/>;
/// unset, it reads as <see cref="Default"/>. The value is stored as JSON, so <typeparamref name="T"/>
/// is a type <see cref="System.Text.Json"/> round-trips (a primitive, a string, an array, a record).
/// </summary>
public sealed class Setting<T>(string key, SettingScope scope, T defaultValue) : Setting(key, scope, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T))
{
    public T Default { get; } = defaultValue;
}

/// <summary>A synced setting as it goes between the user's devices: its value (null when it was
/// removed) and when it last changed (<see cref="SettingsStore.SyncedSettings"/>,
/// <see cref="SettingsStore.MergeSynced"/>).</summary>
public sealed record SyncedSetting(string Key, System.Text.Json.JsonElement? Value, DateTimeOffset Modified);

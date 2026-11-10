using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Devices;

/// <summary>One of the user's devices, as every device in the group keeps it. A device removed (or
/// that left) stays as <see cref="Removed"/>, so the others don't add it back from an older list.</summary>
public sealed record DeviceRecord(string PeerId, string Name, DateTimeOffset Modified, bool Removed = false);

/// <summary>Your devices: the group this device belongs to.</summary>
public static class DeviceSettings
{
    /// <summary>The secret the group's devices share (base64, 32 bytes). They find each other through
    /// it, so only they can. Null: not paired.</summary>
    public static readonly Setting<string?> GroupKey = new("devices.groupKey", SettingScope.Secret, null);

    /// <summary>Every device in the group, this one included. The devices keep it in step themselves,
    /// so it isn't a synced setting.</summary>
    public static readonly Setting<DeviceRecord[]> Devices = new("devices.list", SettingScope.Device, []);

    /// <summary>This device's peer ID (from its identity key), kept so it's known before the network starts.</summary>
    public static readonly Setting<string?> Self = new("devices.self", SettingScope.Device, null);

    /// <summary>What this device is called on the others. Null: the computer's name.</summary>
    public static readonly Setting<string?> Name = new("devices.name", SettingScope.Device, null);
}

internal static class DeviceList
{
    /// <summary>Both lists in one: for a device in both, the record changed last (a removal, on a tie).</summary>
    public static DeviceRecord[] Merge(IEnumerable<DeviceRecord> mine, IEnumerable<DeviceRecord> theirs)
    {
        var byId = new Dictionary<string, DeviceRecord>(StringComparer.Ordinal);
        foreach (var record in mine.Concat(theirs))
        {
            if (string.IsNullOrWhiteSpace(record.PeerId)) continue;
            if (!byId.TryGetValue(record.PeerId, out var known) || Later(record, known)) byId[record.PeerId] = record;
        }
        return [.. byId.Values.OrderBy(r => r.PeerId, StringComparer.Ordinal)];
    }

    private static bool Later(DeviceRecord a, DeviceRecord b)
    {
        if (a.Modified != b.Modified) return a.Modified > b.Modified;
        if (a.Removed != b.Removed) return a.Removed;
        return string.CompareOrdinal(a.Name, b.Name) > 0;
    }
}

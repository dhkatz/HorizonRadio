using System.Text.Json;
using System.Text.Json.Serialization;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Devices;

// What the user's devices say to each other, as JSON messages over the peer network:
//
//   pair.hello    joiner → host   { name, proof }                  "I have the code"
//   pair.welcome  host → joiner   { name, proof, groupKey, devices } "so do I; here's the group"
//   pair.refused  host → joiner   {}                               wrong code, or no pairing open
//   sync          either way      { devices?, settings?, reply }   the device list and synced
//                                                                  settings (all, or what changed);
//                                                                  reply asks for the other's back

internal sealed record DeviceMessage
{
    public required string Type { get; init; }
    public string? Name { get; init; }
    public string? Proof { get; init; }
    public string? GroupKey { get; init; }
    public DeviceRecord[]? Devices { get; init; }
    public WireSetting[]? Settings { get; init; }
    public bool Reply { get; init; }

    public const string Hello = "pair.hello";
    public const string Welcome = "pair.welcome";
    public const string Refused = "pair.refused";
    public const string Sync = "sync";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Json);

    public static DeviceMessage? From(JsonElement json)
    {
        try
        {
            return json.Deserialize<DeviceMessage>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A synced setting on the wire. <see cref="Removed"/> rather than a missing value, since a
/// setting's value can itself be JSON null.</summary>
internal sealed record WireSetting(string Key, JsonElement? Value, DateTimeOffset Modified, bool Removed = false)
{
    public static WireSetting From(SyncedSetting s) => new(s.Key, s.Value, s.Modified, s.Value == null);

    public SyncedSetting ToSetting() =>
        new(Key, Removed ? null : Value ?? JsonNull, Modified);

    private static readonly JsonElement JsonNull = JsonDocument.Parse("null").RootElement.Clone();
}

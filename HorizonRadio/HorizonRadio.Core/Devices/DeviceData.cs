using System.Text.Json;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Devices;

/// <summary>
/// Something besides settings that your devices keep in step (<see cref="MyDevices"/>): each device
/// sends all of it when they meet, and what changed when it changes, and merges what the others
/// send, the newest change of each item winning.
/// </summary>
public interface IDeviceData
{
    /// <summary>What it's called in the messages ("mixes").</summary>
    string Name { get; }

    /// <summary>All of it (<paramref name="full"/>, when devices meet) or what may have just changed,
    /// deletions included, as JSON: in parts each small enough for one message (a relayed connection
    /// carries 128 KB in all), each one merged on its own.</summary>
    Task<IReadOnlyList<JsonElement>> ExportAsync(bool full);

    /// <summary>Take in another device's export.</summary>
    Task MergeAsync(JsonElement theirs);

    /// <summary>It changed (here, or by a merge).</summary>
    event Action? Changed;
}

/// <summary>Your mixes, kept in step between your devices.</summary>
public sealed class MixSync : IDeviceData
{
    private readonly MixStore _mixes;

    public MixSync(MixStore mixes)
    {
        _mixes = mixes;
        mixes.Changed += () => Changed?.Invoke();
    }

    public string Name => "mixes";

    public event Action? Changed;

    public Task<IReadOnlyList<JsonElement>> ExportAsync(bool full) =>
        Task.FromResult<IReadOnlyList<JsonElement>>(
            [JsonSerializer.SerializeToElement(_mixes.SyncedMixes().Select(WireMix.From).ToArray(), Json)]);

    public Task MergeAsync(JsonElement theirs)
    {
        WireMix[]? mixes;
        try
        {
            mixes = theirs.Deserialize<WireMix[]>(Json);
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }
        if (mixes == null) return Task.CompletedTask;
        var synced = mixes.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => m.ToSynced()).ToList();
        if (_mixes.MergeSynced(synced)) _mixes.SaveToDisk();
        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record WireEntry(string Source, string Locator, string? Name);

    private sealed record WireMix(string Id, string? Name, WireEntry[]? Entries, DateTimeOffset Modified, bool Removed = false)
    {
        public static WireMix From(SyncedMix m) => m.Mix is { } mix
            ? new WireMix(m.Id, mix.Name, [.. mix.Entries.Select(e => new WireEntry(e.SourceId, e.Locator, e.DisplayName))], m.Modified)
            : new WireMix(m.Id, null, null, m.Modified, Removed: true);

        public SyncedMix ToSynced() => Removed
            ? new SyncedMix(Id, null, Modified)
            : new SyncedMix(Id, new Mix(Id, Name ?? "Mix",
                [.. (Entries ?? []).Where(e => !string.IsNullOrEmpty(e.Source) && !string.IsNullOrEmpty(e.Locator))
                    .Select(e => new ContentRef(e.Source, e.Locator, e.Name))]), Modified);
    }
}

/// <summary>
/// Your play history, kept in step between your devices: songs played anywhere show everywhere,
/// and a song removed (or the history cleared) goes everywhere. Sent whole when devices meet, in
/// parts of <see cref="PartSize"/> songs; after that, the newest songs as they're played.
/// </summary>
public sealed class HistorySync : IDeviceData
{
    /// <summary>Songs per message (about 60 KB).</summary>
    public const int PartSize = 100;

    /// <summary>The newest songs sent on a change: a song played, or enriched.</summary>
    public const int Recent = 10;

    private readonly History.PlayHistoryStore _history;

    public HistorySync(History.PlayHistoryStore history)
    {
        _history = history;
        history.Changed += () => Changed?.Invoke();
    }

    public string Name => "history";

    public event Action? Changed;

    public Task<IReadOnlyList<JsonElement>> ExportAsync(bool full)
    {
        var history = _history.SyncedHistory(full ? null : Recent);
        var parts = new List<JsonElement>();
        // The removals and the clearing go with the first part.
        for (var i = 0; i == 0 || i < history.Entries.Count; i += PartSize)
        {
            parts.Add(JsonSerializer.SerializeToElement(new WireHistory(
                [.. history.Entries.Skip(i).Take(PartSize).Select(History.PlayHistoryStore.ToJson)],
                i == 0 ? history.Removed : null,
                i == 0 ? history.ClearedBefore : null), Json));
        }
        return Task.FromResult<IReadOnlyList<JsonElement>>(parts);
    }

    public Task MergeAsync(JsonElement theirs)
    {
        WireHistory? wire;
        try
        {
            wire = theirs.Deserialize<WireHistory>(Json);
        }
        catch (JsonException)
        {
            return Task.CompletedTask;
        }
        if (wire == null) return Task.CompletedTask;
        _history.MergeSynced(new History.SyncedHistory(
            [.. (wire.Entries ?? []).Select(History.PlayHistoryStore.FromJson).OfType<History.PlayHistoryEntry>()],
            wire.Removed ?? [],
            wire.ClearedBefore));
        return Task.CompletedTask; // the history service saves on Changed
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record WireHistory(JsonElement[]? Entries, IReadOnlyList<string>? Removed, DateTimeOffset? ClearedBefore);
}

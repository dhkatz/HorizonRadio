using System.Text.Json;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Devices;

/// <summary>
/// Something besides settings that your devices keep in step (<see cref="MyDevices"/>): each device
/// sends all of it when they meet and again when it changes, and merges what the others send, the
/// newest change of each item winning.
/// </summary>
public interface IDeviceData
{
    /// <summary>What it's called in the messages ("mixes").</summary>
    string Name { get; }

    /// <summary>All of it, deletions included, as JSON.</summary>
    Task<JsonElement> ExportAsync();

    /// <summary>Take in another device's export.</summary>
    Task MergeAsync(JsonElement theirs);

    /// <summary>It changed (here, or by a merge).</summary>
    event Action? Changed;
}

/// <summary>Your mixes, kept in step between your devices. The store belongs to the UI thread, so
/// it's only touched through <c>onOwnerThread</c>.</summary>
public sealed class MixSync : IDeviceData
{
    private readonly MixStore _mixes;
    private readonly Func<Action, Task> _onOwnerThread;

    public MixSync(MixStore mixes, Func<Action, Task> onOwnerThread)
    {
        _mixes = mixes;
        _onOwnerThread = onOwnerThread;
        mixes.Changed += () => Changed?.Invoke();
    }

    public string Name => "mixes";

    public event Action? Changed;

    public async Task<JsonElement> ExportAsync()
    {
        IReadOnlyList<SyncedMix> all = [];
        await _onOwnerThread(() => all = _mixes.SyncedMixes()).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(all.Select(WireMix.From).ToArray(), Json);
    }

    public async Task MergeAsync(JsonElement theirs)
    {
        WireMix[]? mixes;
        try
        {
            mixes = theirs.Deserialize<WireMix[]>(Json);
        }
        catch (JsonException)
        {
            return;
        }
        if (mixes == null) return;
        var synced = mixes.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => m.ToSynced()).ToList();
        await _onOwnerThread(() =>
        {
            if (_mixes.MergeSynced(synced)) _mixes.SaveToDisk();
        }).ConfigureAwait(false);
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

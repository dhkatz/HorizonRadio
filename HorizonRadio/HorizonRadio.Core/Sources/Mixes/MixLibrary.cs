using System.Linq;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Input;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>What became of adding a song to a mix (<see cref="MixLibrary.Add"/>).</summary>
public enum MixAddResult { Added, AlreadyInMix, NoSuchMix }

/// <summary>
/// Changing the user's mixes, whatever asks (the Mixes page, a song's menu, the CLI): each change
/// is saved, the looked-up entry titles and covers are kept only for entries some mix still has,
/// and the Console says what changed. When a mix goes, by a delete here or one synced from another
/// PC, the key bindings that switched to it go too. Playing a mix is <see cref="MixSwitcher"/>'s.
/// Safe from any thread.
/// </summary>
public sealed class MixLibrary : IDisposable
{
    /// <summary>The Console tag for changes to mixes.</summary>
    public const string ConsoleTag = "mixes";

    private readonly MixStore _store;
    private readonly MixEntryDescriber? _describer;
    private readonly InputBindingStore? _bindings;
    private readonly IPluginLog _log;
    private readonly Lock _lock = new();
    private HashSet<string> _ids;

    /// <param name="describer">Entry titles and covers, trimmed to what the mixes still have.</param>
    /// <param name="bindings">Key bindings, cleared for a mix that's gone.</param>
    public MixLibrary(MixStore store, MixEntryDescriber? describer, InputBindingStore? bindings, IPluginLog log)
    {
        _store = store;
        _describer = describer;
        _bindings = bindings;
        _log = log;
        _ids = [.. store.All.Select(m => m.Id)];
        _store.Changed += OnChanged;
    }

    /// <summary>Whether two entries are the same content (the same source and locator).</summary>
    public static bool SameEntry(ContentRef a, ContentRef b) => a.SourceId == b.SourceId && a.Locator == b.Locator;

    /// <summary>Save a mix: a new one when <paramref name="id"/> is null. Returns its id.</summary>
    /// <exception cref="ArgumentException">No name, or no entries (the message says which, for the user).</exception>
    public string Save(string? id, string name, IReadOnlyList<ContentRef> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the mix a name.");
        if (entries.Count == 0) throw new ArgumentException("Add at least one entry: paste a link, or a folder or file path.");

        id ??= Guid.NewGuid().ToString("n");
        var isNew = _store.Get(id) is null;
        _store.AddOrUpdate(new Mix(id, name.Trim(), [.. entries]));
        _store.SaveToDisk();
        _describer?.KeepOnly(_store.All.SelectMany(m => m.Entries));
        _log.Write(ConsoleTag, $"{(isNew ? "created" : "saved")} {name.Trim()} ({entries.Count} entr{(entries.Count == 1 ? "y" : "ies")})");
        return id;
    }

    /// <summary>Add <paramref name="entry"/> to the end of a saved mix (Add To Mix on a song's
    /// menu), unless it's there already.</summary>
    /// <param name="known">What's already known of the entry (its title and cover), kept so the
    /// mix shows it without looking it up.</param>
    public MixAddResult Add(string mixId, ContentRef entry, MixEntryInfo? known = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_store.Get(mixId) is not { } mix) return MixAddResult.NoSuchMix;
        if (mix.Entries.Any(e => SameEntry(e, entry))) return MixAddResult.AlreadyInMix;

        if (known != null) _describer?.Remember(entry, known);
        _store.AddOrUpdate(mix with { Entries = [.. mix.Entries, entry] });
        _store.SaveToDisk();
        _log.Write(ConsoleTag, $"added {entry.DisplayName ?? known?.Title ?? entry.Locator} to {mix.Name}");
        return MixAddResult.Added;
    }

    /// <summary>Delete a mix (on your other devices too, once they sync). False when there's no
    /// such mix.</summary>
    public bool Delete(string id)
    {
        if (_store.Get(id) is not { } mix) return false;
        _store.Remove(id);
        _store.SaveToDisk();
        _describer?.KeepOnly(_store.All.SelectMany(m => m.Entries));
        _log.Write(ConsoleTag, $"deleted {mix.Name}");
        return true;
    }

    // Raised on the thread that changed the store: here, or a sync from another PC.
    private void OnChanged()
    {
        HashSet<string> gone;
        lock (_lock)
        {
            var now = _store.All.Select(m => m.Id).ToHashSet();
            gone = [.. _ids.Except(now)];
            _ids = now;
        }
        foreach (var id in gone)
        {
            if (_bindings?.ClearBindingsForAction(new EventAction(EventActionType.SwitchMix, id)) == true)
                _log.Write(ConsoleTag, "cleared the key bindings of a deleted mix");
        }
    }

    public void Dispose() => _store.Changed -= OnChanged;
}

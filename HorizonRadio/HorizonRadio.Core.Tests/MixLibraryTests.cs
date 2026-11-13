using HorizonRadio.Core.Events;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Changing mixes through the library, as the Mixes page and a song's menu do: each change is
/// saved and logged, and a mix that goes takes its key bindings with it, however it went.
/// </summary>
public sealed class MixLibraryTests : IDisposable
{
    private static readonly InputBinding Space = new(InputKind.Keyboard, null, 57, "Space", "key.space");
    private static readonly ContentRef Melt = new("local", "C:/Music/melt.flac", "Melt");

    private readonly TempDir _dir = TempDir.Create();
    private readonly SettingsStore _settings = SettingsStore.CreateInMemory();
    private readonly LinesLog _log = new();

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    private string MixesFile => Path.Combine(_dir.Path, "mixes.json");

    [Fact]
    public void A_saved_mix_is_on_disk_and_a_song_is_added_once()
    {
        var store = MixStore.LoadFromDisk(MixesFile);
        using var library = new MixLibrary(store, null, null, _log);

        Assert.Throws<ArgumentException>(() => library.Save(null, " ", [Melt]));
        Assert.Throws<ArgumentException>(() => library.Save(null, "Drive", []));
        var id = library.Save(null, " Drive ", [Melt]);

        var song = new ContentRef("local", "C:/Music/world-is-mine.flac", "World is Mine");
        Assert.Equal(MixAddResult.Added, library.Add(id, song));
        Assert.Equal(MixAddResult.AlreadyInMix, library.Add(id, song with { DisplayName = "another title" }));
        Assert.Equal(MixAddResult.NoSuchMix, library.Add("gone", song));

        var saved = MixStore.LoadFromDisk(MixesFile).Get(id)!;
        Assert.Equal("Drive", saved.Name);
        Assert.Equal([Melt.Locator, song.Locator], saved.Entries.Select(e => e.Locator));
        Assert.Equal(["mixes: created Drive (1 entry)", "mixes: added World is Mine to Drive"], _log.Lines);
    }

    [Fact]
    public void A_deleted_mix_takes_its_key_bindings_with_it()
    {
        var store = MixStore.LoadFromDisk(MixesFile);
        var bindings = new InputBindingStore(_settings);
        using var library = new MixLibrary(store, null, bindings, _log);
        var id = library.Save(null, "Drive", [Melt]);
        var playDrive = new EventAction(EventActionType.SwitchMix, id);
        bindings.Bind(Space, playDrive);

        Assert.True(library.Delete(id));
        Assert.False(library.Delete(id));

        Assert.Null(MixStore.LoadFromDisk(MixesFile).Get(id));
        Assert.Equal(EventAction.None, bindings.Match(Space));
        Assert.Contains("mixes: deleted Drive", _log.Lines);
    }

    [Fact]
    public void A_mix_deleted_on_another_PC_takes_its_key_bindings_too()
    {
        var store = new MixStore();
        var bindings = new InputBindingStore(_settings);
        using var library = new MixLibrary(store, null, bindings, _log);
        var id = library.Save(null, "Drive", [Melt]);
        bindings.Bind(Space, new EventAction(EventActionType.SwitchMix, id));

        // The other PC deleted it after this one saved it.
        store.MergeSynced([new SyncedMix(id, null, DateTimeOffset.UtcNow.AddMinutes(1))]);

        Assert.Null(store.Get(id));
        Assert.Equal(EventAction.None, bindings.Match(Space));
    }
}

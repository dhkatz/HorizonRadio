using System.Text.Json;
using HorizonRadio.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace HorizonRadio.Core.Tests;

/// <summary>Synced settings going between two of the user's devices: the later change wins, a
/// removal included, and settings a device keeps to itself never move.</summary>
public sealed class SettingsMergeTests : IDisposable
{
    private static readonly Setting<bool> Karaoke = new("test.karaoke", SettingScope.Synced, true);
    private static readonly Setting<double> Offset = new("test.offset", SettingScope.Synced, 0.0);
    private static readonly Setting<string?> Folder = new("test.folder", SettingScope.Device, null);

    private readonly TempDir _dir = TempDir.Create();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    private SettingsStore Device() => SettingsStore.CreateInMemory(_clock);

    private void Later() => _clock.Advance(TimeSpan.FromSeconds(1));

    [Fact]
    public void A_removed_synced_setting_is_remembered_with_when_it_went()
    {
        var path = Path.Combine(_dir.Path, SettingsStore.FileName);
        using (var store = SettingsStore.Open(path))
        {
            store.Set(Karaoke, false);
            store.Set(Folder, "C:/Music");
            Assert.True(store.Remove(Karaoke.Key));
            Assert.True(store.Remove(Folder.Key));
            Assert.False(store.Remove(Karaoke.Key)); // already gone

            Assert.True(store.Get(Karaoke));
            Assert.False(store.IsSet(Karaoke.Key));
            Assert.Null(store.ScopeOf(Karaoke.Key));
            Assert.Empty(store.KeysWithPrefix("test."));
            var removed = Assert.Single(store.SyncedSettings());
            Assert.Equal(Karaoke.Key, removed.Key);
            Assert.Null(removed.Value);
        }

        var json = File.ReadAllText(path);
        Assert.Contains("\"removed\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Folder.Key, json, StringComparison.Ordinal); // a device setting just goes

        using var reopened = SettingsStore.Open(path);
        Assert.Null(Assert.Single(reopened.SyncedSettings()).Value);
        reopened.Set(Karaoke, false); // and can be set again
        Assert.False(reopened.Get(Karaoke));
    }

    [Fact]
    public void The_later_change_wins_on_either_side()
    {
        var desktop = Device();
        var laptop = Device();
        desktop.Set(Karaoke, false);
        Later();
        laptop.Set(Karaoke, true);
        laptop.Set(Offset, 1.5);

        // The desktop's older value doesn't replace the laptop's.
        Assert.Empty(laptop.MergeSynced(desktop.SyncedSettings()));
        Assert.True(laptop.Get(Karaoke));

        var changed = desktop.MergeSynced(laptop.SyncedSettings());
        Assert.Equal([Karaoke.Key, Offset.Key], changed.Order());
        Assert.True(desktop.Get(Karaoke));
        Assert.Equal(1.5, desktop.Get(Offset));
        Assert.Equal(laptop.ModifiedAt(Karaoke.Key), desktop.ModifiedAt(Karaoke.Key));
    }

    [Fact]
    public void A_later_removal_wins_over_an_older_value_and_an_older_one_doesnt()
    {
        var desktop = Device();
        var laptop = Device();
        desktop.Set(Offset, 2.0);
        laptop.MergeSynced(desktop.SyncedSettings());
        Later();
        laptop.Reset(Offset);

        Assert.Equal([Offset.Key], desktop.MergeSynced(laptop.SyncedSettings()));
        Assert.Equal(0.0, desktop.Get(Offset));

        // The desktop sets it again later: that beats the removal.
        Later();
        desktop.Set(Offset, 3.0);
        laptop.MergeSynced(desktop.SyncedSettings());
        Assert.Equal(3.0, laptop.Get(Offset));
    }

    [Fact]
    public void A_tie_is_decided_the_same_way_on_both_devices()
    {
        var desktop = Device();
        var laptop = Device();
        desktop.Set(Offset, 1.0);
        laptop.Set(Offset, 2.0); // same instant

        var fromDesktop = desktop.SyncedSettings();
        var fromLaptop = laptop.SyncedSettings();
        desktop.MergeSynced(fromLaptop);
        laptop.MergeSynced(fromDesktop);

        Assert.Equal(desktop.Get(Offset), laptop.Get(Offset));
    }

    [Fact]
    public void Settings_a_device_keeps_to_itself_never_move()
    {
        var desktop = Device();
        desktop.Set(Folder, "D:/Music");
        Assert.Empty(desktop.SyncedSettings());

        // Another device claims the key is synced (a different version, or a forged message).
        Later();
        var claim = new SyncedSetting(Folder.Key, JsonSerializer.SerializeToElement("C:/evil"), _clock.GetUtcNow());
        Assert.Empty(desktop.MergeSynced([claim]));
        Assert.Equal("D:/Music", desktop.Get(Folder));
    }

    [Fact]
    public void Merging_raises_Changed_for_what_changed_and_nothing_else()
    {
        var desktop = Device();
        var laptop = Device();
        laptop.Set(Karaoke, false);
        laptop.Set(Offset, 1.0);
        desktop.MergeSynced(laptop.SyncedSettings());

        var raised = new List<string>();
        desktop.Changed += raised.Add;
        desktop.MergeSynced(laptop.SyncedSettings()); // nothing new
        Assert.Empty(raised);

        Later();
        laptop.Set(Offset, 4.0);
        desktop.MergeSynced(laptop.SyncedSettings());
        Assert.Equal([Offset.Key], raised);
    }

    [Fact]
    public void Change_times_are_kept_to_the_millisecond_so_they_survive_the_file()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567));
        var path = Path.Combine(_dir.Path, SettingsStore.FileName);
        DateTimeOffset? set;
        using (var store = SettingsStore.Open(path, TimeSpan.FromHours(1), clock))
        {
            store.Set(Karaoke, false);
            set = store.ModifiedAt(Karaoke.Key);
        }
        Assert.Equal(0, set!.Value.Ticks % TimeSpan.TicksPerMillisecond);
        using var reopened = SettingsStore.Open(path);
        Assert.Equal(set, reopened.ModifiedAt(Karaoke.Key));
    }
}

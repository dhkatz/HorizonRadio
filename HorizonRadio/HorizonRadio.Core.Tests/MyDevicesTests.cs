using System.Text.Json;
using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk.Peers;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>Pairing devices and syncing their settings, on an in-memory peer network.</summary>
public sealed class MyDevicesTests : IAsyncDisposable
{
    private static readonly Setting<bool> Karaoke = new("test.karaoke", SettingScope.Synced, true);
    private static readonly Setting<double> Offset = new("test.offset", SettingScope.Synced, 0.0);
    private static readonly Setting<string?> Folder = new("test.folder", SettingScope.Device, null);
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    private readonly MemoryPeerNetwork _net = new();
    private readonly List<IAsyncDisposable> _running = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in Enumerable.Reverse(_running)) await r.DisposeAsync();
    }

    private sealed record Device(MyDevices Devices, SettingsStore Settings, Func<MemoryPeer?> Peer, MixStore Mixes);

    // A device: its settings, and a helper that starts when MyDevices first needs it.
    // joinTimeout: how long joining looks for a code. Generous, since stretching the code's key on both
    // sides takes a while on a busy CI runner; short only where a test waits it out.
    private async Task<Device> NewDevice(string name, SettingsStore? settings = null, string? peerId = null, MixStore? mixes = null, TimeSpan? joinTimeout = null)
    {
        settings ??= SettingsStore.CreateInMemory();
        mixes ??= new MixStore();
        var mixSync = new MixSync(mixes, action =>
        {
            lock (mixes) action(); // the tests' stand-in for the UI thread
            return Task.CompletedTask;
        });
        settings.Set(DeviceSettings.Name, name);
        MemoryPeer? peer = null;
        var devices = new MyDevices(settings, _ =>
        {
            peer = _net.Join(peerId ?? settings.Get(DeviceSettings.Self));
            return Task.FromResult<IPeerNetwork>(peer);
        }, data: [mixSync], presenceInterval: TimeSpan.FromMilliseconds(100), sendDelay: TimeSpan.FromMilliseconds(10), joinTimeout: joinTimeout ?? TimeSpan.FromSeconds(20));
        _running.Add(devices);
        await devices.StartAsync();
        return new Device(devices, settings, () => peer, mixes);
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var end = DateTime.UtcNow + Soon;
        while (!condition())
        {
            if (DateTime.UtcNow > end) Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    private static async Task<(Device Host, Device Joiner)> Pair(Device host, Device joiner)
    {
        var code = await host.Devices.StartPairingAsync();
        await joiner.Devices.JoinAsync(code);
        await Eventually(() => host.Devices.Devices.Count == joiner.Devices.Devices.Count && host.Devices.IsPaired, "both to list each other");
        return (host, joiner);
    }

    [Fact]
    public async Task Pairing_puts_both_devices_in_one_group_and_brings_their_settings_together()
    {
        var desktop = await NewDevice("Desktop");
        var laptop = await NewDevice("Laptop");
        desktop.Settings.Set(Karaoke, false);
        desktop.Settings.Set(Folder, "D:/Music");
        await Task.Delay(5);
        laptop.Settings.Set(Offset, 1.5);

        var code = await desktop.Devices.StartPairingAsync();
        Assert.Equal(code, desktop.Devices.Pairing?.Code);
        var joined = await laptop.Devices.JoinAsync(code);
        Assert.Equal("Desktop", joined.Name);

        await Eventually(() => desktop.Devices.IsPaired, "the desktop to list the laptop");
        Assert.Null(desktop.Devices.Pairing); // the code is used up
        Assert.Equal(desktop.Settings.Get(DeviceSettings.GroupKey), laptop.Settings.Get(DeviceSettings.GroupKey));
        Assert.Equal(["Desktop", "Laptop"], desktop.Devices.Devices.Select(d => d.Name).Order());
        Assert.True(desktop.Devices.Devices.Single(d => d.Name == "Desktop").IsThisDevice);
        Assert.Equal(PeerPath.Direct, desktop.Devices.Devices.Single(d => d.Name == "Laptop").Path);

        await Eventually(() => !laptop.Settings.Get(Karaoke) && desktop.Settings.Get(Offset) == 1.5, "the settings to meet");
        Assert.Null(laptop.Settings.Get(Folder)); // a device setting stays put
    }

    [Fact]
    public async Task A_change_on_one_device_reaches_the_other()
    {
        var (desktop, laptop) = await Pair(await NewDevice("Desktop"), await NewDevice("Laptop"));

        desktop.Settings.Set(Offset, -0.5);
        await Eventually(() => laptop.Settings.Get(Offset) == -0.5, "the change to arrive");

        laptop.Settings.Reset(Offset);
        await Eventually(() => desktop.Settings.Get(Offset) == 0.0, "the removal to arrive");
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_uses_the_code_up()
    {
        var desktop = await NewDevice("Desktop");
        var laptop = await NewDevice("Laptop", joinTimeout: TimeSpan.FromSeconds(6)); // waits it out below
        var code = (await desktop.Devices.StartPairingAsync()).ToString();
        // Same meeting place (the first group), wrong secret.
        var wrong = code[..5] + (code[5] == 'A' ? 'B' : 'A') + code[6..];
        Assert.True(PairingCode.TryParse(wrong, out var guess));

        var ex = await Assert.ThrowsAsync<PairingException>(() => laptop.Devices.JoinAsync(guess));
        Assert.Contains("didn't match", ex.Message);
        Assert.Null(desktop.Devices.Pairing);
        Assert.Contains("wrong code", desktop.Devices.Status);
        Assert.False(desktop.Devices.IsPaired);
        Assert.Null(laptop.Settings.Get(DeviceSettings.GroupKey));

        // The right code no longer works either: it was one attempt.
        Assert.True(PairingCode.TryParse(code, out var right));
        await Assert.ThrowsAsync<PairingException>(() => laptop.Devices.JoinAsync(right));
    }

    [Fact]
    public async Task A_device_that_was_offline_catches_up_when_it_comes_back()
    {
        var laptopSettings = SettingsStore.CreateInMemory();
        var (desktop, laptop) = await Pair(await NewDevice("Desktop"), await NewDevice("Laptop", laptopSettings));
        var laptopId = laptop.Peer()!.PeerId;

        // The laptop shuts down; the desktop changes a setting meanwhile.
        await laptop.Devices.DisposeAsync();
        await laptop.Peer()!.DisposeAsync();
        await Eventually(() => desktop.Devices.Devices.Single(d => d.Name == "Laptop").Path == PeerPath.None, "the laptop to go offline");
        desktop.Settings.Set(Karaoke, false);

        // Back up with the same settings and identity: the devices find each other again.
        var back = await NewDevice("Laptop", laptopSettings, laptopId);
        Assert.True(back.Devices.IsPaired);
        await Eventually(() => !laptopSettings.Get(Karaoke), "the laptop to catch up");
    }

    [Fact]
    public async Task A_third_device_added_from_one_finds_the_other()
    {
        var (desktop, laptop) = await Pair(await NewDevice("Desktop"), await NewDevice("Laptop"));
        var (_, tablet) = await Pair(desktop, await NewDevice("Tablet"));

        await Eventually(() => laptop.Devices.Devices.Count == 3, "the laptop to hear of the tablet");
        tablet.Settings.Set(Offset, 2.25);
        await Eventually(() => laptop.Settings.Get(Offset) == 2.25, "the tablet's change to reach the laptop");
    }

    [Fact]
    public async Task A_removed_device_is_told_and_isnt_listened_to_any_more()
    {
        var (desktop, laptop) = await Pair(await NewDevice("Desktop"), await NewDevice("Laptop"));
        var laptopId = laptop.Peer()!.PeerId;

        await desktop.Devices.RemoveAsync(laptopId);
        Assert.False(desktop.Devices.IsPaired);
        await Eventually(() => laptop.Settings.Get(DeviceSettings.GroupKey) == null, "the laptop to leave");
        Assert.Contains("removed", laptop.Devices.Status);

        // What it sends now is ignored.
        laptop.Peer()!.SendUnchecked(desktop.Peer()!, Sync(Offset.Key, 9.0));
        await Task.Delay(200);
        Assert.Equal(0.0, desktop.Settings.Get(Offset));
    }

    [Fact]
    public async Task Only_the_groups_devices_are_listened_to()
    {
        var (desktop, _) = await Pair(await NewDevice("Desktop"), await NewDevice("Laptop"));
        var stranger = _net.Join();

        stranger.SendUnchecked(desktop.Peer()!, Sync(Offset.Key, 7.0));
        await Task.Delay(200);
        Assert.Equal(0.0, desktop.Settings.Get(Offset));
    }

    [Fact]
    public async Task Leaving_tells_the_others_and_keeps_this_devices_settings()
    {
        var (desktop, laptop) = await Pair(await NewDevice("Desktop"), await NewDevice("Laptop"));
        laptop.Settings.Set(Offset, 3.0);
        await Eventually(() => desktop.Settings.Get(Offset) == 3.0, "the change to arrive");

        await laptop.Devices.LeaveAsync();
        Assert.False(laptop.Devices.IsPaired);
        Assert.Equal(3.0, laptop.Settings.Get(Offset));
        await Eventually(() => !desktop.Devices.IsPaired, "the desktop to hear the laptop left");
    }

    [Fact]
    public async Task Mixes_go_both_ways_and_a_deleted_one_goes_everywhere()
    {
        var desktop = await NewDevice("Desktop");
        var laptop = await NewDevice("Laptop");
        var drive = new Mix("drive", "Drive", [new ContentRef("youtube", "https://youtu.be/a", "Synthwave")]);
        lock (desktop.Mixes) desktop.Mixes.AddOrUpdate(drive);
        lock (laptop.Mixes) laptop.Mixes.AddOrUpdate(new Mix("chill", "Chill", []));
        await Pair(desktop, laptop);

        Mix? Find(Device d, string id)
        {
            lock (d.Mixes) return d.Mixes.Get(id);
        }
        await Eventually(() => Find(laptop, "drive") != null && Find(desktop, "chill") != null, "each to get the other's mix");
        Assert.Equal("Synthwave", Find(laptop, "drive")!.Entries.Single().DisplayName);

        // Renamed on the laptop, deleted on the desktop: both arrive.
        lock (laptop.Mixes) laptop.Mixes.AddOrUpdate(Find(laptop, "drive")! with { Name = "Night Drive" });
        await Eventually(() => Find(desktop, "drive")?.Name == "Night Drive", "the rename to arrive");
        lock (desktop.Mixes) desktop.Mixes.Remove("chill");
        await Eventually(() => Find(laptop, "chill") == null, "the deletion to arrive");
    }

    [Fact]
    public void Codes_read_as_typed_and_reject_look_alikes()
    {
        var code = PairingCode.New();
        var text = code.ToString();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", text);
        Assert.True(PairingCode.TryParse(text.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), out var typed));
        Assert.Equal(code, typed);
        Assert.False(PairingCode.TryParse("ABCD-EFGH-IJK0", out _)); // I and 0 aren't in it
        Assert.False(PairingCode.TryParse("ABCD-EFGH", out _));
    }

    private static JsonElement Sync(string key, double value) => JsonSerializer.SerializeToElement(new
    {
        type = "sync",
        settings = new[] { new { key, value, modified = DateTimeOffset.UtcNow.AddYears(1) } },
    });
}

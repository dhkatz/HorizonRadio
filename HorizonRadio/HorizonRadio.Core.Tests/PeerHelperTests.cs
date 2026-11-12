using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HorizonRadio.Core.Peers;
using HorizonRadio.Sdk.Peers;
using HorizonRadio.Tools.Peer;

namespace HorizonRadio.Core.Tests;

/// <summary>The app's side of the peer helper protocol, against a scripted helper.</summary>
public sealed class PeerHelperProtocolTests : IAsyncLifetime, IDisposable
{
    private readonly AnonymousPipeServerStream _toApp = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _toHelper = new(PipeDirection.Out);
    private StreamWriter _helperOut = null!;
    private StreamReader _helperIn = null!;
    private PeerHelper _peer = null!;

    public async Task InitializeAsync()
    {
        _helperOut = new StreamWriter(_toApp, new UTF8Encoding(false)) { AutoFlush = true };
        _helperIn = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toHelper.ClientSafePipeHandle));
        _peer = new PeerHelper(
            new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toApp.ClientSafePipeHandle)),
            new StreamWriter(_toHelper, new UTF8Encoding(false)));
        await Emit("""{"event":"ready","peer_id":"12D3self","addrs":["/ip4/127.0.0.1/tcp/4001"]}""");
        await _peer.WaitReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        _helperOut.Dispose(); // the helper's stdout closes: it's gone
        await _peer.DisposeAsync();
    }

    public void Dispose()
    {
        _helperIn.Dispose();
        _toApp.Dispose();
        _toHelper.Dispose();
    }

    private Task Emit(string line) => _helperOut.WriteLineAsync(line);

    private async Task<JsonElement> NextCommand()
    {
        var line = await _helperIn.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return JsonDocument.Parse(line!).RootElement.Clone();
    }

    [Fact]
    public void Ready_gives_the_peer_id_and_addresses()
    {
        Assert.Equal("12D3self", _peer.PeerId);
        Assert.Equal(["/ip4/127.0.0.1/tcp/4001"], _peer.Addresses);
    }

    [Fact]
    public async Task A_command_completes_with_its_reply_and_fails_with_its_error()
    {
        var send = _peer.SendAsync("12D3other", JsonDocument.Parse("""{"n":1}""").RootElement);
        var cmd = await NextCommand();
        Assert.Equal("send", cmd.GetProperty("cmd").GetString());
        Assert.Equal("12D3other", cmd.GetProperty("peer").GetString());
        Assert.Equal(1, cmd.GetProperty("message").GetProperty("n").GetInt32());
        Assert.False(cmd.TryGetProperty("topic", out _)); // unset fields aren't sent
        await Emit($$"""{"event":"ok","id":"{{cmd.GetProperty("id").GetString()}}"}""");
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        var connect = _peer.ConnectAsync("12D3other", ["/ip4/10.0.0.2/udp/1/quic-v1"]);
        cmd = await NextCommand();
        Assert.Equal("/ip4/10.0.0.2/udp/1/quic-v1", cmd.GetProperty("addrs")[0].GetString());
        await Emit($$"""{"event":"connected","id":"{{cmd.GetProperty("id").GetString()}}","path":"relayed"}""");
        Assert.Equal(PeerPath.Relayed, await connect.WaitAsync(TimeSpan.FromSeconds(5)));

        var refused = _peer.SendAsync("12D3nobody", JsonDocument.Parse("{}").RootElement);
        cmd = await NextCommand();
        await Emit($$"""{"event":"error","id":"{{cmd.GetProperty("id").GetString()}}","error":"not connected to that peer"}""");
        var ex = await Assert.ThrowsAsync<PeerNetworkException>(() => refused.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public async Task A_find_yields_each_peer_and_stopping_early_tells_the_helper()
    {
        var found = new List<FoundPeer>();
        var find = Task.Run(async () =>
        {
            await foreach (var p in _peer.FindAsync("pair/4F7K", TimeSpan.FromMinutes(1)))
            {
                found.Add(p);
                if (found.Count == 2) break;
            }
        });
        var cmd = await NextCommand();
        Assert.Equal("find", cmd.GetProperty("cmd").GetString());
        Assert.Equal(60, cmd.GetProperty("timeout_seconds").GetDouble());
        var id = cmd.GetProperty("id").GetString();
        await Emit($$"""{"event":"found","id":"{{id}}","peer":"12D3a","addrs":["/ip4/1.1.1.1/tcp/1"]}""");
        await Emit($$"""{"event":"found","id":"{{id}}","peer":"12D3b","addrs":[]}""");
        await find.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["12D3a", "12D3b"], found.Select(p => p.PeerId));
        var stop = await NextCommand();
        Assert.Equal("stop", stop.GetProperty("cmd").GetString());
        Assert.Equal(id, stop.GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_find_ends_when_the_helper_is_done()
    {
        var find = Task.Run(async () =>
        {
            var n = 0;
            await foreach (var _ in _peer.FindAsync("x", TimeSpan.FromSeconds(1))) n++;
            return n;
        });
        var id = (await NextCommand()).GetProperty("id").GetString();
        await Emit($$"""{"event":"find_done","id":"{{id}}","found":0}""");
        Assert.Equal(0, await find.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Messages_and_path_changes_are_raised()
    {
        var messages = new BlockingCollection<PeerMessage>();
        var paths = new BlockingCollection<PeerPathChange>();
        _peer.MessageReceived += messages.Add;
        _peer.PathChanged += paths.Add;

        await Emit("""{"event":"message","peer":"12D3a","message":{"type":"hello","n":[1,2]}}""");
        await Emit("""{"event":"peer","peer":"12D3a","path":"direct","conns":[]}""");

        Assert.True(messages.TryTake(out var message, TimeSpan.FromSeconds(5)));
        Assert.Equal("12D3a", message!.PeerId);
        Assert.Equal("hello", message.Message.GetProperty("type").GetString());
        Assert.True(paths.TryTake(out var path, TimeSpan.FromSeconds(5)));
        Assert.Equal(new PeerPathChange("12D3a", PeerPath.Direct), path);
    }

    [Fact]
    public async Task When_the_helper_stops_waiting_commands_fail()
    {
        var exited = new TaskCompletionSource();
        _peer.Exited += () => exited.TrySetResult();
        var send = _peer.SendAsync("12D3a", JsonDocument.Parse("{}").RootElement);
        await NextCommand();

        _helperOut.Dispose();

        await Assert.ThrowsAsync<PeerNetworkException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<PeerNetworkException>(() => _peer.AdvertiseAsync("x"));
    }

    [Fact]
    public void Discovery_maps_to_the_helpers_names()
    {
        Assert.Equal("amino,lan", PeerHelper.DiscoveryArgument(PeerDiscovery.All));
        Assert.Equal("lan", PeerHelper.DiscoveryArgument(PeerDiscovery.Lan));
        Assert.Equal("none", PeerHelper.DiscoveryArgument(PeerDiscovery.None));
    }
}

/// <summary>Two real helpers (the bundled <c>horizon-peer.exe</c>) on this machine.</summary>
public sealed class PeerHelperIntegrationTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    // The helper is built with Go, separately from the C# build; CI builds it first. Locally,
    // without it, these tests don't run.
    private static bool HelperBuilt()
    {
        if (PeerHelper.FindExe() != null) return true;
        Assert.True(Environment.GetEnvironmentVariable("CI") == null, $"{PeerHelper.ExeName} wasn't built before the tests");
        return false;
    }

    private Task<PeerHelper> Start(string name) =>
        PeerHelper.StartAsync(RealProcesses.Instance, Path.Combine(_dir.Path, name + ".key"), PeerDiscovery.None);

    [Fact]
    public async Task Two_helpers_connect_and_message_each_other()
    {
        if (!HelperBuilt()) return;
        await using var a = await Start("a");
        await using var b = await Start("b");
        Assert.NotEqual(a.PeerId, b.PeerId);

        var atB = new BlockingCollection<PeerMessage>();
        var atA = new BlockingCollection<PeerMessage>();
        b.MessageReceived += atB.Add;
        a.MessageReceived += atA.Add;

        Assert.Equal(PeerPath.Direct, await a.ConnectAsync(b.PeerId, b.Addresses));
        for (var i = 0; i < 10; i++)
            await a.SendAsync(b.PeerId, JsonSerializer.SerializeToElement(new { n = i }));

        for (var i = 0; i < 10; i++)
        {
            Assert.True(atB.TryTake(out var m, TimeSpan.FromSeconds(10)), $"message {i} didn't arrive");
            Assert.Equal(a.PeerId, m!.PeerId);
            Assert.Equal(i, m.Message.GetProperty("n").GetInt32());
        }

        await b.SendAsync(a.PeerId, JsonSerializer.SerializeToElement(new { reply = "hi" }));
        Assert.True(atA.TryTake(out var back, TimeSpan.FromSeconds(10)));
        Assert.Equal("hi", back!.Message.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task The_identity_is_kept_across_restarts_and_errors_come_back()
    {
        if (!HelperBuilt()) return;
        string id;
        await using (var first = await Start("same")) id = first.PeerId;
        await using var again = await Start("same");
        Assert.Equal(id, again.PeerId);

        var ex = await Assert.ThrowsAsync<PeerNetworkException>(() =>
            again.SendAsync("12D3KooWJPMTXATvFksayTLgi9nmRzQdkMuHyWncF889xCTSZWzH", JsonSerializer.SerializeToElement(new { })));
        Assert.Contains("not connected", ex.Message);

        var found = 0;
        await foreach (var _ in again.FindAsync("nobody/here", TimeSpan.FromMilliseconds(200))) found++;
        Assert.Equal(0, found);
    }

    [Fact]
    public async Task Two_devices_pair_over_the_local_network_and_sync_a_setting()
    {
        if (!HelperBuilt()) return;
        var karaoke = new Core.Settings.Setting<bool>("test.karaoke", Core.Settings.SettingScope.Synced, true);
        var networks = new List<SharedPeerNetwork>();
        Core.Devices.MyDevices Device(string name, Core.Settings.SettingsStore settings)
        {
            settings.Set(Core.Devices.DeviceSettings.Name, name);
            var network = new SharedPeerNetwork(async ct =>
                await PeerHelper.StartAsync(RealProcesses.Instance, Path.Combine(_dir.Path, name + ".key"), PeerDiscovery.Lan, ct: ct));
            networks.Add(network);
            return new Core.Devices.MyDevices(settings, network.GetAsync,
                presenceInterval: TimeSpan.FromSeconds(2), sendDelay: TimeSpan.FromMilliseconds(50));
        }
        var desktopSettings = Core.Settings.SettingsStore.CreateInMemory();
        var laptopSettings = Core.Settings.SettingsStore.CreateInMemory();
        await using var desktop = Device("Desktop", desktopSettings);
        await using var laptop = Device("Laptop", laptopSettings);
        await desktop.StartAsync();
        await laptop.StartAsync();

        var code = await desktop.StartPairingAsync();
        var joined = await laptop.JoinAsync(code);
        Assert.Equal("Desktop", joined.Name);

        desktopSettings.Set(karaoke, false);
        var end = DateTime.UtcNow.AddSeconds(15);
        while (laptopSettings.Get(karaoke) && DateTime.UtcNow < end) await Task.Delay(50);
        Assert.False(laptopSettings.Get(karaoke));

        await desktop.DisposeAsync();
        await laptop.DisposeAsync();
        foreach (var network in networks) await network.DisposeAsync();
    }

    [Fact]
    public async Task A_missing_helper_says_so()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            PeerHelper.StartAsync(RealProcesses.Instance, Path.Combine(_dir.Path, "k.key"), PeerDiscovery.None, Path.Combine(_dir.Path, "nope.exe")));
        Assert.Contains("nope.exe", ex.Message + ex.FileName);
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using HorizonRadio.Core.Friends;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Peers;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Devices;

/// <summary>A device in the group, as the Devices page shows it.</summary>
public sealed record DeviceStatus(string PeerId, string Name, bool IsThisDevice, PeerPath Path, DateTimeOffset? LastSeen);

/// <summary>
/// Your devices: pairing them (<see cref="StartPairingAsync"/> on one, <see cref="JoinAsync"/> with
/// its code on the other), keeping the group's device list in step, and syncing their synced
/// settings (<see cref="SettingScope.Synced"/>; device and secret settings never leave). No server is
/// involved: the devices meet over the peer network through a topic only the group's key gives.
///
/// When devices connect they send each other every synced setting, and each keeps whichever value
/// changed last (<see cref="SettingsStore.MergeSynced"/>); after that, each change is sent as it
/// happens. A device offline at the time catches up the next time they meet.
///
/// Messages are handled one at a time, in order, on a loop of their own; <see cref="Changed"/> is
/// raised from it (or the caller's thread), not the UI thread.
/// </summary>
public sealed class MyDevices : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly Func<CancellationToken, Task<IPeerNetwork>> _startNetwork;
    private readonly TimeProvider _time;
    private readonly TimeSpan _presenceInterval;
    private readonly TimeSpan _sendDelay;
    private readonly TimeSpan _joinTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly Lock _lock = new();

    // Under _lock.
    private readonly Dictionary<string, PeerPath> _paths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<DeviceMessage>> _joins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedData = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IDeviceData> _data;
    private IPeerNetwork? _network;
    private string? _advertisedGroupTopic;
    private PairingCode? _pairing;
    private DateTimeOffset _pairingExpires;
    private bool _sendScheduled;
    private Task? _loop;
    private Task? _presence;

    /// <param name="startNetwork">Starts the peer network (the helper) when it's first needed:
    /// when pairing, or at start when already paired.</param>
    /// <param name="data">What else the devices keep in step besides settings (mixes).</param>
    public MyDevices(SettingsStore settings, Func<CancellationToken, Task<IPeerNetwork>> startNetwork,
        IEnumerable<IDeviceData>? data = null,
        TimeProvider? time = null, TimeSpan? presenceInterval = null, TimeSpan? sendDelay = null, TimeSpan? joinTimeout = null)
    {
        _data = [.. data ?? []];
        foreach (var part in _data) part.Changed += () => OnDataChanged(part.Name);
        _settings = settings;
        _startNetwork = startNetwork;
        _time = time ?? TimeProvider.System;
        _presenceInterval = presenceInterval ?? TimeSpan.FromSeconds(30);
        _sendDelay = sendDelay ?? TimeSpan.FromMilliseconds(250);
        _joinTimeout = joinTimeout ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>The device list, the connections or the pairing changed.</summary>
    public event Action? Changed;

    /// <summary>What happened last that the user should hear about ("Paired with Laptop", "Someone
    /// tried a wrong code"), or why the network couldn't start.</summary>
    public string? Status { get; private set; }

    /// <summary>This device's ID on the peer network (kept from the last time it started).</summary>
    public string? PeerId => _network?.PeerId ?? _settings.Get(DeviceSettings.Self);

    /// <summary>What this device is called on the others.</summary>
    public string Name => _settings.Get(DeviceSettings.Name) is { Length: > 0 } name ? name : AppPaths.Label(Environment.MachineName);

    /// <summary>Whether this device is in a group with another.</summary>
    public bool IsPaired => _settings.Get(DeviceSettings.GroupKey) != null && Others().Any();

    /// <summary>The peer network has started (pairing, or paired).</summary>
    public bool IsOnline => _network != null;

    /// <summary>The code this device is showing for another to join with, and until when.</summary>
    public (PairingCode Code, DateTimeOffset Expires)? Pairing
    {
        get
        {
            lock (_lock) return _pairing != null ? (_pairing, _pairingExpires) : null;
        }
    }

    /// <summary>Every device in the group, this one first.</summary>
    public IReadOnlyList<DeviceStatus> Devices
    {
        get
        {
            var self = PeerId;
            var list = new List<DeviceStatus>();
            lock (_lock)
            {
                foreach (var d in _settings.Get(DeviceSettings.Devices).Where(d => !d.Removed))
                {
                    var isSelf = d.PeerId == self;
                    list.Add(new DeviceStatus(d.PeerId, isSelf ? Name : d.Name, isSelf,
                        isSelf ? PeerPath.Direct : _paths.GetValueOrDefault(d.PeerId),
                        _lastSeen.TryGetValue(d.PeerId, out var seen) ? seen : null));
                }
            }
            return [.. list.OrderByDescending(d => d.IsThisDevice).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    /// <summary>Start syncing: at once if this device is paired, otherwise when it pairs.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _settings.Changed += OnSettingChanged;
        _loop = Task.Run(RunAsync, CancellationToken.None);
        if (_settings.Get(DeviceSettings.GroupKey) != null && Others().Any())
        {
            try
            {
                await EnsureNetworkAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or PeerNetworkException or IOException or TimeoutException)
            {
                SetStatus($"Couldn't connect to your devices: {ex.Message}");
            }
        }
    }

    // ---- Pairing -------------------------------------------------------------------------------

    /// <summary>Show a code for another device to join this one's group with (it's good for
    /// <see cref="PairingCode.Lifetime"/>, and for one attempt). Replaces a code already showing.</summary>
    public async Task<PairingCode> StartPairingAsync(CancellationToken ct = default)
    {
        var network = await EnsureNetworkAsync(ct).ConfigureAwait(false);
        EnsureGroup(network.PeerId);
        CancelPairing();
        var code = PairingCode.New();
        lock (_lock)
        {
            _pairing = code;
            _pairingExpires = _time.GetUtcNow() + PairingCode.Lifetime;
        }
        await network.AdvertiseAsync(code.Topic, ct).ConfigureAwait(false);
        _ = ExpireAsync(code);
        SetStatus(null);
        return code;
    }

    /// <summary>Stop showing the pairing code.</summary>
    public void CancelPairing()
    {
        PairingCode? code;
        lock (_lock)
        {
            code = _pairing;
            _pairing = null;
        }
        if (code != null)
        {
            _ = _network?.UnadvertiseAsync(code.Topic);
            RaiseChanged();
        }
    }

    private async Task ExpireAsync(PairingCode code)
    {
        try
        {
            await Task.Delay(PairingCode.Lifetime, _time, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (_lock)
        {
            if (!Equals(_pairing, code)) return;
        }
        CancelPairing();
    }

    /// <summary>Join the group of the device showing <paramref name="code"/>. This device can't be in
    /// a group of its own already.</summary>
    /// <exception cref="PairingException">No device is showing the code, or it didn't match.</exception>
    public async Task<DeviceRecord> JoinAsync(PairingCode code, CancellationToken ct = default)
    {
        if (IsPaired) throw new PairingException("This device is already paired. Remove it from its devices first, or add the other device from here.");
        var network = await EnsureNetworkAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_joinTimeout);
        try
        {
            await foreach (var host in network.FindAsync(code.Topic, _joinTimeout, timeout.Token).ConfigureAwait(false))
            {
                if (host.PeerId == network.PeerId) continue;
                var reply = new TaskCompletionSource<DeviceMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_lock) _joins[host.PeerId] = reply;
                try
                {
                    await network.ConnectAsync(host.PeerId, host.Addresses, timeout.Token).ConfigureAwait(false);
                    await network.SendAsync(host.PeerId, new DeviceMessage
                    {
                        Type = DeviceMessage.Hello,
                        Name = Name,
                        Proof = code.Proof("hello", network.PeerId, host.PeerId),
                    }.ToJson(), timeout.Token).ConfigureAwait(false);

                    var answer = await reply.Task.WaitAsync(TimeSpan.FromSeconds(30), timeout.Token).ConfigureAwait(false);
                    if (answer.Type == DeviceMessage.Refused)
                        throw new PairingException("The code didn't match. Start pairing again on the other device for a new one.");
                    if (!code.Verifies(answer.Proof, "welcome", host.PeerId, network.PeerId) || answer.GroupKey is not { Length: > 0 })
                        throw new PairingException("The other device didn't prove it has the code, so this one didn't join it.");

                    var now = _time.GetUtcNow();
                    _settings.Set(DeviceSettings.GroupKey, answer.GroupKey);
                    // Your devices are all you to your friends.
                    if (answer.IdentityKey is { Length: > 0 } identity) _settings.Set(IdentitySettings.Key, identity);
                    _settings.Set(DeviceSettings.Devices, DeviceList.Merge(answer.Devices ?? [], [new DeviceRecord(network.PeerId, Name, now)]));
                    MarkSeen(host.PeerId, PeerPath.Direct);
                    var joined = _settings.Get(DeviceSettings.Devices).First(d => d.PeerId == host.PeerId);
                    SetStatus($"Paired with {joined.Name}.");
                    await AdvertiseGroupAsync(network).ConfigureAwait(false);
                    await SendFullSyncAsync(host.PeerId, reply: true).ConfigureAwait(false);
                    EnsurePresence();
                    return joined;
                }
                catch (Exception ex) when (ex is PeerNetworkException or TimeoutException)
                {
                    Log($"pairing with {host.PeerId} failed: {ex.Message}");
                }
                finally
                {
                    lock (_lock) _joins.Remove(host.PeerId);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        throw new PairingException("No device is showing that code. Check it, and that the other device is online.");
    }

    // ---- The group -----------------------------------------------------------------------------

    /// <summary>Take a device out of the group. It hears so the next time they meet, and nothing it
    /// sends is taken in any more.</summary>
    public async Task RemoveAsync(string peerId)
    {
        var record = _settings.Get(DeviceSettings.Devices).FirstOrDefault(d => d.PeerId == peerId && !d.Removed);
        if (record == null) return;
        if (record.PeerId == _network?.PeerId)
        {
            await LeaveAsync().ConfigureAwait(false);
            return;
        }
        await UpdateDevicesAsync([record with { Removed = true, Modified = _time.GetUtcNow() }]).ConfigureAwait(false);
        lock (_lock) _paths.Remove(peerId);
        RaiseChanged();
    }

    /// <summary>Take this device out of its group: the others are told, and it keeps its settings.</summary>
    public async Task LeaveAsync()
    {
        var self = _network?.PeerId;
        if (self != null)
        {
            var me = _settings.Get(DeviceSettings.Devices).FirstOrDefault(d => d.PeerId == self);
            if (me != null) await UpdateDevicesAsync([me with { Removed = true, Modified = _time.GetUtcNow() }]).ConfigureAwait(false);
        }
        await ForgetGroupAsync().ConfigureAwait(false);
        SetStatus("This device left its devices.");
    }

    /// <summary>Rename this device; the others see it the next time they meet.</summary>
    public async Task RenameAsync(string name)
    {
        name = name.Trim();
        _settings.Set(DeviceSettings.Name, name.Length > 0 ? name : null);
        var self = _network?.PeerId;
        var me = self == null ? null : _settings.Get(DeviceSettings.Devices).FirstOrDefault(d => d.PeerId == self);
        if (me != null) await UpdateDevicesAsync([me with { Name = Name, Modified = _time.GetUtcNow() }]).ConfigureAwait(false);
        RaiseChanged();
    }

    private void EnsureGroup(string self)
    {
        if (_settings.Get(DeviceSettings.GroupKey) == null)
            _settings.Set(DeviceSettings.GroupKey, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var devices = _settings.Get(DeviceSettings.Devices);
        if (!devices.Any(d => d.PeerId == self && !d.Removed))
            _settings.Set(DeviceSettings.Devices, DeviceList.Merge(devices, [new DeviceRecord(self, Name, _time.GetUtcNow())]));
    }

    private async Task ForgetGroupAsync()
    {
        string? topic;
        lock (_lock)
        {
            topic = _advertisedGroupTopic;
            _advertisedGroupTopic = null;
            _paths.Clear();
        }
        if (topic != null && _network != null) await _network.UnadvertiseAsync(topic).ConfigureAwait(false);
        _settings.Reset(DeviceSettings.GroupKey);
        _settings.Reset(DeviceSettings.Devices);
        RaiseChanged();
    }

    // Records changed here: merged into the list and sent to the devices online.
    private async Task UpdateDevicesAsync(DeviceRecord[] changed)
    {
        _settings.Set(DeviceSettings.Devices, DeviceList.Merge(_settings.Get(DeviceSettings.Devices), changed));
        var message = new DeviceMessage { Type = DeviceMessage.Sync, Devices = _settings.Get(DeviceSettings.Devices) }.ToJson();
        foreach (var peer in Connected().Concat(changed.Select(c => c.PeerId)).Distinct())
            await TrySendAsync(peer, message).ConfigureAwait(false);
    }

    private IEnumerable<DeviceRecord> Others()
    {
        var self = PeerId;
        return _settings.Get(DeviceSettings.Devices).Where(d => !d.Removed && d.PeerId != self);
    }

    private bool IsTrusted(string peerId) =>
        _settings.Get(DeviceSettings.GroupKey) != null
        && peerId != _network?.PeerId
        && _settings.Get(DeviceSettings.Devices).Any(d => d.PeerId == peerId && !d.Removed);

    private string[] Connected()
    {
        lock (_lock) return [.. _paths.Where(p => p.Value != PeerPath.None && IsTrusted(p.Key)).Select(p => p.Key)];
    }

    // The group's devices advertise and look for this topic, which only the group's key gives.
    private string? GroupTopic()
    {
        var key = _settings.Get(DeviceSettings.GroupKey);
        if (key == null) return null;
        var hash = HMACSHA256.HashData(Convert.FromBase64String(key), Encoding.UTF8.GetBytes("horizon-radio/devices"));
        return "devices/" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private async Task AdvertiseGroupAsync(IPeerNetwork network)
    {
        var topic = GroupTopic();
        string? old;
        lock (_lock)
        {
            if (topic == _advertisedGroupTopic) return;
            old = _advertisedGroupTopic;
            _advertisedGroupTopic = topic;
        }
        if (old != null) await network.UnadvertiseAsync(old).ConfigureAwait(false);
        if (topic != null) await network.AdvertiseAsync(topic).ConfigureAwait(false);
    }

    // ---- Presence: finding the group's devices and connecting --------------------------------------

    private void EnsurePresence()
    {
        lock (_lock) _presence ??= Task.Run(PresenceAsync);
    }

    private async Task PresenceAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (GroupTopic() is { } topic && Others().Any())
                {
                    var network = await EnsureNetworkAsync(ct).ConfigureAwait(false);
                    await AdvertiseGroupAsync(network).ConfigureAwait(false);
                    var look = _presenceInterval < TimeSpan.FromSeconds(20) ? _presenceInterval : TimeSpan.FromSeconds(20);
                    await foreach (var found in network.FindAsync(topic, look, ct).ConfigureAwait(false))
                    {
                        if (!IsTrusted(found.PeerId)) continue;
                        lock (_lock)
                        {
                            if (_paths.GetValueOrDefault(found.PeerId) != PeerPath.None) continue;
                        }
                        _ = MeetAsync(network, found);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is PeerNetworkException or FileNotFoundException or IOException or TimeoutException)
            {
                Log($"looking for your devices: {ex.Message}");
            }
            try
            {
                await Task.Delay(_presenceInterval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MeetAsync(IPeerNetwork network, FoundPeer found)
    {
        try
        {
            var path = await network.ConnectAsync(found.PeerId, found.Addresses, _stop.Token).ConfigureAwait(false);
            MarkSeen(found.PeerId, path);
            await SendFullSyncAsync(found.PeerId, reply: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PeerNetworkException or OperationCanceledException)
        {
            Log($"couldn't reach {found.PeerId}: {ex.Message}");
        }
    }

    // ---- Messages ------------------------------------------------------------------------------

    private async Task<IPeerNetwork> EnsureNetworkAsync(CancellationToken ct)
    {
        if (_network is { } running) return running;
        await _starting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_network is { } started) return started;
            var network = await _startNetwork(ct).ConfigureAwait(false);
            if (_settings.Get(DeviceSettings.Self) != network.PeerId) _settings.Set(DeviceSettings.Self, network.PeerId);
            network.MessageReceived += m => _work.Writer.TryWrite(() => OnMessageAsync(m));
            network.PathChanged += p => _work.Writer.TryWrite(() => OnPathChanged(p));
            network.Exited += () => _work.Writer.TryWrite(OnExited);
            lock (_lock) _network = network;
            if (GroupTopic() != null && Others().Any())
            {
                await AdvertiseGroupAsync(network).ConfigureAwait(false);
                EnsurePresence();
            }
            return network;
        }
        finally
        {
            _starting.Release();
        }
    }

    private async Task RunAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log($"handling a message failed: {ex}");
            }
        }
    }

    private async Task OnMessageAsync(PeerMessage received)
    {
        if (DeviceMessage.From(received.Message) is not { } message) return;
        var from = received.PeerId;
        switch (message.Type)
        {
            case DeviceMessage.Hello:
                await OnHelloAsync(from, message).ConfigureAwait(false);
                break;
            case DeviceMessage.Welcome or DeviceMessage.Refused:
                TaskCompletionSource<DeviceMessage>? join;
                lock (_lock) _joins.TryGetValue(from, out join);
                join?.TrySetResult(message);
                break;
            case DeviceMessage.Sync when IsTrusted(from):
                MarkSeen(from, null);
                await OnSyncAsync(from, message).ConfigureAwait(false);
                break;
            case DeviceMessage.Sync:
                Log($"ignored a sync from {from}, which isn't one of your devices");
                break;
            default:
                break; // another feature's message (friends)
        }
    }

    private async Task OnHelloAsync(string from, DeviceMessage hello)
    {
        var network = _network!;
        PairingCode? code;
        lock (_lock) code = _pairing;
        if (code == null)
        {
            await TrySendAsync(from, new DeviceMessage { Type = DeviceMessage.Refused }.ToJson()).ConfigureAwait(false);
            return;
        }
        CancelPairing(); // one attempt per code, right or wrong
        if (!code.Verifies(hello.Proof, "hello", from, network.PeerId))
        {
            await TrySendAsync(from, new DeviceMessage { Type = DeviceMessage.Refused }.ToJson()).ConfigureAwait(false);
            SetStatus("A device tried to pair with the wrong code. Start pairing again for a new one.");
            return;
        }

        var name = string.IsNullOrWhiteSpace(hello.Name) ? "Another device" : hello.Name.Trim();
        EnsureGroup(network.PeerId);
        _settings.Set(DeviceSettings.Devices, DeviceList.Merge(_settings.Get(DeviceSettings.Devices), [new DeviceRecord(from, name, _time.GetUtcNow())]));
        MarkSeen(from, PeerPath.Direct);
        await TrySendAsync(from, new DeviceMessage
        {
            Type = DeviceMessage.Welcome,
            Name = Name,
            Proof = code.Proof("welcome", network.PeerId, from),
            GroupKey = _settings.Get(DeviceSettings.GroupKey),
            IdentityKey = IdentityKeyToShare(),
            Devices = _settings.Get(DeviceSettings.Devices),
        }.ToJson()).ConfigureAwait(false);
        SetStatus($"Paired with {name}.");
        await AdvertiseGroupAsync(network).ConfigureAwait(false);
        EnsurePresence();
        // The other devices hear of the new one.
        await UpdateDevicesAsync([]).ConfigureAwait(false);
    }

    private string? IdentityKeyToShare()
    {
        Identity.GetOrCreate(_settings);
        return _settings.Get(IdentitySettings.Key);
    }

    private async Task OnSyncAsync(string from, DeviceMessage sync)
    {
        if (sync.Devices is { Length: > 0 } devices)
        {
            var merged = DeviceList.Merge(_settings.Get(DeviceSettings.Devices), devices);
            _settings.Set(DeviceSettings.Devices, merged);
            var self = _network?.PeerId;
            if (merged.Any(d => d.PeerId == self && d.Removed))
            {
                await ForgetGroupAsync().ConfigureAwait(false);
                SetStatus("This device was removed from your devices.");
                return;
            }
            RaiseChanged();
        }
        if (sync.Settings is { Length: > 0 } settings)
        {
            var changed = _settings.MergeSynced(settings.Select(s => s.ToSetting()));
            if (changed.Count > 0) Log($"took {changed.Count} setting(s) from {from}");
        }
        if (sync.Data is { Count: > 0 } data)
        {
            foreach (var part in _data)
            {
                if (data.TryGetValue(part.Name, out var theirs)) await part.MergeAsync(theirs).ConfigureAwait(false);
            }
        }
        if (sync.Reply) await SendFullSyncAsync(from, reply: false).ConfigureAwait(false);
    }

    private Task OnPathChanged(PeerPathChange change)
    {
        lock (_lock)
        {
            _paths[change.PeerId] = change.Path;
            if (change.Path != PeerPath.None) _lastSeen[change.PeerId] = _time.GetUtcNow();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    private Task OnExited()
    {
        lock (_lock)
        {
            _network = null;
            _advertisedGroupTopic = null;
            _pairing = null;
            _paths.Clear();
        }
        SetStatus("The connection to your devices stopped; it restarts on its own.");
        return Task.CompletedTask;
    }

    private void MarkSeen(string peerId, PeerPath? path)
    {
        lock (_lock)
        {
            if (path is { } p) _paths[peerId] = p;
            else if (_paths.GetValueOrDefault(peerId) == PeerPath.None) _paths[peerId] = PeerPath.Direct;
            _lastSeen[peerId] = _time.GetUtcNow();
        }
        RaiseChanged();
    }

    // Everything: the device list and settings in one message, then each kind of data in its parts.
    private async Task SendFullSyncAsync(string peerId, bool reply)
    {
        await TrySendAsync(peerId, new DeviceMessage
        {
            Type = DeviceMessage.Sync,
            Devices = _settings.Get(DeviceSettings.Devices),
            Settings = [.. _settings.SyncedSettings().Select(WireSetting.From)],
            Reply = reply,
        }.ToJson()).ConfigureAwait(false);
        foreach (var message in await DataMessagesAsync(_data, full: true).ConfigureAwait(false))
            await TrySendAsync(peerId, message).ConfigureAwait(false);
    }

    private static async Task<List<System.Text.Json.JsonElement>> DataMessagesAsync(IEnumerable<IDeviceData> parts, bool full)
    {
        var messages = new List<System.Text.Json.JsonElement>();
        foreach (var part in parts)
        {
            foreach (var chunk in await part.ExportAsync(full).ConfigureAwait(false))
            {
                messages.Add(new DeviceMessage
                {
                    Type = DeviceMessage.Sync,
                    Data = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal) { [part.Name] = chunk },
                }.ToJson());
            }
        }
        return messages;
    }

    private async Task TrySendAsync(string peerId, System.Text.Json.JsonElement message)
    {
        if (_network is not { } network) return;
        try
        {
            await network.SendAsync(peerId, message).ConfigureAwait(false);
        }
        catch (PeerNetworkException ex)
        {
            Log($"couldn't send to {peerId}: {ex.Message}");
        }
    }

    // ---- Local changes -------------------------------------------------------------------------

    // A synced setting changed here (or was taken from another device: sending it on is harmless,
    // since the others already have it or take the newer one): sent shortly after, so a slider
    // dragged through fifty values sends one message.
    private void OnSettingChanged(string key)
    {
        if (key.StartsWith("devices.", StringComparison.Ordinal)) return;
        lock (_lock)
        {
            _changedKeys.Add(key);
            if (_sendScheduled) return;
            _sendScheduled = true;
        }
        _ = SendChangesSoonAsync();
    }

    private void OnDataChanged(string name)
    {
        lock (_lock)
        {
            _changedData.Add(name);
            if (_sendScheduled) return;
            _sendScheduled = true;
        }
        _ = SendChangesSoonAsync();
    }

    private async Task SendChangesSoonAsync()
    {
        try
        {
            await Task.Delay(_sendDelay, _time, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        HashSet<string> keys, parts;
        lock (_lock)
        {
            keys = [.. _changedKeys];
            parts = [.. _changedData];
            _changedKeys.Clear();
            _changedData.Clear();
            _sendScheduled = false;
        }
        if (Connected().Length == 0) return; // nobody to tell: they catch up when they meet
        var changes = _settings.SyncedSettings().Where(s => keys.Contains(s.Key)).Select(WireSetting.From).ToArray();
        var messages = await DataMessagesAsync(_data.Where(p => parts.Contains(p.Name)), full: false).ConfigureAwait(false);
        if (changes.Length > 0) messages.Insert(0, new DeviceMessage { Type = DeviceMessage.Sync, Settings = changes }.ToJson());
        if (messages.Count == 0) return;
        _work.Writer.TryWrite(async () =>
        {
            foreach (var peer in Connected())
            {
                foreach (var message in messages) await TrySendAsync(peer, message).ConfigureAwait(false);
            }
        });
    }

    private void SetStatus(string? status)
    {
        Status = status;
        if (status != null) Log(status);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"a Changed handler failed: {ex}");
        }
    }

    private static void Log(string message) => Trace.WriteLine($"[hzn-devices] {message}");

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _settings.Changed -= OnSettingChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        _work.Writer.TryComplete();
        if (_loop != null) await _loop.ConfigureAwait(false);
        if (_presence != null)
        {
            try { await _presence.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose(); // the network is shared: whoever made it stops it
        _starting.Dispose();
    }
}

/// <summary>Pairing didn't happen; the message says why, for the user.</summary>
public sealed class PairingException(string message) : Exception(message);

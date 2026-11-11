using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Peers;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Friends;

/// <summary>A friend, as you keep them: their public key (which proves it's them) and their name.
/// Kept as a synced setting per friend (<c>friend.&lt;user id&gt;</c>), so a friend added on one of
/// your devices is a friend on all of them.</summary>
public sealed record FriendRecord(string PublicKey, string Name);

/// <summary>What someone is listening to.</summary>
public sealed record Listening(string Title, string Artist, string Source, string? ExternalId = null);

/// <summary>A friend as the Friends page shows them.</summary>
public sealed record FriendStatus(string UserId, string Name, bool IsOnline, Listening? Listening, DateTimeOffset? LastSeen);

/// <summary>
/// Your friends: adding one (<see cref="InviteAsync"/> shows a code, <see cref="AcceptAsync"/> takes
/// one), seeing when they're online and what they're listening to, and telling them what you are
/// (<see cref="SetListening"/>). No server: each of your devices advertises a topic made from your
/// user id, which only your friends know; theirs find it, connect, and each side proves who it is by
/// signing a hello bound to the two connections' peer ids with its identity key
/// (<see cref="Identity"/>), so nobody else can pass as either.
///
/// Messages are handled one at a time on a loop of their own; <see cref="Changed"/> is raised off the
/// UI thread.
/// </summary>
public sealed class Friends : IAsyncDisposable
{
    public const string RecordPrefix = "friend.";

    private readonly SettingsStore _settings;
    private readonly Func<CancellationToken, Task<IPeerNetwork>> _getNetwork;
    private readonly TimeProvider _time;
    private readonly TimeSpan _presenceInterval;
    private readonly TimeSpan _joinTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();

    // Under _lock. A friend's devices we've verified: peer → user id; and what each is playing.
    private readonly Dictionary<string, string> _friendPeers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Listening?> _listening = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<FriendMessage>> _accepts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connecting = new(StringComparer.Ordinal);
    private IPeerNetwork? _network;
    private string? _advertised;
    private PairingCode? _invite;
    private DateTimeOffset _inviteExpires;
    private Listening? _myListening;
    private Task? _loop;
    private Task? _presence;

    /// <param name="getNetwork">The peer network, started when first needed (<see cref="SharedPeerNetwork.GetAsync"/>).</param>
    public Friends(SettingsStore settings, Func<CancellationToken, Task<IPeerNetwork>> getNetwork,
        TimeProvider? time = null, TimeSpan? presenceInterval = null, TimeSpan? joinTimeout = null)
    {
        _settings = settings;
        _getNetwork = getNetwork;
        _time = time ?? TimeProvider.System;
        _presenceInterval = presenceInterval ?? TimeSpan.FromSeconds(30);
        _joinTimeout = joinTimeout ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>The friend list, who's online, what they're playing, or the invite changed.</summary>
    public event Action? Changed;

    /// <summary>What happened last that you should hear about ("Added Sam").</summary>
    public string? Status { get; private set; }

    /// <summary>What your friends see you as.</summary>
    public string MyName => _settings.Get(IdentitySettings.Name) is { Length: > 0 } name ? name : Environment.UserName;

    /// <summary>The code this device is showing for a friend to add you with, and until when.</summary>
    public (PairingCode Code, DateTimeOffset Expires)? Invite
    {
        get
        {
            lock (_lock) return _invite != null ? (_invite, _inviteExpires) : null;
        }
    }

    /// <summary>Your friends, online ones first.</summary>
    public IReadOnlyList<FriendStatus> All
    {
        get
        {
            var list = new List<FriendStatus>();
            lock (_lock)
            {
                foreach (var (userId, friend) in Records())
                {
                    var peers = _friendPeers.Where(p => p.Value == userId).Select(p => p.Key).ToList();
                    var listening = peers.Select(p => _listening.GetValueOrDefault(p)).FirstOrDefault(l => l != null);
                    list.Add(new FriendStatus(userId, friend.Name, peers.Count > 0, listening,
                        _lastSeen.TryGetValue(userId, out var seen) ? seen : null));
                }
            }
            return [.. list.OrderByDescending(f => f.IsOnline).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    public Task StartAsync()
    {
        _settings.Changed += OnSettingChanged;
        _loop = Task.Run(RunAsync, CancellationToken.None);
        if (Records().Any()) EnsurePresence();
        return Task.CompletedTask;
    }

    // A friend added on another of your devices (it syncs): start looking for them here too.
    private void OnSettingChanged(string key)
    {
        if (key.StartsWith(RecordPrefix, StringComparison.Ordinal))
        {
            if (Records().Any()) EnsurePresence();
            RaiseChanged();
        }
        else if (key == IdentitySettings.Name.Key)
        {
            _work.Writer.TryWrite(SendStatusToAllAsync);
        }
    }

    // ---- Adding friends ------------------------------------------------------------------------

    /// <summary>Show a code for a friend to add you with (good for <see cref="PairingCode.Lifetime"/>
    /// and one try).</summary>
    public async Task<PairingCode> InviteAsync(CancellationToken ct = default)
    {
        var network = await EnsureNetworkAsync(ct).ConfigureAwait(false);
        Identity.GetOrCreate(_settings);
        CancelInvite();
        var code = PairingCode.New();
        lock (_lock)
        {
            _invite = code;
            _inviteExpires = _time.GetUtcNow() + PairingCode.Lifetime;
        }
        await network.AdvertiseAsync(code.TopicFor("friend"), ct).ConfigureAwait(false);
        _ = ExpireAsync(code);
        SetStatus(null);
        return code;
    }

    public void CancelInvite()
    {
        PairingCode? code;
        lock (_lock)
        {
            code = _invite;
            _invite = null;
        }
        if (code != null)
        {
            _ = _network?.UnadvertiseAsync(code.TopicFor("friend"));
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
            if (!Equals(_invite, code)) return;
        }
        CancelInvite();
    }

    /// <summary>Add the friend showing <paramref name="code"/>; they add you too.</summary>
    /// <exception cref="PairingException">Nobody's showing the code, or it didn't match.</exception>
    public async Task<FriendStatus> AcceptAsync(PairingCode code, CancellationToken ct = default)
    {
        var network = await EnsureNetworkAsync(ct).ConfigureAwait(false);
        var me = Identity.GetOrCreate(_settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_joinTimeout);
        try
        {
            await foreach (var host in network.FindAsync(code.TopicFor("friend"), _joinTimeout, timeout.Token).ConfigureAwait(false))
            {
                if (host.PeerId == network.PeerId) continue;
                var reply = new TaskCompletionSource<FriendMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_lock) _accepts[host.PeerId] = reply;
                try
                {
                    await network.ConnectAsync(host.PeerId, host.Addresses, timeout.Token).ConfigureAwait(false);
                    await network.SendAsync(host.PeerId, new FriendMessage
                    {
                        Type = FriendMessage.Request,
                        Name = MyName,
                        UserKey = me.PublicKey,
                        Proof = code.Proof("friend-request", network.PeerId, host.PeerId),
                    }.ToJson(), timeout.Token).ConfigureAwait(false);

                    var answer = await reply.Task.WaitAsync(TimeSpan.FromSeconds(30), timeout.Token).ConfigureAwait(false);
                    if (answer.Type == FriendMessage.Refused)
                        throw new PairingException("The code didn't match. Ask your friend to show a new one.");
                    if (!code.Verifies(answer.Proof, "friend-accept", host.PeerId, network.PeerId) || answer.UserKey is not { Length: > 0 } key)
                        throw new PairingException("The other side didn't prove it has the code, so they weren't added.");

                    var userId = Identity.UserIdOf(key);
                    if (userId == me.UserId) throw new PairingException("That's your own code: show it on a friend's PC, not one of yours.");
                    var name = Clean(answer.Name, "Friend");
                    AddRecord(userId, new FriendRecord(key, name));
                    SetStatus($"Added {name}.");
                    await HelloAsync(network, host.PeerId, reply: true).ConfigureAwait(false);
                    EnsurePresence();
                    return All.First(f => f.UserId == userId);
                }
                catch (Exception ex) when (ex is PeerNetworkException or TimeoutException)
                {
                    Log($"adding through {host.PeerId} failed: {ex.Message}");
                }
                finally
                {
                    lock (_lock) _accepts.Remove(host.PeerId);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        throw new PairingException("Nobody is showing that code. Check it, and that your friend's Horizon Radio is open.");
    }

    /// <summary>Stop being friends (on all your devices): they're no longer told what you play.</summary>
    public Task RemoveAsync(string userId)
    {
        _settings.Remove(RecordPrefix + userId);
        lock (_lock)
        {
            foreach (var peer in _friendPeers.Where(p => p.Value == userId).Select(p => p.Key).ToList())
            {
                _friendPeers.Remove(peer);
                _listening.Remove(peer);
            }
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    /// <summary>Change what your friends see you as (on all your devices).</summary>
    public void Rename(string name)
    {
        name = name.Trim();
        _settings.Set(IdentitySettings.Name, name.Length > 0 ? name : null);
        RaiseChanged();
    }

    /// <summary>What this device is playing now (null: nothing), for your friends.</summary>
    public void SetListening(Listening? now)
    {
        lock (_lock)
        {
            if (Equals(_myListening, now)) return;
            _myListening = now;
        }
        _work.Writer.TryWrite(SendStatusToAllAsync);
    }

    private IEnumerable<(string UserId, FriendRecord Friend)> Records()
    {
        foreach (var key in _settings.KeysWithPrefix(RecordPrefix))
        {
            var setting = new Setting<FriendRecord?>(key, SettingScope.Synced, null);
            if (_settings.Get(setting) is { PublicKey.Length: > 0 } friend) yield return (key[RecordPrefix.Length..], friend);
        }
    }

    private FriendRecord? RecordOf(string userId) =>
        _settings.Get(new Setting<FriendRecord?>(RecordPrefix + userId, SettingScope.Synced, null));

    private void AddRecord(string userId, FriendRecord friend) =>
        _settings.Set(new Setting<FriendRecord?>(RecordPrefix + userId, SettingScope.Synced, null), friend);

    // ---- Presence ------------------------------------------------------------------------------

    private static string PresenceTopic(string userId) => "presence/" + userId;

    private void EnsurePresence()
    {
        lock (_lock) _presence ??= Task.Run(PresenceAsync, CancellationToken.None);
    }

    private async Task PresenceAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var friends = Records().ToList();
                if (friends.Count > 0)
                {
                    var network = await EnsureNetworkAsync(ct).ConfigureAwait(false);
                    var look = _presenceInterval < TimeSpan.FromSeconds(20) ? _presenceInterval : TimeSpan.FromSeconds(20);
                    await Task.WhenAll(friends.Select(f => LookForAsync(network, f.UserId, look, ct))).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is PeerNetworkException or FileNotFoundException or IOException or TimeoutException)
            {
                Log($"looking for your friends: {ex.Message}");
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

    private async Task LookForAsync(IPeerNetwork network, string userId, TimeSpan look, CancellationToken ct)
    {
        await foreach (var found in network.FindAsync(PresenceTopic(userId), look, ct).ConfigureAwait(false))
        {
            lock (_lock)
            {
                if (_friendPeers.ContainsKey(found.PeerId) || !_connecting.Add(found.PeerId)) continue;
            }
            _ = MeetAsync(network, found);
        }
    }

    private async Task MeetAsync(IPeerNetwork network, FoundPeer found)
    {
        try
        {
            await network.ConnectAsync(found.PeerId, found.Addresses, _stop.Token).ConfigureAwait(false);
            await HelloAsync(network, found.PeerId, reply: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PeerNetworkException or OperationCanceledException)
        {
            Log($"couldn't reach {found.PeerId}: {ex.Message}");
        }
        finally
        {
            lock (_lock) _connecting.Remove(found.PeerId);
        }
    }

    private static string HelloText(string from, string to) => $"horizon-radio/friend|{from}|{to}";

    private Task HelloAsync(IPeerNetwork network, string peerId, bool reply)
    {
        var me = Identity.GetOrCreate(_settings);
        return TrySendAsync(peerId, new FriendMessage
        {
            Type = FriendMessage.Hello,
            Name = MyName,
            UserKey = me.PublicKey,
            Proof = me.Sign(HelloText(network.PeerId, peerId)),
            Reply = reply,
        });
    }

    // ---- Messages ------------------------------------------------------------------------------

    private async Task<IPeerNetwork> EnsureNetworkAsync(CancellationToken ct)
    {
        if (_network is { } running) return running;
        var network = await _getNetwork(ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (ReferenceEquals(_network, network)) return network;
            _network = network;
        }
        network.MessageReceived += m => _work.Writer.TryWrite(() => OnMessageAsync(m));
        network.PathChanged += p => _work.Writer.TryWrite(() => OnPathChanged(p));
        network.Exited += () => _work.Writer.TryWrite(OnExited);
        var topic = PresenceTopic(Identity.GetOrCreate(_settings).UserId);
        await network.AdvertiseAsync(topic, ct).ConfigureAwait(false);
        lock (_lock) _advertised = topic;
        return network;
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
        if (FriendMessage.From(received.Message) is not { } message) return;
        var from = received.PeerId;
        switch (message.Type)
        {
            case FriendMessage.Request:
                await OnRequestAsync(from, message).ConfigureAwait(false);
                break;
            case FriendMessage.Accept or FriendMessage.Refused:
                TaskCompletionSource<FriendMessage>? accept;
                lock (_lock) _accepts.TryGetValue(from, out accept);
                accept?.TrySetResult(message);
                break;
            case FriendMessage.Hello:
                await OnHelloAsync(from, message).ConfigureAwait(false);
                break;
            case FriendMessage.StatusType:
                string? userId;
                lock (_lock)
                {
                    if (!_friendPeers.TryGetValue(from, out userId)) return; // not a friend we've verified
                    _listening[from] = message.Listening;
                    _lastSeen[userId] = _time.GetUtcNow();
                }
                RaiseChanged();
                break;
        }
    }

    private async Task OnRequestAsync(string from, FriendMessage request)
    {
        var network = _network!;
        PairingCode? code;
        lock (_lock) code = _invite;
        if (code == null)
        {
            await TrySendAsync(from, new FriendMessage { Type = FriendMessage.Refused }).ConfigureAwait(false);
            return;
        }
        CancelInvite(); // one try per code
        if (!code.Verifies(request.Proof, "friend-request", from, network.PeerId) || request.UserKey is not { Length: > 0 } key)
        {
            await TrySendAsync(from, new FriendMessage { Type = FriendMessage.Refused }).ConfigureAwait(false);
            SetStatus("Someone tried to add you with the wrong code. Show a new one.");
            return;
        }
        var me = Identity.GetOrCreate(_settings);
        var userId = Identity.UserIdOf(key);
        if (userId == me.UserId)
        {
            await TrySendAsync(from, new FriendMessage { Type = FriendMessage.Refused }).ConfigureAwait(false);
            SetStatus("That code was typed on one of your own PCs. Show it to a friend instead.");
            return;
        }
        var name = Clean(request.Name, "Friend");
        AddRecord(userId, new FriendRecord(key, name));
        await TrySendAsync(from, new FriendMessage
        {
            Type = FriendMessage.Accept,
            Name = MyName,
            UserKey = me.PublicKey,
            Proof = code.Proof("friend-accept", network.PeerId, from),
        }).ConfigureAwait(false);
        SetStatus($"Added {name}.");
        EnsurePresence();
    }

    private async Task OnHelloAsync(string from, FriendMessage hello)
    {
        if (hello.UserKey is not { Length: > 0 } key || _network is not { } network) return;
        var userId = Identity.UserIdOf(key);
        var friend = RecordOf(userId);
        // Only a friend, with the key we keep for them, signing this very connection.
        if (friend == null || friend.PublicKey != key || !Identity.Verify(key, HelloText(from, network.PeerId), hello.Proof))
        {
            Log($"ignored a hello from {from}, which isn't a friend's");
            return;
        }
        bool known;
        lock (_lock)
        {
            known = _friendPeers.ContainsKey(from);
            _friendPeers[from] = userId;
            _lastSeen[userId] = _time.GetUtcNow();
        }
        if (Clean(hello.Name, friend.Name) is var name && name != friend.Name) AddRecord(userId, friend with { Name = name });
        if (hello.Reply || !known) await HelloAsync(network, from, reply: false).ConfigureAwait(false);
        await SendStatusAsync(from).ConfigureAwait(false);
        RaiseChanged();
    }

    private Task OnPathChanged(PeerPathChange change)
    {
        if (change.Path != PeerPath.None) return Task.CompletedTask;
        bool was;
        lock (_lock)
        {
            was = _friendPeers.Remove(change.PeerId, out var userId);
            _listening.Remove(change.PeerId);
            if (was) _lastSeen[userId!] = _time.GetUtcNow();
        }
        if (was) RaiseChanged();
        return Task.CompletedTask;
    }

    private Task OnExited()
    {
        lock (_lock)
        {
            _network = null;
            _advertised = null;
            _invite = null;
            _friendPeers.Clear();
            _listening.Clear();
        }
        RaiseChanged();
        return Task.CompletedTask;
    }

    private Task SendStatusAsync(string peerId)
    {
        Listening? now;
        lock (_lock) now = _myListening;
        return TrySendAsync(peerId, new FriendMessage { Type = FriendMessage.StatusType, Name = MyName, Listening = now });
    }

    private async Task SendStatusToAllAsync()
    {
        string[] peers;
        lock (_lock) peers = [.. _friendPeers.Keys];
        foreach (var peer in peers) await SendStatusAsync(peer).ConfigureAwait(false);
    }

    private async Task TrySendAsync(string peerId, FriendMessage message)
    {
        if (_network is not { } network) return;
        try
        {
            await network.SendAsync(peerId, message.ToJson()).ConfigureAwait(false);
        }
        catch (PeerNetworkException ex)
        {
            Log($"couldn't send to {peerId}: {ex.Message}");
        }
    }

    private static string Clean(string? name, string fallback)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length > 40) trimmed = trimmed[..40];
        return trimmed.Length > 0 ? trimmed : fallback;
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

    private static void Log(string message) => Trace.WriteLine($"[hzn-friends] {message}");

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
        if (_advertised != null && _network != null)
        {
            try { await _network.UnadvertiseAsync(_advertised).ConfigureAwait(false); }
            catch (Exception ex) when (ex is PeerNetworkException or ObjectDisposedException) { }
        }
        _stop.Dispose();
    }
}

// What friends say to each other:
//   friend.request  you (typing their code) → them  { name, userKey, proof }
//   friend.accept   them → you                       { name, userKey, proof }
//   friend.refused  them → you                       wrong code, or none showing
//   friend.hello    either way                       { name, userKey, proof (signed), reply }
//   friend.status   either way                       { name, listening? }
internal sealed record FriendMessage
{
    public required string Type { get; init; }
    public string? Name { get; init; }
    public string? UserKey { get; init; }
    public string? Proof { get; init; }
    public bool Reply { get; init; }
    public Listening? Listening { get; init; }

    public const string Request = "friend.request";
    public const string Accept = "friend.accept";
    public const string Refused = "friend.refused";
    public const string Hello = "friend.hello";
    public const string StatusType = "friend.status";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Json);

    public static FriendMessage? From(JsonElement json)
    {
        try
        {
            var message = json.Deserialize<FriendMessage>(Json);
            return message?.Type?.StartsWith("friend.", StringComparison.Ordinal) == true ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

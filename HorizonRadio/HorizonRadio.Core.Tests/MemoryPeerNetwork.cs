using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using HorizonRadio.Sdk.Peers;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// An in-memory peer network for tests: peers find each other by topic, connect, and exchange
/// messages, delivered in order on each peer's own thread as the real helper does. A peer can go
/// offline (<see cref="MemoryPeer.GoOffline"/>), dropping its connections.
/// </summary>
internal sealed class MemoryPeerNetwork
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MemoryPeer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _topics = new(StringComparer.Ordinal);
    private int _next;

    /// <summary>A new peer (a new device's helper). Pass the same id to bring a device back.</summary>
    public MemoryPeer Join(string? id = null)
    {
        var peer = new MemoryPeer(this, id ?? $"12D3Peer{Interlocked.Increment(ref _next)}");
        lock (_lock) _peers[peer.PeerId] = peer;
        return peer;
    }

    internal void Advertise(string peer, string topic, bool on)
    {
        lock (_lock)
        {
            if (!_topics.TryGetValue(topic, out var set)) _topics[topic] = set = new(StringComparer.Ordinal);
            if (on) set.Add(peer);
            else set.Remove(peer);
        }
    }

    internal string[] Advertisers(string topic)
    {
        lock (_lock) return _topics.TryGetValue(topic, out var set) ? [.. set] : [];
    }

    internal MemoryPeer? Find(string id)
    {
        lock (_lock) return _peers.TryGetValue(id, out var p) && !p.IsOffline ? p : null;
    }

    internal void Leave(MemoryPeer peer)
    {
        lock (_lock)
        {
            if (_peers.TryGetValue(peer.PeerId, out var current) && current == peer) _peers.Remove(peer.PeerId);
            foreach (var set in _topics.Values) set.Remove(peer.PeerId);
        }
    }
}

internal sealed class MemoryPeer : IPeerNetwork, IAsyncDisposable
{
    private readonly MemoryPeerNetwork _net;
    private readonly Channel<Action> _inbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly HashSet<string> _connected = new(StringComparer.Ordinal);
    private readonly Task _delivering;

    public MemoryPeer(MemoryPeerNetwork net, string id)
    {
        _net = net;
        PeerId = id;
        _delivering = Task.Run(async () =>
        {
            await foreach (var deliver in _inbox.Reader.ReadAllAsync()) deliver();
        });
    }

    public string PeerId { get; }

    public bool IsOffline { get; private set; }

    /// <summary>Messages this peer sent, for tests that look at the wire.</summary>
    public List<(string To, JsonElement Message)> Sent { get; } = [];

    public event Action<PeerMessage>? MessageReceived;
    public event Action<PeerPathChange>? PathChanged;
    public event Action? Exited;

    public Task AdvertiseAsync(string topic, CancellationToken ct = default)
    {
        _net.Advertise(PeerId, topic, true);
        return Task.CompletedTask;
    }

    public Task UnadvertiseAsync(string topic, CancellationToken ct = default)
    {
        _net.Advertise(PeerId, topic, false);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<FoundPeer> FindAsync(string topic, TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested && !IsOffline)
        {
            foreach (var id in _net.Advertisers(topic))
            {
                if (id != PeerId && seen.Add(id)) yield return new FoundPeer(id, [$"/memory/{id}"]);
            }
            try
            {
                await Task.Delay(20, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public Task<PeerPath> ConnectAsync(string peerId, IReadOnlyList<string>? addresses = null, CancellationToken ct = default)
    {
        if (IsOffline) throw new PeerNetworkException("offline");
        var other = _net.Find(peerId) ?? throw new PeerNetworkException($"can't reach {peerId}");
        Link(peerId);
        other.Link(PeerId, report: false); // the other side reports it when the first message arrives
        return Task.FromResult(PeerPath.Direct);
    }

    public Task SendAsync(string peerId, JsonElement message, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (IsOffline || !_connected.Contains(peerId)) throw new PeerNetworkException("not connected to that peer");
            Sent.Add((peerId, message.Clone()));
        }
        var other = _net.Find(peerId) ?? throw new PeerNetworkException("not connected to that peer");
        other.Deliver(PeerId, message.Clone());
        return Task.CompletedTask;
    }

    /// <summary>Lets a test send as this peer to anyone, connected or not (an impostor).</summary>
    public void SendUnchecked(MemoryPeer to, JsonElement message) => to.Deliver(PeerId, message.Clone());

    private void Deliver(string from, JsonElement message) => _inbox.Writer.TryWrite(() =>
    {
        if (IsOffline) return;
        Link(from); // the helper starts watching a peer it hears from
        MessageReceived?.Invoke(new PeerMessage(from, message));
    });

    internal void Link(string peerId, bool report = true)
    {
        bool added;
        lock (_lock) added = _connected.Add(peerId);
        if (added && report) _inbox.Writer.TryWrite(() => PathChanged?.Invoke(new PeerPathChange(peerId, PeerPath.Direct)));
    }

    internal void Unlink(string peerId)
    {
        bool removed;
        lock (_lock) removed = _connected.Remove(peerId);
        if (removed) _inbox.Writer.TryWrite(() => PathChanged?.Invoke(new PeerPathChange(peerId, PeerPath.None)));
    }

    /// <summary>The helper exits: connections drop on both sides.</summary>
    public void GoOffline()
    {
        string[] peers;
        lock (_lock)
        {
            IsOffline = true;
            peers = [.. _connected];
            _connected.Clear();
        }
        _net.Leave(this);
        foreach (var id in peers) _net.Find(id)?.Unlink(PeerId);
        _inbox.Writer.TryWrite(() => Exited?.Invoke());
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsOffline) GoOffline();
        _inbox.Writer.TryComplete();
        await _delivering;
    }
}

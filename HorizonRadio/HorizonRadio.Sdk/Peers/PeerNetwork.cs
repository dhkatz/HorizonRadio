using System.Text.Json;

namespace HorizonRadio.Sdk.Peers;

/// <summary>
/// Starts the peer network your devices and friends meet over. The host calls it once, when one of
/// them first needs it (a paired PC, a friend, a party), and stops the network at exit. The peer
/// helper's plugin provides it; a plugin can register another network instead.
/// </summary>
public interface IPeerNetworkFactory
{
    Task<IPeerNetwork> StartAsync(CancellationToken ct);
}

/// <summary>
/// The peer-to-peer network as the app's features use it (the helper process implements it;
/// tests use an in-memory one): peers find each other by topic, connect, and exchange JSON
/// messages, in order per peer and at most once each. A message's sender is authenticated by
/// the network: <see cref="PeerMessage.PeerId"/> is who sent it.
/// </summary>
public interface IPeerNetwork
{
    /// <summary>This peer's ID: how other peers address it.</summary>
    string PeerId { get; }

    /// <summary>Be findable under <paramref name="topic"/> until <see cref="UnadvertiseAsync"/>.
    /// Only those who know the topic can find it.</summary>
    Task AdvertiseAsync(string topic, CancellationToken ct = default);

    Task UnadvertiseAsync(string topic, CancellationToken ct = default);

    /// <summary>Each peer found under <paramref name="topic"/>, once, until
    /// <paramref name="timeout"/> or the caller stops.</summary>
    IAsyncEnumerable<FoundPeer> FindAsync(string topic, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Connect to a peer by the addresses found for it (or those already known).</summary>
    Task<PeerPath> ConnectAsync(string peerId, IReadOnlyList<string>? addresses = null, CancellationToken ct = default);

    /// <summary>Send a message to a connected peer. Throws <see cref="PeerNetworkException"/>
    /// when it can't (not connected, too big).</summary>
    Task SendAsync(string peerId, JsonElement message, CancellationToken ct = default);

    /// <summary>A message arrived (raised on the network's thread: keep handlers short).</summary>
    event Action<PeerMessage>? MessageReceived;

    /// <summary>A peer connected to, or heard from, changed path.</summary>
    event Action<PeerPathChange>? PathChanged;

    /// <summary>The network stopped (the helper exited).</summary>
    event Action? Exited;
}

/// <summary>How the helper finds peers by topic.</summary>
[Flags]
public enum PeerDiscovery
{
    /// <summary>Only peers it's given addresses for.</summary>
    None = 0,

    /// <summary>Provider records in the public IPFS DHT (Amino): peers anywhere.</summary>
    Internet = 1,

    /// <summary>mDNS: peers on the local network, and other instances on this machine.</summary>
    Lan = 2,

    All = Internet | Lan,
}

/// <summary>How a peer is connected: straight to it, through a relay (which cuts the connection
/// after 2 minutes or 128 KB), or not at all.</summary>
public enum PeerPath
{
    None,
    Relayed,
    Direct,
}

/// <summary>A peer found under a topic, with the addresses it can be reached on.</summary>
public sealed record FoundPeer(string PeerId, IReadOnlyList<string> Addresses);

/// <summary>A message from a peer: whatever JSON it sent.</summary>
public sealed record PeerMessage(string PeerId, JsonElement Message);

/// <summary>A peer the app connected to or heard from changed path.</summary>
public sealed record PeerPathChange(string PeerId, PeerPath Path);

/// <summary>A network that can be told to stop at once, without waiting (the app is exiting).</summary>
public interface IStoppable
{
    void StopNow();
}

/// <summary>The network refused or failed a request.</summary>
public sealed class PeerNetworkException(string message) : Exception(message);

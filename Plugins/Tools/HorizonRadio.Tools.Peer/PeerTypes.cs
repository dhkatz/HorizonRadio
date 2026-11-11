using System.Text.Json;

namespace HorizonRadio.Tools.Peer;

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

/// <summary>The helper refused or failed a command.</summary>
public sealed class PeerHelperException(string message) : Exception(message);

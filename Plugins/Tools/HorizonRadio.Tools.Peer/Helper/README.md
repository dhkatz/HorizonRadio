# horizon-peer

Horizon Radio's peer-to-peer helper (Go, [go-libp2p](https://github.com/libp2p/go-libp2p)).
It will carry multiplayer (friends, listening parties) and syncing your own devices,
without a server of ours: peers find each other through the public IPFS DHT (Amino),
meet through public IPFS relays, and hole punch (DCUtR) to a direct connection.

The app runs it as `horizon-peer serve`. A `probe` mode measures how often two machines
get a direct connection. Output is newline-delimited JSON.

## Serve

```powershell
horizon-peer serve -key <identity file> [-discovery amino,lan]
```

The app writes one JSON command per line to stdin and reads events from stdout. Each
command with an `id` gets `ok` or `error` back with that `id`.

| command | does |
|---|---|
| `{"cmd":"advertise","topic":"…"}` | makes this peer findable under a topic (`unadvertise` stops) |
| `{"id":"1","cmd":"find","topic":"…","timeout_seconds":60}` | `found` for each peer advertising it, then `find_done` (`stop` with the id ends it early) |
| `{"id":"2","cmd":"connect","peer":"12D3…","addrs":[…]}` | `connected` with the path; without addresses, those a find learned or a DHT lookup |
| `{"id":"3","cmd":"send","peer":"12D3…","message":{…}}` | sends any JSON to a connected peer; it arrives as a `message` event |
| `{"cmd":"disconnect","peer":"12D3…"}`, `{"cmd":"shutdown"}` | |

- **Topics** are how peers find each other (a pairing code, a device's ID). Only a hash of a
  topic goes on the network, so a peer can only be found by someone who knows its topic.
- **Discovery:** `amino` publishes and looks up provider records in the public IPFS DHT
  (it joins in the background, and `bootstrapped` says when). `lan` finds Horizon Radio peers
  on the local network with mDNS (`_horizon-radio._udp`), including other instances on
  the same machine, and asks each whether it advertises the topic.
- **Messages** to a peer arrive in the order they were sent: one stream per peer, a
  length-prefixed frame per message, at most 1 MiB each. Delivery is at most once: one in
  flight when a connection drops is lost, so the app's protocols catch up on reconnect. A
  stream moves off a relay once there's a direct connection, since the relay cuts it after
  two minutes.
- **`peer` events** report the path (`direct`, `relayed`, `none`) of every peer the app
  connected to or heard from, whenever it changes.
- The helper exits when stdin closes, so it never outlives the app.

## Tests

`go test ./...` runs peers on go-libp2p's simulated network (`marcopolo/simnet`): two
peers behind NAT meet through a relay, hole punch to a direct connection and exchange
messages. The LAN discovery and the commands are tested over loopback.

## Build

```powershell
cd Plugins/Tools/HorizonRadio.Tools.Peer/Helper
go build -trimpath -ldflags "-s -w" -o ../../../../build/peer/horizon-peer.exe .
```

`go.mod` pins the toolchain (`toolchain go1.27.3`); Go 1.26.0 crashed in go-libp2p's
dialer with a memory fault. `go build -tags debug` adds pprof on `127.0.0.1:6061`.

## Probing connectivity between two machines

Use two machines on **different networks** (e.g. home and a friend's, or a phone hotspot).

1. Machine A: `horizon-peer probe listen`. Note the `peer_id` in the `listening` line and
   wait for `advertised` (about a minute: the record is stored on the closest DHT peers).
2. Machine B: `horizon-peer probe dial <peer_id>`.

The dialer finds A, connects, waits for the connection to go direct, then holds it for
3 minutes (past the public relays' 2-minute limit) and prints a `result` line:

| field | meaning |
|---|---|
| `found_via` | `provider_record` (A's published DHT record) or `peer_lookup` |
| `first_path` / `final_path` | `direct` or `relayed` |
| `direct_after_seconds` | how long hole punching took, when it started relayed |
| `stayed_connected` | whether the connection outlived the hold |

Useful lines on either side: `reachability` (AutoNAT's verdict), `nat_type`
(`Endpoint Independent` punches well; `Endpoint Dependent` usually doesn't),
`addresses` (port mappings and IPv6 show up here), `hole_punch` (each DCUtR attempt,
including other IPFS nodes punching to us).

Flags: `-force-private` (act as if behind NAT, to exercise relays and hole punching even
when reachable), `-find record` (find A only through its published record), `-hold 3m`,
`-find-timeout 3m`, `-key <file>` (identity; default
`%LOCALAPPDATA%\HorizonRadio\peer\probe.key`; give two probes on one machine
different keys).

## Notes

- The DHT runs in client mode: the helper queries the network but doesn't serve it.
- `provide.go` replaces kad-dht's `Provide`, whose lookup has no per-peer timeout: one
  peer that accepts a stream and never answers stalls it until the caller's deadline.
- Relay service is off: the helper uses public relays but doesn't relay for others.

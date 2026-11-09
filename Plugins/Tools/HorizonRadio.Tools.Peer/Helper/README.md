# horizon-peer

Horizon Radio's peer-to-peer helper (Go, [go-libp2p](https://github.com/libp2p/go-libp2p)).
It will carry multiplayer (friends, listening parties) and syncing your own devices,
without a server of ours: peers find each other through the public IPFS DHT (Amino),
meet through public IPFS relays, and hole punch (DCUtR) to a direct connection.

For now it only **probes connectivity**, to measure how often two machines get a
direct connection before anything is built on it. Output is newline-delimited JSON.

## Build

```powershell
cd Plugins/Tools/HorizonRadio.Tools.Peer/Helper
go build -trimpath -ldflags "-s -w" -o ../../../../build/peer/horizon-peer.exe .
```

`go.mod` pins the toolchain (`toolchain go1.27.3`); Go 1.26.0 crashed in go-libp2p's
dialer with a memory fault. `go build -tags debug` adds pprof on `127.0.0.1:6061`.

## Probing two machines

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

package main

import (
	"context"
	"errors"
	"fmt"
	"math/rand/v2"
	"os"
	"path/filepath"
	"sync/atomic"
	"time"

	"github.com/libp2p/go-libp2p"
	dht "github.com/libp2p/go-libp2p-kad-dht"
	"github.com/libp2p/go-libp2p-kad-dht/dual"
	"github.com/libp2p/go-libp2p/core/crypto"
	"github.com/libp2p/go-libp2p/core/event"
	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/p2p/protocol/holepunch"
)

type nodeOptions struct {
	keyPath      string
	forcePrivate bool
	// amino joins the public IPFS network (the Amino DHT): it finds relays
	// among the DHT's peers when it's behind NAT. Without it the node only
	// reaches peers it's given addresses for (the LAN).
	amino  bool
	events *events
}

// node is a libp2p host that hole punches out of relayed connections, on the
// public IPFS network when amino is set.
type node struct {
	host   host.Host
	dht    *dual.DHT // nil without amino
	events *events

	// bootstrapped closes once the DHT can be searched (at once without amino);
	// bootErr says why it can't.
	bootstrapped chan struct{}
	bootErr      error
}

func defaultKeyPath() string {
	dir, err := os.UserCacheDir() // %LOCALAPPDATA% on Windows
	if err != nil {
		dir = "."
	}
	return filepath.Join(dir, "HorizonRadio", "peer", "probe.key")
}

// loadOrCreateKey keeps the probe's identity across runs, so its peer ID can be
// handed to the other machine once.
func loadOrCreateKey(path string) (crypto.PrivKey, error) {
	if b, err := os.ReadFile(path); err == nil {
		return crypto.UnmarshalPrivateKey(b)
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, err
	}
	sk, _, err := crypto.GenerateEd25519Key(nil)
	if err != nil {
		return nil, err
	}
	b, err := crypto.MarshalPrivateKey(sk)
	if err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, err
	}
	if err := os.WriteFile(path, b, 0o600); err != nil {
		return nil, err
	}
	return sk, nil
}

// startNode starts the host and, with amino, the DHT, which bootstraps in the
// background (see node.bootstrapped).
func startNode(ctx context.Context, opts nodeOptions) (*node, error) {
	if opts.events == nil {
		opts.events = stdout
	}
	ev := opts.events
	sk, err := loadOrCreateKey(opts.keyPath)
	if err != nil {
		return nil, fmt.Errorf("identity key: %w", err)
	}

	// AutoRelay asks for relay candidates before the DHT exists, so the peer
	// source reads it through a pointer set once it's up.
	var kdht atomic.Pointer[dual.DHT]
	var self atomic.Pointer[host.Host]
	relayCandidates := func(ctx context.Context, num int) <-chan peer.AddrInfo {
		out := make(chan peer.AddrInfo, num)
		go func() {
			defer close(out)
			d, h := kdht.Load(), self.Load()
			if d == nil || h == nil {
				return
			}
			ids := d.WAN.RoutingTable().ListPeers()
			rand.Shuffle(len(ids), func(i, j int) { ids[i], ids[j] = ids[j], ids[i] })
			for _, id := range ids {
				if num == 0 {
					return
				}
				info := (*h).Peerstore().PeerInfo(id)
				if len(info.Addrs) == 0 {
					continue
				}
				select {
				case out <- info:
					num--
				case <-ctx.Done():
					return
				}
			}
		}()
		return out
	}

	libp2pOpts := []libp2p.Option{
		libp2p.Identity(sk),
		libp2p.ListenAddrStrings(
			"/ip4/0.0.0.0/udp/0/quic-v1",
			"/ip6/::/udp/0/quic-v1",
			"/ip4/0.0.0.0/tcp/0",
			"/ip6/::/tcp/0",
		),
		libp2p.NATPortMap(),
		libp2p.EnableAutoNATv2(),
		libp2p.EnableHolePunching(holepunch.WithTracer(holePunchTracer{ev})),
	}
	if opts.amino {
		libp2pOpts = append(libp2pOpts, libp2p.EnableAutoRelayWithPeerSource(relayCandidates))
	}
	if opts.forcePrivate {
		// Act as if behind NAT even when reachable, to exercise relay + hole punching.
		libp2pOpts = append(libp2pOpts, libp2p.ForceReachabilityPrivate())
	}

	h, err := libp2p.New(libp2pOpts...)
	if err != nil {
		return nil, fmt.Errorf("libp2p host: %w", err)
	}
	self.Store(&h)
	watchHostEvents(ctx, h, ev)

	n := &node{host: h, events: ev, bootstrapped: make(chan struct{})}
	if !opts.amino {
		close(n.bootstrapped)
		ev.emit("started", map[string]any{"peer_id": h.ID().String(), "listen": addrStrings(h.Network().ListenAddresses())})
		return n, nil
	}

	d, err := dual.New(h,
		dual.DHTOption(
			dht.Mode(dht.ModeClient),
			dht.BootstrapPeers(dht.GetDefaultBootstrapPeerAddrInfos()...),
		),
	)
	if err != nil {
		_ = h.Close()
		return nil, fmt.Errorf("dht: %w", err)
	}
	kdht.Store(d)
	n.dht = d

	ev.emit("started", map[string]any{"peer_id": h.ID().String(), "listen": addrStrings(h.Network().ListenAddresses())})
	go func() {
		defer close(n.bootstrapped)
		n.bootErr = bootstrap(ctx, h, d, ev)
	}()
	return n, nil
}

// waitBootstrapped waits until the DHT can be searched.
func (n *node) waitBootstrapped(ctx context.Context) error {
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-n.bootstrapped:
		return n.bootErr
	}
}

// bootstrap connects to the public bootstrap peers and waits until the DHT's
// routing table has enough peers to search with.
func bootstrap(ctx context.Context, h host.Host, d *dual.DHT, ev *events) error {
	start := time.Now()
	connected := 0
	for _, info := range dht.GetDefaultBootstrapPeerAddrInfos() {
		cctx, cancel := context.WithTimeout(ctx, 10*time.Second)
		if err := h.Connect(cctx, info); err == nil {
			connected++
		}
		cancel()
	}
	if err := d.Bootstrap(ctx); err != nil {
		return fmt.Errorf("dht bootstrap: %w", err)
	}
	deadline := time.Now().Add(60 * time.Second)
	for d.WAN.RoutingTable().Size() < 20 && time.Now().Before(deadline) {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
	ev.emit("bootstrapped", map[string]any{
		"bootstrap_peers_connected": connected,
		"routing_table":             d.WAN.RoutingTable().Size(),
		"seconds":                   time.Since(start).Seconds(),
	})
	if d.WAN.RoutingTable().Size() == 0 {
		return errors.New("no DHT peers: is UDP/TCP outbound blocked?")
	}
	return nil
}

func (n *node) close() {
	if n.dht != nil {
		_ = n.dht.Close()
	}
	_ = n.host.Close()
}

// watchHostEvents reports what the host learns about its own reachability:
// AutoNAT's verdict, the NAT's mapping behaviour, and the addresses (public,
// port-mapped, IPv6, relay) it can be reached on.
func watchHostEvents(ctx context.Context, h host.Host, ev *events) {
	sub, err := h.EventBus().Subscribe([]any{
		new(event.EvtLocalReachabilityChanged),
		new(event.EvtNATDeviceTypeChanged),
		new(event.EvtLocalAddressesUpdated),
		new(event.EvtAutoRelayAddrsUpdated),
	})
	if err != nil {
		ev.emit("error", map[string]any{"error": err.Error()})
		return
	}
	go func() {
		defer sub.Close()
		for {
			select {
			case <-ctx.Done():
				return
			case e, ok := <-sub.Out():
				if !ok {
					return
				}
				switch x := e.(type) {
				case event.EvtLocalReachabilityChanged:
					ev.emit("reachability", map[string]any{"reachability": x.Reachability.String()})
				case event.EvtNATDeviceTypeChanged:
					ev.emit("nat_type", map[string]any{"protocol": x.TransportProtocol.String(), "type": x.NatDeviceType.String()})
				case event.EvtLocalAddressesUpdated:
					ev.emit("addresses", map[string]any{"addrs": addrStrings(h.Addrs())})
				case event.EvtAutoRelayAddrsUpdated:
					ev.emit("relay_addresses", map[string]any{"addrs": addrStrings(x.RelayAddrs)})
				}
			}
		}
	}()
}

type holePunchTracer struct{ events *events }

func (t holePunchTracer) Trace(evt *holepunch.Event) {
	t.events.emit("hole_punch", map[string]any{"remote": evt.Remote.String(), "type": evt.Type, "detail": evt.Evt})
}

package main

import (
	"context"
	"slices"
	"time"

	"github.com/ipfs/go-cid"
	dht "github.com/libp2p/go-libp2p-kad-dht"
	"github.com/libp2p/go-libp2p/core/event"
	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/multiformats/go-multihash"
)

// A topic is how peers find each other: a peer advertises the topics it
// answers to (a pairing code, a device's own ID), and others look them up. Only
// a hash of the topic goes on the network, so finding a peer through a topic
// takes knowing the topic.
func topicKey(topic string) cid.Cid {
	mh, err := multihash.Sum([]byte("horizon-radio/topic/"+topic), multihash.SHA2_256, -1)
	if err != nil {
		panic(err) // SHA2-256 is always available
	}
	return cid.NewCidV1(cid.Raw, mh)
}

// discovery is one way of advertising and finding topics.
type discovery interface {
	name() string
	// advertise makes this peer findable under topic until ctx ends.
	advertise(ctx context.Context, topic string)
	// find reports peers advertising topic (repeats included) until ctx ends.
	find(ctx context.Context, topic string, found func(peer.AddrInfo))
}

// aminoDiscovery uses provider records in the public IPFS DHT. A peer behind
// NAT isn't in anyone's routing table, so a plain peer lookup rarely finds it;
// its provider record carries its relay addresses instead.
type aminoDiscovery struct {
	n *node
}

func (aminoDiscovery) name() string { return "amino" }

// advertise (re)publishes the provider record when the addresses others can
// reach us on change (a relay reservation, a port mapping), and every 10
// minutes, since a record's addresses expire long before the record.
//
// libp2p's RoutingDiscovery does the same, but cuts every publish off at 60 s,
// and a provide on Amino often needs longer.
func (a aminoDiscovery) advertise(ctx context.Context, topic string) {
	ev := a.n.events
	if a.n.waitBootstrapped(ctx) != nil {
		return
	}
	sub, err := a.n.host.EventBus().Subscribe(new(event.EvtLocalAddressesUpdated))
	if err != nil {
		ev.emit("error", map[string]any{"error": err.Error()})
		return
	}
	defer sub.Close()
	key := topicKey(topic)

	// Give AutoNAT and AutoRelay a moment, so the first record has useful addresses.
	timer := time.NewTimer(10 * time.Second)
	defer timer.Stop()
	var published []string
	for {
		select {
		case <-ctx.Done():
			return
		case <-sub.Out():
			if !slices.Equal(publicAddrs(a.n.host), published) {
				// Debounce: addresses tend to change several times in a row.
				timer.Reset(5 * time.Second)
			}
			continue
		case <-timer.C:
		}
		start := time.Now()
		addrs := publicAddrs(a.n.host)
		stored, err := provide(ctx, a.n.dht.WAN, key)
		if ctx.Err() != nil {
			return
		}
		if err != nil {
			ev.emit("advertise_failed", map[string]any{"topic": topic, "via": "amino", "error": err.Error(), "seconds": time.Since(start).Seconds()})
			timer.Reset(30 * time.Second)
			continue
		}
		published = addrs
		ev.emit("advertised", map[string]any{"topic": topic, "via": "amino", "addrs": addrs, "stored_on_peers": stored, "seconds": time.Since(start).Seconds()})
		timer.Reset(10 * time.Minute)
	}
}

func (a aminoDiscovery) find(ctx context.Context, topic string, found func(peer.AddrInfo)) {
	if a.n.waitBootstrapped(ctx) != nil {
		return
	}
	findProviders(ctx, a.n.dht.WAN, topicKey(topic), found)
}

// findProviders searches for key's providers every few seconds until ctx ends.
// The public DHT only: the LAN one would answer for a peer on this machine.
func findProviders(ctx context.Context, d *dht.IpfsDHT, key cid.Cid, found func(peer.AddrInfo)) {
	for ctx.Err() == nil {
		for p := range d.FindProvidersAsync(ctx, key, 20) {
			if len(p.Addrs) > 0 {
				found(p)
			}
		}
		sleep(ctx, 5*time.Second)
	}
}

// selfFilter drops this host from what a discovery finds.
func selfFilter(h host.Host, found func(peer.AddrInfo)) func(peer.AddrInfo) {
	return func(p peer.AddrInfo) {
		if p.ID != h.ID() {
			found(p)
		}
	}
}

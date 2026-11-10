package main

import (
	"bufio"
	"context"
	"encoding/hex"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/peerstore"
	"github.com/libp2p/go-libp2p/core/protocol"
	"github.com/libp2p/go-libp2p/p2p/discovery/mdns"
)

const (
	// lanService is the mDNS service Horizon Radio peers announce themselves under.
	lanService = "_horizon-radio._udp"
	// topicProtocol asks a peer whether it advertises a topic (by the topic's hash).
	topicProtocol = protocol.ID("/horizon-radio/topic/1.0.0")
)

// lanDiscovery finds peers on the local network, or other instances on this
// machine, without the Internet: mDNS finds the Horizon Radio peers nearby, and
// each is asked whether it advertises the topic.
type lanDiscovery struct {
	h    host.Host
	mdns mdns.Service

	mu     sync.Mutex
	topics map[string]int // topic hash → advertisements
	nearby map[peer.ID]peer.AddrInfo
}

func newLANDiscovery(h host.Host, withMDNS bool) (*lanDiscovery, error) {
	d := &lanDiscovery{h: h, topics: map[string]int{}, nearby: map[peer.ID]peer.AddrInfo{}}
	h.SetStreamHandler(topicProtocol, d.answer)
	if withMDNS {
		d.mdns = mdns.NewMdnsService(h, lanService, d)
		if err := d.mdns.Start(); err != nil {
			return nil, fmt.Errorf("mdns: %w", err)
		}
	}
	return d, nil
}

func (*lanDiscovery) name() string { return "lan" }

func (d *lanDiscovery) close() {
	if d.mdns != nil {
		_ = d.mdns.Close()
	}
	d.h.RemoveStreamHandler(topicProtocol)
}

// HandlePeerFound is mDNS reporting a peer nearby.
func (d *lanDiscovery) HandlePeerFound(p peer.AddrInfo) {
	if p.ID == d.h.ID() {
		return
	}
	d.h.Peerstore().AddAddrs(p.ID, p.Addrs, peerstore.TempAddrTTL)
	d.mu.Lock()
	d.nearby[p.ID] = p
	d.mu.Unlock()
}

func topicHash(topic string) string { return hex.EncodeToString(topicKey(topic).Hash()) }

func (d *lanDiscovery) advertise(ctx context.Context, topic string) {
	key := topicHash(topic)
	d.mu.Lock()
	d.topics[key]++
	d.mu.Unlock()
	<-ctx.Done()
	d.mu.Lock()
	if d.topics[key]--; d.topics[key] <= 0 {
		delete(d.topics, key)
	}
	d.mu.Unlock()
}

// find asks every peer nearby about the topic every couple of seconds; a peer
// that said yes isn't asked again.
func (d *lanDiscovery) find(ctx context.Context, topic string, found func(peer.AddrInfo)) {
	key := topicHash(topic)
	answered := map[peer.ID]bool{}
	for ctx.Err() == nil {
		d.mu.Lock()
		var ask []peer.AddrInfo
		for id, p := range d.nearby {
			if !answered[id] {
				ask = append(ask, p)
			}
		}
		d.mu.Unlock()

		var wg sync.WaitGroup
		var foundMu sync.Mutex
		for _, p := range ask {
			wg.Go(func() {
				if d.ask(ctx, p, key) {
					foundMu.Lock()
					answered[p.ID] = true
					foundMu.Unlock()
					found(peer.AddrInfo{ID: p.ID, Addrs: d.h.Peerstore().Addrs(p.ID)})
				}
			})
		}
		wg.Wait()
		sleep(ctx, 2*time.Second)
	}
}

func (d *lanDiscovery) ask(ctx context.Context, p peer.AddrInfo, key string) bool {
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err := d.h.Connect(ctx, p); err != nil {
		return false
	}
	s, err := d.h.NewStream(ctx, p.ID, topicProtocol)
	if err != nil {
		return false
	}
	defer s.Close()
	_ = s.SetDeadline(time.Now().Add(10 * time.Second))
	if _, err := fmt.Fprintf(s, "%s\n", key); err != nil {
		return false
	}
	reply, err := bufio.NewReader(s).ReadString('\n')
	return err == nil && strings.TrimSpace(reply) == "yes"
}

func (d *lanDiscovery) answer(s network.Stream) {
	defer s.Close()
	_ = s.SetDeadline(time.Now().Add(10 * time.Second))
	line, err := bufio.NewReader(s).ReadString('\n')
	if err != nil {
		return
	}
	d.mu.Lock()
	yes := d.topics[strings.TrimSpace(line)] > 0
	d.mu.Unlock()
	if yes {
		_, _ = fmt.Fprint(s, "yes\n")
	} else {
		_, _ = fmt.Fprint(s, "no\n")
	}
}

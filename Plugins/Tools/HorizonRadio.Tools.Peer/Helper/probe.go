package main

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"slices"
	"sync"
	"time"

	"github.com/ipfs/go-cid"
	"github.com/libp2p/go-libp2p/core/event"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/protocol"
	"github.com/libp2p/go-libp2p/p2p/protocol/ping"
	"github.com/multiformats/go-multihash"
)

const probeProtocol = protocol.ID("/horizon-radio/probe/1.0.0")

// probeKey is where a listener announces itself in the DHT. A peer behind NAT
// isn't in anyone's routing table, so a plain peer lookup rarely finds it; its
// provider record carries its relay addresses instead.
//
// libp2p's RoutingDiscovery does the same, but cuts every publish off at 60 s,
// and a provide on Amino often needs longer.
func probeKey(id peer.ID) cid.Cid {
	mh, err := multihash.Sum([]byte("horizon-radio/probe/"+id.String()), multihash.SHA2_256, -1)
	if err != nil {
		panic(err) // SHA2-256 is always available
	}
	return cid.NewCidV1(cid.Raw, mh)
}

// listen announces this peer and reports every probe that reaches it, and how.
func listen(ctx context.Context, n *node) error {
	h := n.host
	var mu sync.Mutex
	visitors := map[peer.ID]string{} // peer → last reported path

	h.SetStreamHandler(probeProtocol, func(s network.Stream) {
		defer s.Close()
		remote := s.Conn().RemotePeer()
		line, err := bufio.NewReader(s).ReadString('\n')
		if err != nil {
			return
		}
		mu.Lock()
		_, known := visitors[remote]
		visitors[remote] = describeConn(s.Conn()).Path
		mu.Unlock()
		if !known {
			emit("visitor", map[string]any{"peer": remote.String(), "hello": line[:len(line)-1], "conns": describeConns(h, remote)})
		}
		_, _ = fmt.Fprintf(s, "hello from %s\n", h.ID())
	})

	// Report path changes for each visitor: relayed → direct is a hole punch.
	go func() {
		t := time.NewTicker(time.Second)
		defer t.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-t.C:
			}
			mu.Lock()
			for id, last := range visitors {
				if now := bestPath(h, id); now != last {
					visitors[id] = now
					emit("visitor_path", map[string]any{"peer": id.String(), "from": last, "to": now, "conns": describeConns(h, id)})
				}
			}
			mu.Unlock()
		}
	}()

	emit("listening", map[string]any{
		"peer_id": h.ID().String(),
		"hint":    "on the other machine run: horizon-peer probe dial " + h.ID().String(),
	})
	return advertiseLoop(ctx, n)
}

// advertiseLoop (re)publishes the provider record when the addresses others
// can reach us on change (a relay reservation, a port mapping), and every
// 10 minutes.
func advertiseLoop(ctx context.Context, n *node) error {
	sub, err := n.host.EventBus().Subscribe(new(event.EvtLocalAddressesUpdated))
	if err != nil {
		return err
	}
	defer sub.Close()
	key := probeKey(n.host.ID())

	// Give AutoNAT and AutoRelay a moment, so the first record has useful addresses.
	timer := time.NewTimer(10 * time.Second)
	defer timer.Stop()
	var published []string
	for {
		select {
		case <-ctx.Done():
			return nil
		case <-sub.Out():
			if !slices.Equal(publicAddrs(n.host), published) {
				// Debounce: addresses tend to change several times in a row.
				timer.Reset(5 * time.Second)
			}
			continue
		case <-timer.C:
		}
		start := time.Now()
		addrs := publicAddrs(n.host)
		stored, err := provide(ctx, n.dht.WAN, key)
		if ctx.Err() != nil {
			return nil
		}
		if err != nil {
			emit("advertise_failed", map[string]any{"error": err.Error(), "seconds": time.Since(start).Seconds()})
			timer.Reset(30 * time.Second)
			continue
		}
		published = addrs
		emit("advertised", map[string]any{"addrs": addrs, "stored_on_peers": stored, "seconds": time.Since(start).Seconds()})
		timer.Reset(10 * time.Minute)
	}
}

type dialOptions struct {
	findTimeout time.Duration
	hold        time.Duration
	recordOnly  bool // find only through the provider record (skip the peer lookup)
}

type dialResult struct {
	FoundVia        string  `json:"found_via"`
	FoundSeconds    float64 `json:"found_seconds"`
	FirstPath       string  `json:"first_path"`
	FinalPath       string  `json:"final_path"`
	DirectAfter     float64 `json:"direct_after_seconds,omitempty"`
	MedianRTTms     float64 `json:"median_rtt_ms"`
	StayedConnected bool    `json:"stayed_connected"`
	HeldSeconds     float64 `json:"held_seconds"`
}

// dial finds a listening probe by peer ID, connects, waits for the connection
// to go direct, then holds it past the public relays' 2-minute limit.
func dial(ctx context.Context, n *node, target peer.ID, opts dialOptions) error {
	h := n.host
	var res dialResult

	start := time.Now()
	info, via, err := findPeer(ctx, n, target, opts.findTimeout, opts.recordOnly)
	if err != nil {
		return err
	}
	res.FoundVia, res.FoundSeconds = via, time.Since(start).Seconds()
	emit("found", map[string]any{"via": via, "addrs": addrStrings(info.Addrs), "seconds": res.FoundSeconds})

	cctx, cancel := context.WithTimeout(ctx, time.Minute)
	err = h.Connect(cctx, info)
	cancel()
	if err != nil {
		return fmt.Errorf("connect: %w", err)
	}
	connected := time.Now()
	res.FirstPath = bestPath(h, target)
	emit("connected", map[string]any{"path": res.FirstPath, "conns": describeConns(h, target)})

	if err := hello(ctx, n, target); err != nil {
		return fmt.Errorf("probe protocol: %w", err)
	}

	// The listener starts hole punching (DCUtR) when it sees our relayed
	// connection; wait for the direct one.
	if res.FirstPath != "direct" {
		deadline := time.Now().Add(time.Minute)
		for bestPath(h, target) != "direct" && time.Now().Before(deadline) && ctx.Err() == nil {
			time.Sleep(250 * time.Millisecond)
		}
		if bestPath(h, target) == "direct" {
			res.DirectAfter = time.Since(connected).Seconds()
			emit("went_direct", map[string]any{"seconds": res.DirectAfter, "conns": describeConns(h, target)})
		} else {
			emit("still_relayed", map[string]any{"conns": describeConns(h, target)})
		}
	}

	res.MedianRTTms = medianRTT(ctx, n, target, 5)
	emit("rtt", map[string]any{"median_ms": res.MedianRTTms, "path": bestPath(h, target)})

	// Hold the connection: a relayed one is cut at 2 minutes / 128 KB.
	res.StayedConnected = true
	holdStart := time.Now()
	for time.Since(holdStart) < opts.hold && ctx.Err() == nil {
		select {
		case <-ctx.Done():
		case <-time.After(15 * time.Second):
		}
		path := bestPath(h, target)
		rtt := medianRTT(ctx, n, target, 1)
		emit("holding", map[string]any{"elapsed_seconds": int(time.Since(holdStart).Seconds()), "path": path, "rtt_ms": rtt})
		if path == "none" || rtt < 0 {
			res.StayedConnected = false
			break
		}
	}
	res.HeldSeconds = time.Since(holdStart).Seconds()
	res.FinalPath = bestPath(h, target)
	emit("result", map[string]any{"result": res})
	return nil
}

// findPeer looks the target up two ways at once: its provider record (which
// carries relay addresses for a peer behind NAT) and a plain DHT peer lookup.
func findPeer(ctx context.Context, n *node, target peer.ID, timeout time.Duration, recordOnly bool) (peer.AddrInfo, string, error) {
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	type found struct {
		info peer.AddrInfo
		via  string
	}
	results := make(chan found, 2)
	key := probeKey(target)

	go func() {
		for ctx.Err() == nil {
			// The public DHT only: the LAN one would answer for a peer on this machine.
			for p := range n.dht.WAN.FindProvidersAsync(ctx, key, 20) {
				if p.ID == target && len(p.Addrs) > 0 {
					results <- found{p, "provider_record"}
					return
				}
			}
			sleep(ctx, 5*time.Second)
		}
	}()
	if !recordOnly {
		go func() {
			for ctx.Err() == nil {
				if p, err := n.dht.FindPeer(ctx, target); err == nil && len(p.Addrs) > 0 {
					results <- found{p, "peer_lookup"}
					return
				}
				sleep(ctx, 5*time.Second)
			}
		}()
	}

	emit("finding", map[string]any{"peer": target.String(), "timeout_seconds": timeout.Seconds()})
	select {
	case f := <-results:
		return f.info, f.via, nil
	case <-ctx.Done():
		return peer.AddrInfo{}, "", errors.New("peer not found in the DHT: is the listener running and advertised?")
	}
}

func hello(ctx context.Context, n *node, target peer.ID) error {
	sctx, cancel := context.WithTimeout(network.WithAllowLimitedConn(ctx, "probe"), 30*time.Second)
	defer cancel()
	s, err := n.host.NewStream(sctx, target, probeProtocol)
	if err != nil {
		return err
	}
	defer s.Close()
	if _, err := fmt.Fprintf(s, "hello from %s\n", n.host.ID()); err != nil {
		return err
	}
	reply, err := bufio.NewReader(s).ReadString('\n')
	if err != nil {
		return err
	}
	emit("hello", map[string]any{"reply": reply[:len(reply)-1], "over": describeConn(s.Conn())})
	return nil
}

// medianRTT pings the peer count times; -1 when every ping failed.
func medianRTT(ctx context.Context, n *node, target peer.ID, count int) float64 {
	pctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	var rtts []float64
	results := ping.Ping(pctx, n.host, target)
	for range count {
		r, ok := <-results
		if !ok || r.Error != nil {
			break
		}
		rtts = append(rtts, float64(r.RTT.Microseconds())/1000)
	}
	if len(rtts) == 0 {
		return -1
	}
	slices.Sort(rtts)
	return rtts[len(rtts)/2]
}

func sleep(ctx context.Context, d time.Duration) {
	select {
	case <-ctx.Done():
	case <-time.After(d):
	}
}

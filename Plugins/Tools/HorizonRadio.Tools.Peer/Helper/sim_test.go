package main

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"slices"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/libp2p/go-libp2p"
	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	relayv2 "github.com/libp2p/go-libp2p/p2p/protocol/circuitv2/relay"
	"github.com/libp2p/go-libp2p/p2p/protocol/holepunch"
	"github.com/libp2p/go-libp2p/p2p/transport/quicreuse"
	"github.com/marcopolo/simnet"
	ma "github.com/multiformats/go-multiaddr"
	"github.com/stretchr/testify/require"
	"go.uber.org/fx"
)

// The tests run peers on go-libp2p's simulated network: QUIC over in-memory
// UDP, behind a router that drops unsolicited packets to a peer that isn't
// publicly reachable (a NAT), so the relay and hole punching paths run as they
// would on the Internet.

type sourceIP struct{ ip atomic.Pointer[net.IP] }

func (s *sourceIP) PreferredSourceIPForDestination(*net.UDPAddr) (net.IP, error) {
	return *s.ip.Load(), nil
}

func simQUIC(public bool, router *simnet.SimpleFirewallRouter) libp2p.Option {
	src := &sourceIP{}
	return libp2p.QUICReuse(
		quicreuse.NewConnManager,
		quicreuse.OverrideSourceIPSelector(func() (quicreuse.SourceIPSelector, error) { return src, nil }),
		quicreuse.OverrideListenUDP(func(_ string, addr *net.UDPAddr) (net.PacketConn, error) {
			src.ip.Store(&addr.IP)
			if public {
				router.SetAddrPubliclyReachable(addr)
			}
			c := simnet.NewSimConn(addr)
			c.SetUpPacketReceiver(router)
			router.AddNode(addr, c)
			return c, nil
		}))
}

type simNet struct {
	t      *testing.T
	router *simnet.SimpleFirewallRouter
	relay  host.Host
}

func newSimNet(t *testing.T) *simNet {
	n := &simNet{t: t, router: &simnet.SimpleFirewallRouter{}}
	n.relay = n.host("/ip4/1.2.0.1/udp/8000/quic-v1", true,
		libp2p.DisableRelay(),
		libp2p.WithFxOption(fx.Invoke(func(h host.Host) {
			_, err := relayv2.New(h)
			require.NoError(t, err)
		})))
	return n
}

func (n *simNet) host(listen string, public bool, opts ...libp2p.Option) host.Host {
	n.t.Helper()
	h, err := libp2p.New(append([]libp2p.Option{
		simQUIC(public, n.router),
		libp2p.ListenAddrs(ma.StringCast(listen)),
		libp2p.ResourceManager(&network.NullResourceManager{}),
	}, opts...)...)
	require.NoError(n.t, err)
	n.t.Cleanup(func() { _ = h.Close() })
	return h
}

// natPeer is a peer behind NAT that keeps a reservation on the relay, as one
// on the Internet does through AutoRelay.
func (n *simNet) natPeer(listen string) host.Host {
	relay := peer.AddrInfo{ID: n.relay.ID(), Addrs: n.relay.Addrs()}
	return n.host(listen, false,
		libp2p.EnableRelay(),
		libp2p.EnableAutoRelayWithStaticRelays([]peer.AddrInfo{relay}),
		libp2p.EnableHolePunching(holepunch.DirectDialTimeout(100*time.Millisecond)),
		libp2p.ForceReachabilityPrivate(),
	)
}

// holePunchReady waits until each host's hole punching service is on: it
// waits for the host to learn its public address, which a peer on the Internet
// has long before anyone dials it.
func holePunchReady(t *testing.T, hs ...host.Host) {
	t.Helper()
	for _, h := range hs {
		require.Eventually(t, func() bool {
			return slices.Contains(h.Mux().Protocols(), holepunch.Protocol)
		}, 10*time.Second, 50*time.Millisecond, "hole punching never started")
	}
}

// relayAddrs waits for h's reservation and returns its relay addresses.
func relayAddrs(t *testing.T, h host.Host) []ma.Multiaddr {
	t.Helper()
	var addrs []ma.Multiaddr
	require.Eventually(t, func() bool {
		addrs = nil
		for _, a := range h.Addrs() {
			if isRelayed(a) {
				addrs = append(addrs, a)
			}
		}
		return len(addrs) > 0
	}, 10*time.Second, 50*time.Millisecond, "no relay reservation")
	return addrs
}

// memoryDiscovery is a topic registry shared by the peers of one test.
type memoryDiscovery struct {
	mu     sync.Mutex
	topics map[string][]host.Host
	// addrs picks what a find reports for a host (default: all its addresses).
	addrs func(host.Host) []ma.Multiaddr
}

func (*memoryDiscovery) name() string { return "memory" }

// on returns the discovery as seen from h (advertising adds h).
func (m *memoryDiscovery) on(h host.Host) discovery { return memoryOn{m, h} }

type memoryOn struct {
	m *memoryDiscovery
	h host.Host
}

func (memoryOn) name() string { return "memory" }

func (d memoryOn) advertise(ctx context.Context, topic string) {
	d.m.mu.Lock()
	if d.m.topics == nil {
		d.m.topics = map[string][]host.Host{}
	}
	d.m.topics[topic] = append(d.m.topics[topic], d.h)
	d.m.mu.Unlock()
	<-ctx.Done()
	d.m.mu.Lock()
	defer d.m.mu.Unlock()
	hs := d.m.topics[topic]
	for i, h := range hs {
		if h == d.h {
			d.m.topics[topic] = append(hs[:i], hs[i+1:]...)
			break
		}
	}
}

func (d memoryOn) find(ctx context.Context, topic string, found func(peer.AddrInfo)) {
	for ctx.Err() == nil {
		d.m.mu.Lock()
		hs := append([]host.Host(nil), d.m.topics[topic]...)
		d.m.mu.Unlock()
		for _, h := range hs {
			addrs := h.Addrs()
			if d.m.addrs != nil {
				addrs = d.m.addrs(h)
			}
			found(peer.AddrInfo{ID: h.ID(), Addrs: addrs})
		}
		sleep(ctx, 50*time.Millisecond)
	}
}

// recorder collects a service's events.
type recorder struct {
	mu  sync.Mutex
	all []map[string]any
	new chan struct{}
}

func newRecorder() *recorder { return &recorder{new: make(chan struct{}, 1)} }

func (r *recorder) Write(p []byte) (int, error) {
	var ev map[string]any
	if err := json.Unmarshal(p, &ev); err != nil {
		return 0, err
	}
	r.mu.Lock()
	r.all = append(r.all, ev)
	r.mu.Unlock()
	select {
	case r.new <- struct{}{}:
	default:
	}
	return len(p), nil
}

// wait returns the first event (from the start) that matches, or fails the test.
func (r *recorder) wait(t *testing.T, timeout time.Duration, match func(map[string]any) bool) map[string]any {
	t.Helper()
	deadline := time.After(timeout)
	for {
		r.mu.Lock()
		for _, ev := range r.all {
			if match(ev) {
				r.mu.Unlock()
				return ev
			}
		}
		r.mu.Unlock()
		select {
		case <-r.new:
		case <-deadline:
			r.mu.Lock()
			defer r.mu.Unlock()
			t.Fatalf("no matching event in %s; got %v", timeout, r.all)
			return nil
		}
	}
}

func (r *recorder) matching(match func(map[string]any) bool) []map[string]any {
	r.mu.Lock()
	defer r.mu.Unlock()
	var out []map[string]any
	for _, ev := range r.all {
		if match(ev) {
			out = append(out, ev)
		}
	}
	return out
}

func is(event string, fields ...string) func(map[string]any) bool {
	return func(ev map[string]any) bool {
		if ev["event"] != event {
			return false
		}
		for i := 0; i+1 < len(fields); i += 2 {
			if ev[fields[i]] != fields[i+1] {
				return false
			}
		}
		return true
	}
}

// testService runs a service over h, taking commands through the returned function.
type testService struct {
	*service
	rec *recorder
	in  chan<- command
}

func startService(t *testing.T, h host.Host, disc ...discovery) *testService {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	rec := newRecorder()
	s := newService(ctx, h, disc, &events{w: rec})
	r, w := io.Pipe()
	done := make(chan struct{})
	go func() {
		defer close(done)
		_ = s.run(ctx, r)
	}()
	t.Cleanup(func() {
		cancel()
		_ = w.Close()
		<-done
	})
	in := make(chan command)
	go func() {
		for c := range in {
			b, _ := json.Marshal(c)
			_, _ = w.Write(append(b, '\n'))
		}
	}()
	t.Cleanup(func() { close(in) })
	return &testService{s, rec, in}
}

func (s *testService) do(c command) { s.in <- c }

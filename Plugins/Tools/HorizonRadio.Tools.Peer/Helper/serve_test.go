package main

import (
	"encoding/json"
	"fmt"
	"testing"
	"time"

	"github.com/libp2p/go-libp2p"
	"github.com/libp2p/go-libp2p/core/crypto"
	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/peer"
	ma "github.com/multiformats/go-multiaddr"
	"github.com/stretchr/testify/require"
)

// Two peers behind NAT find each other through a topic, meet through a relay,
// hole punch to a direct connection, and exchange messages in order.
func TestPeersBehindNATFindEachOtherAndGoDirect(t *testing.T) {
	net := newSimNet(t)
	a := net.natPeer("/ip4/2.2.0.1/udp/8000/quic-v1")
	b := net.natPeer("/ip4/2.2.0.2/udp/8001/quic-v1")
	relayAddrs(t, b)
	holePunchReady(t, a, b)

	// Like a provider record of a peer behind NAT: only its relay addresses.
	reg := &memoryDiscovery{addrs: func(h host.Host) []ma.Multiaddr {
		var out []ma.Multiaddr
		for _, addr := range h.Addrs() {
			if isRelayed(addr) {
				out = append(out, addr)
			}
		}
		return out
	}}
	sa := startService(t, a, reg.on(a))
	sb := startService(t, b, reg.on(b))

	sb.do(command{ID: "adv", Cmd: "advertise", Topic: "pair/4F7K"})
	sb.rec.wait(t, 5*time.Second, is("ok", "id", "adv"))
	sa.do(command{ID: "f", Cmd: "find", Topic: "pair/4F7K", TimeoutSeconds: 10})
	found := sa.rec.wait(t, 10*time.Second, is("found", "id", "f", "peer", b.ID().String()))
	sa.do(command{ID: "f", Cmd: "stop"})
	sa.rec.wait(t, 5*time.Second, is("find_done", "id", "f"))

	var addrs []string
	for _, addr := range found["addrs"].([]any) {
		addrs = append(addrs, addr.(string))
	}
	sa.do(command{ID: "c", Cmd: "connect", Peer: b.ID().String(), Addrs: addrs})
	sa.rec.wait(t, 10*time.Second, is("connected", "id", "c"))

	// The first message goes over the relay if the hole punch hasn't finished yet.
	sa.do(command{ID: "first", Cmd: "send", Peer: b.ID().String(), Message: json.RawMessage(`{"n":0}`)})
	sa.rec.wait(t, 10*time.Second, is("ok", "id", "first"))
	sa.rec.wait(t, 10*time.Second, is("peer", "peer", b.ID().String(), "path", "direct"))

	const count = 30
	for i := 1; i <= count; i++ {
		sa.do(command{Cmd: "send", Peer: b.ID().String(), Message: json.RawMessage(fmt.Sprintf(`{"n":%d}`, i))})
	}
	sb.rec.wait(t, 10*time.Second, func(ev map[string]any) bool {
		return ev["event"] == "message" && ev["message"].(map[string]any)["n"] == float64(count)
	})
	got := sb.rec.matching(is("message", "peer", a.ID().String()))
	require.Len(t, got, count+1)
	for i, ev := range got {
		require.Equal(t, float64(i), ev["message"].(map[string]any)["n"], "messages arrive in order")
	}
	require.Empty(t, sa.rec.matching(is("error")))

	// The stream moved off the relay, which would cut it after 2 minutes.
	sa.msg.mu.Lock()
	out := sa.msg.outs[b.ID()]
	sa.msg.mu.Unlock()
	out.mu.Lock()
	require.False(t, out.s.Conn().Stat().Limited, "messages go over the direct connection")
	out.mu.Unlock()

	// And back: b learned of a from its messages.
	sb.rec.wait(t, 5*time.Second, is("peer", "peer", a.ID().String(), "path", "direct"))
	sb.do(command{ID: "back", Cmd: "send", Peer: a.ID().String(), Message: json.RawMessage(`{"hello":"a"}`)})
	ev := sa.rec.wait(t, 5*time.Second, is("message", "peer", b.ID().String()))
	require.Equal(t, "a", ev["message"].(map[string]any)["hello"])
}

func loopbackHost(t *testing.T) host.Host {
	t.Helper()
	h, err := libp2p.New(libp2p.ListenAddrStrings("/ip4/127.0.0.1/tcp/0"))
	require.NoError(t, err)
	t.Cleanup(func() { _ = h.Close() })
	return h
}

// The LAN discovery asks the peers mDNS found (here: told about directly)
// whether they advertise a topic.
func TestLANDiscoveryFindsOnlyAdvertisedTopics(t *testing.T) {
	a, b := loopbackHost(t), loopbackHost(t)
	la, err := newLANDiscovery(a, false)
	require.NoError(t, err)
	lb, err := newLANDiscovery(b, false)
	require.NoError(t, err)
	la.HandlePeerFound(peer.AddrInfo{ID: b.ID(), Addrs: b.Addrs()})

	sa := startService(t, a, la)
	sb := startService(t, b, lb)
	sb.do(command{ID: "adv", Cmd: "advertise", Topic: "device/one"})
	sb.rec.wait(t, 5*time.Second, is("ok", "id", "adv"))

	sa.do(command{ID: "yes", Cmd: "find", Topic: "device/one", TimeoutSeconds: 5})
	sa.rec.wait(t, 5*time.Second, is("found", "id", "yes", "peer", b.ID().String()))
	sa.do(command{ID: "no", Cmd: "find", Topic: "device/two", TimeoutSeconds: 1})
	done := sa.rec.wait(t, 5*time.Second, is("find_done", "id", "no"))
	require.Equal(t, float64(0), done["found"])

	sb.do(command{ID: "unadv", Cmd: "unadvertise", Topic: "device/one"})
	sb.rec.wait(t, 5*time.Second, is("ok", "id", "unadv"))
	sa.do(command{ID: "gone", Cmd: "find", Topic: "device/one", TimeoutSeconds: 1})
	done = sa.rec.wait(t, 5*time.Second, is("find_done", "id", "gone"))
	require.Equal(t, float64(0), done["found"])
}

func TestCommandsThatCantBeDoneSayWhy(t *testing.T) {
	a, b := loopbackHost(t), loopbackHost(t)
	sa := startService(t, a)

	sa.do(command{ID: "1", Cmd: "dance"})
	sa.do(command{ID: "2", Cmd: "advertise"})
	sa.do(command{Cmd: "find", Topic: "x"})
	sa.do(command{ID: "4", Cmd: "send", Peer: b.ID().String(), Message: json.RawMessage(`{}`)})
	sa.do(command{ID: "5", Cmd: "connect", Peer: "not a peer id"})
	sa.do(command{ID: "6", Cmd: "connect", Peer: a.ID().String()})
	ev := sa.rec.wait(t, 5*time.Second, is("error", "cmd", "find"))
	require.Contains(t, ev["error"], "needs an id and a topic")
	for id, want := range map[string]string{
		"1": "unknown command",
		"2": "needs a topic",
		"4": "not connected",
		"5": "peer id",
		"6": "this peer",
	} {
		ev := sa.rec.wait(t, 5*time.Second, is("error", "id", id))
		require.Contains(t, ev["error"], want, "command %s", id)
	}

	// Connected, a message over the size limit is refused before it's sent.
	sa.do(command{ID: "c", Cmd: "connect", Peer: b.ID().String(), Addrs: []string{b.Addrs()[0].String()}})
	sa.rec.wait(t, 5*time.Second, is("connected", "id", "c"))
	require.ErrorContains(t, sa.msg.send(t.Context(), b.ID(), make([]byte, maxMessageSize+1)), "limit")
}

// A peer that crashed and came back (same identity, new connection) gets the
// replies to what it sends, though the old connection still looks alive.
func TestARestartedPeerGetsItsReplies(t *testing.T) {
	net := newSimNet(t)
	a := net.host("/ip4/1.2.0.10/udp/8000/quic-v1", true)
	key, _, err := crypto.GenerateEd25519Key(nil)
	require.NoError(t, err)
	b, crash := net.crashableHost("/ip4/1.2.0.20/udp/8000/quic-v1", libp2p.Identity(key))
	sa := startService(t, a)
	sb := startService(t, b)

	n := func(ev map[string]any, field string) any {
		m, _ := ev["message"].(map[string]any)
		return m[field]
	}
	exchange := func(sb *testService, round float64) {
		t.Helper()
		sb.do(command{Cmd: "send", Peer: a.ID().String(), Message: json.RawMessage(fmt.Sprintf(`{"n":%v}`, round))})
		sa.rec.wait(t, 5*time.Second, func(ev map[string]any) bool { return ev["event"] == "message" && n(ev, "n") == round })
		sa.do(command{Cmd: "send", Peer: b.ID().String(), Message: json.RawMessage(fmt.Sprintf(`{"reply":%v}`, round))})
		sb.rec.wait(t, 5*time.Second, func(ev map[string]any) bool { return ev["event"] == "message" && n(ev, "reply") == round })
	}

	sb.do(command{ID: "c", Cmd: "connect", Peer: a.ID().String(), Addrs: []string{a.Addrs()[0].String()}})
	sb.rec.wait(t, 5*time.Second, is("connected", "id", "c"))
	exchange(sb, 1)

	crash()
	again := net.host("/ip4/1.2.0.30/udp/8000/quic-v1", true, libp2p.Identity(key))
	sb2 := startService(t, again)
	sb2.do(command{ID: "c", Cmd: "connect", Peer: a.ID().String(), Addrs: []string{a.Addrs()[0].String()}})
	sb2.rec.wait(t, 5*time.Second, is("connected", "id", "c"))
	exchange(sb2, 2)
}

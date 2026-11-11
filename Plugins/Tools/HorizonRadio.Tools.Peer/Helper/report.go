package main

import (
	"encoding/json"
	"io"
	"os"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	ma "github.com/multiformats/go-multiaddr"
	manet "github.com/multiformats/go-multiaddr/net"
)

// events writes events as JSON lines, the same newline-delimited JSON the app
// already speaks with its other helpers.
type events struct {
	mu sync.Mutex
	w  io.Writer
}

var stdout = &events{w: os.Stdout}

// emit writes one event as a JSON line on stdout.
func emit(event string, fields map[string]any) { stdout.emit(event, fields) }

func (e *events) emit(event string, fields map[string]any) {
	line := map[string]any{"time": time.Now().Format("15:04:05.000"), "event": event}
	for k, v := range fields {
		line[k] = v
	}
	b, err := json.Marshal(line)
	if err != nil {
		b = []byte(`{"event":"error","error":"unencodable event"}`)
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	_, _ = e.w.Write(append(b, '\n'))
}

// connInfo describes one connection to a peer: how it got through.
type connInfo struct {
	Path      string `json:"path"` // direct or relayed
	Transport string `json:"transport"`
	IPv6      bool   `json:"ipv6"`
	Direction string `json:"direction"`
	Limited   bool   `json:"limited"`
	Remote    string `json:"remote"`
}

func describeConn(c network.Conn) connInfo {
	addr := c.RemoteMultiaddr()
	info := connInfo{
		Path:      "direct",
		Transport: transportOf(addr),
		Direction: c.Stat().Direction.String(),
		Limited:   c.Stat().Limited,
		Remote:    addr.String(),
	}
	if isRelayed(addr) {
		info.Path = "relayed"
	}
	if _, err := addr.ValueForProtocol(ma.P_IP6); err == nil {
		info.IPv6 = true
	}
	return info
}

func describeConns(h host.Host, id peer.ID) []connInfo {
	conns := h.Network().ConnsToPeer(id)
	out := make([]connInfo, 0, len(conns))
	for _, c := range conns {
		out = append(out, describeConn(c))
	}
	return out
}

// bestPath is the best way we're connected to a peer right now: direct beats
// relayed, and "none" means not connected at all.
func bestPath(h host.Host, id peer.ID) string {
	best := "none"
	for _, c := range describeConns(h, id) {
		if c.Path == "direct" {
			return "direct"
		}
		best = c.Path
	}
	return best
}

func isRelayed(addr ma.Multiaddr) bool {
	_, err := addr.ValueForProtocol(ma.P_CIRCUIT)
	return err == nil
}

func transportOf(addr ma.Multiaddr) string {
	s := addr.String()
	switch {
	case isRelayed(addr):
		return "circuit"
	case strings.Contains(s, "/quic-v1"):
		return "quic"
	case strings.Contains(s, "/tcp/"):
		return "tcp"
	default:
		return "other"
	}
}

// publicAddrs are the addresses another network can reach us on (public IPs,
// port mappings, relay circuits), sorted so two sets compare equal.
func publicAddrs(h host.Host) []string {
	var out []string
	for _, a := range h.Addrs() {
		if manet.IsPublicAddr(a) {
			out = append(out, a.String())
		}
	}
	slices.Sort(out)
	return out
}

func addrStrings(addrs []ma.Multiaddr) []string {
	out := make([]string, 0, len(addrs))
	for _, a := range addrs {
		out = append(out, a.String())
	}
	return out
}

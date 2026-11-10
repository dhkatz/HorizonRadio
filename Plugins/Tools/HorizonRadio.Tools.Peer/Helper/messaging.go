package main

import (
	"bufio"
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"sync"
	"time"

	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/network"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/protocol"
	msmux "github.com/multiformats/go-multistream"
)

const (
	messageProtocol = protocol.ID("/horizon-radio/message/1.0.0")
	// maxMessageSize caps one message. A relayed connection carries 128 KB in
	// all, so anything big waits for a direct one.
	maxMessageSize = 1 << 20
	writeTimeout   = 30 * time.Second
)

// messenger carries the app's messages between peers: each is a frame (its
// length, then its bytes) on one long-lived stream per peer and direction, so
// messages to a peer arrive in the order they were sent. Delivery is at most
// once: a message in flight when a connection drops is lost, so the protocols
// above catch up when they reconnect.
//
// Replies go back over the connection the peer last messaged on. A peer that
// restarted (crashed, or was killed) leaves its old connection looking alive
// here until it times out, and a stream on it goes nowhere; the restarted peer
// messages over its new connection, so that's the one to answer on.
type messenger struct {
	h       host.Host
	deliver func(from peer.ID, msg []byte)

	mu     sync.Mutex
	outs   map[peer.ID]*outbox
	latest map[peer.ID]network.Conn // the connection each peer last messaged on
}

type outbox struct {
	mu sync.Mutex // one send at a time, so frames don't interleave
	s  network.Stream
}

func newMessenger(h host.Host, deliver func(peer.ID, []byte)) *messenger {
	m := &messenger{h: h, deliver: deliver, outs: map[peer.ID]*outbox{}, latest: map[peer.ID]network.Conn{}}
	h.SetStreamHandler(messageProtocol, m.receive)
	return m
}

func (m *messenger) close() {
	m.h.RemoveStreamHandler(messageProtocol)
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, o := range m.outs {
		o.mu.Lock()
		if o.s != nil {
			_ = o.s.Close()
		}
		o.mu.Unlock()
	}
}

// send writes msg to p over the existing connection (relayed or direct).
func (m *messenger) send(ctx context.Context, p peer.ID, msg []byte) error {
	if len(msg) > maxMessageSize {
		return fmt.Errorf("message is %d bytes; the limit is %d", len(msg), maxMessageSize)
	}
	m.mu.Lock()
	o := m.outs[p]
	if o == nil {
		o = &outbox{}
		m.outs[p] = o
	}
	m.mu.Unlock()

	o.mu.Lock()
	defer o.mu.Unlock()
	var err error
	for range 2 { // a stream found broken is replaced once
		preferred := m.preferredConn(p)
		if o.s != nil && m.stale(o.s, p, preferred) {
			_ = o.s.Close()
			o.s = nil
		}
		if o.s == nil {
			sctx, cancel := context.WithTimeout(network.WithAllowLimitedConn(ctx, "message"), writeTimeout)
			if preferred != nil {
				o.s, err = openOn(sctx, preferred)
			} else {
				o.s, err = m.h.NewStream(sctx, p, messageProtocol)
			}
			cancel()
			if err != nil {
				o.s = nil
				return err
			}
		}
		_ = o.s.SetWriteDeadline(time.Now().Add(writeTimeout))
		if err = writeFrame(o.s, msg); err == nil {
			return nil
		}
		_ = o.s.Reset()
		o.s = nil
	}
	return err
}

// preferredConn is the connection p last messaged on, while it's open and
// isn't a relayed one with a direct one beside it.
func (m *messenger) preferredConn(p peer.ID) network.Conn {
	m.mu.Lock()
	c := m.latest[p]
	m.mu.Unlock()
	if c == nil || c.IsClosed() || (c.Stat().Limited && bestPath(m.h, p) == "direct") {
		return nil
	}
	return c
}

// stale says a stream to p should be replaced: its connection closed, p has
// since messaged on another one, or it's on a relay (which cuts it after 2
// minutes) while a direct connection exists.
func (m *messenger) stale(s network.Stream, p peer.ID, preferred network.Conn) bool {
	c := s.Conn()
	return c.IsClosed() ||
		(preferred != nil && c != preferred) ||
		(c.Stat().Limited && bestPath(m.h, p) == "direct")
}

// openOn opens a message stream on one particular connection.
func openOn(ctx context.Context, c network.Conn) (network.Stream, error) {
	s, err := c.NewStream(ctx)
	if err != nil {
		return nil, err
	}
	if err := s.SetProtocol(messageProtocol); err != nil {
		_ = s.Reset()
		return nil, err
	}
	_ = s.SetDeadline(time.Now().Add(writeTimeout))
	if err := msmux.SelectProtoOrFail(messageProtocol, s); err != nil {
		_ = s.Reset()
		return nil, err
	}
	_ = s.SetDeadline(time.Time{})
	return s, nil
}

func (m *messenger) receive(s network.Stream) {
	defer s.Close()
	from := s.Conn().RemotePeer()
	m.mu.Lock()
	m.latest[from] = s.Conn()
	m.mu.Unlock()
	r := bufio.NewReader(s)
	for {
		msg, err := readFrame(r)
		if err != nil {
			if err != io.EOF {
				_ = s.Reset()
			}
			return
		}
		m.deliver(from, msg)
	}
}

func writeFrame(w io.Writer, msg []byte) error {
	frame := binary.AppendUvarint(make([]byte, 0, len(msg)+binary.MaxVarintLen32), uint64(len(msg)))
	_, err := w.Write(append(frame, msg...))
	return err
}

func readFrame(r *bufio.Reader) ([]byte, error) {
	n, err := binary.ReadUvarint(r)
	if err != nil {
		return nil, err
	}
	if n > maxMessageSize {
		return nil, fmt.Errorf("message of %d bytes is over the limit", n)
	}
	msg := make([]byte, n)
	if _, err := io.ReadFull(r, msg); err != nil {
		return nil, err
	}
	return msg, nil
}

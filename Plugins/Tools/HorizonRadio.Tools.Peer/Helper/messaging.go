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
type messenger struct {
	h       host.Host
	deliver func(from peer.ID, msg []byte)

	mu   sync.Mutex
	outs map[peer.ID]*outbox
}

type outbox struct {
	mu sync.Mutex // one send at a time, so frames don't interleave
	s  network.Stream
}

func newMessenger(h host.Host, deliver func(peer.ID, []byte)) *messenger {
	m := &messenger{h: h, deliver: deliver, outs: map[peer.ID]*outbox{}}
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
		// A stream on a relayed connection moves to the direct one once there is
		// one: the relay cuts its connection after 2 minutes.
		if o.s != nil && o.s.Conn().Stat().Limited && bestPath(m.h, p) == "direct" {
			_ = o.s.Close()
			o.s = nil
		}
		if o.s == nil {
			sctx, cancel := context.WithTimeout(network.WithAllowLimitedConn(ctx, "message"), writeTimeout)
			o.s, err = m.h.NewStream(sctx, p, messageProtocol)
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

func (m *messenger) receive(s network.Stream) {
	defer s.Close()
	from := s.Conn().RemotePeer()
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

package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"sync"
	"time"

	"github.com/libp2p/go-libp2p/core/host"
	"github.com/libp2p/go-libp2p/core/peer"
	"github.com/libp2p/go-libp2p/core/peerstore"
	ma "github.com/multiformats/go-multiaddr"
)

// command is one line the app writes to the helper's stdin. Each gets an
// "ok" or "error" event back with the same id (when it has one).
//
//	{"cmd":"advertise","topic":"pair/4F7K-QX2M"}     findable under a topic
//	{"cmd":"unadvertise","topic":"pair/4F7K-QX2M"}
//	{"id":"1","cmd":"find","topic":"…","timeout_seconds":60}
//	        → "found" events, then "find_done"
//	{"id":"1","cmd":"stop"}                          end a find early
//	{"id":"2","cmd":"connect","peer":"12D3…","addrs":["/ip4/…"]}
//	{"id":"3","cmd":"send","peer":"12D3…","message":{…any JSON…}}
//	{"cmd":"disconnect","peer":"12D3…"}
//	{"cmd":"shutdown"}
type command struct {
	ID             string          `json:"id,omitempty"`
	Cmd            string          `json:"cmd"`
	Topic          string          `json:"topic,omitempty"`
	Peer           string          `json:"peer,omitempty"`
	Addrs          []string        `json:"addrs,omitempty"`
	Message        json.RawMessage `json:"message,omitempty"`
	TimeoutSeconds float64         `json:"timeout_seconds,omitempty"`
}

const defaultFindTimeout = 2 * time.Minute

// service is the helper as the app runs it: the app's commands in, events out.
// Events about peers: "peer" (a peer the app connected to, or that messaged
// it, changed path: direct, relayed or none) and "message" (one arrived).
type service struct {
	h    host.Host
	n    *node // nil in tests that build their own host
	disc []discovery
	ev   *events
	msg  *messenger

	mu         sync.Mutex
	advertised map[string]context.CancelFunc // by topic
	finds      map[string]context.CancelFunc // by command id
	watched    map[peer.ID]string            // peers the app cares about → last path reported
	sends      map[string]chan command       // by peer: sends waiting, in the order given
}

// maxQueuedSends is how many messages may wait for one peer.
const maxQueuedSends = 256

func newService(ctx context.Context, h host.Host, disc []discovery, ev *events) *service {
	s := &service{
		h: h, disc: disc, ev: ev,
		advertised: map[string]context.CancelFunc{},
		finds:      map[string]context.CancelFunc{},
		watched:    map[peer.ID]string{},
		sends:      map[string]chan command{},
	}
	s.msg = newMessenger(h, s.delivered)
	go s.watchPaths(ctx)
	return s
}

// run reads commands from r until it ends, ctx ends or "shutdown".
func (s *service) run(ctx context.Context, r io.Reader) error {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	defer s.msg.close()

	lines := make(chan []byte)
	readErr := make(chan error, 1)
	go func() {
		sc := bufio.NewScanner(r)
		sc.Buffer(make([]byte, 64*1024), 2*maxMessageSize)
		for sc.Scan() {
			select {
			case lines <- append([]byte(nil), sc.Bytes()...):
			case <-ctx.Done():
				return
			}
		}
		readErr <- sc.Err()
	}()

	for {
		select {
		case <-ctx.Done():
			return nil
		case err := <-readErr:
			return err // stdin closed: the app is gone
		case line := <-lines:
			var c command
			if err := json.Unmarshal(line, &c); err != nil {
				s.ev.emit("error", map[string]any{"error": "unreadable command: " + err.Error()})
				continue
			}
			switch c.Cmd {
			case "shutdown":
				return nil
			case "send":
				s.queueSend(ctx, c) // in order, per peer
			default:
				go s.handle(ctx, c)
			}
		}
	}
}

func (s *service) handle(ctx context.Context, c command) {
	var err error
	switch c.Cmd {
	case "advertise":
		err = s.advertise(ctx, c.Topic)
	case "unadvertise":
		s.mu.Lock()
		if stop, ok := s.advertised[c.Topic]; ok {
			stop()
			delete(s.advertised, c.Topic)
		}
		s.mu.Unlock()
	case "find":
		err = s.find(ctx, c)
	case "stop":
		s.mu.Lock()
		if stop, ok := s.finds[c.ID]; ok {
			stop()
		}
		s.mu.Unlock()
	case "connect":
		var path string
		if path, err = s.connect(ctx, c); err == nil {
			s.ev.emit("connected", map[string]any{"id": c.ID, "peer": c.Peer, "path": path})
			return
		}
	case "disconnect":
		var p peer.ID
		if p, err = peer.Decode(c.Peer); err == nil {
			s.mu.Lock()
			delete(s.watched, p)
			s.mu.Unlock()
			err = s.h.Network().ClosePeer(p)
		}
	default:
		err = fmt.Errorf("unknown command %q", c.Cmd)
	}
	s.reply(c, err)
}

func (s *service) reply(c command, err error) {
	if err != nil {
		s.ev.emit("error", map[string]any{"id": c.ID, "cmd": c.Cmd, "error": err.Error()})
	} else if c.ID != "" && c.Cmd != "find" {
		s.ev.emit("ok", map[string]any{"id": c.ID})
	}
}

func (s *service) advertise(ctx context.Context, topic string) error {
	if topic == "" {
		return errors.New("advertise needs a topic")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.advertised[topic]; ok {
		return nil
	}
	actx, stop := context.WithCancel(ctx)
	s.advertised[topic] = stop
	for _, d := range s.disc {
		go d.advertise(actx, topic)
	}
	return nil
}

// find reports each peer found under the topic once, as it's found, until the
// timeout, "stop", or shutdown; then "find_done".
func (s *service) find(ctx context.Context, c command) error {
	if c.Topic == "" || c.ID == "" {
		return errors.New("find needs an id and a topic")
	}
	timeout := defaultFindTimeout
	if c.TimeoutSeconds > 0 {
		timeout = time.Duration(c.TimeoutSeconds * float64(time.Second))
	}
	fctx, stop := context.WithTimeout(ctx, timeout)
	defer stop()
	s.mu.Lock()
	s.finds[c.ID] = stop
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		delete(s.finds, c.ID)
		s.mu.Unlock()
	}()

	var mu sync.Mutex
	seen := map[peer.ID]bool{}
	report := selfFilter(s.h, func(p peer.AddrInfo) {
		s.h.Peerstore().AddAddrs(p.ID, p.Addrs, 10*time.Minute)
		mu.Lock()
		first := !seen[p.ID]
		seen[p.ID] = true
		mu.Unlock()
		if first {
			s.ev.emit("found", map[string]any{"id": c.ID, "topic": c.Topic, "peer": p.ID.String(), "addrs": addrStrings(p.Addrs)})
		}
	})
	var wg sync.WaitGroup
	for _, d := range s.disc {
		wg.Go(func() { d.find(fctx, c.Topic, report) })
	}
	wg.Wait()
	s.ev.emit("find_done", map[string]any{"id": c.ID, "topic": c.Topic, "found": len(seen)})
	return nil
}

// connect dials a peer by the addresses given, those already known (from a
// find), or, with the public DHT, a lookup of its ID.
func (s *service) connect(ctx context.Context, c command) (string, error) {
	p, err := peer.Decode(c.Peer)
	if err != nil {
		return "", fmt.Errorf("peer id: %w", err)
	}
	if p == s.h.ID() {
		return "", errors.New("that's this peer")
	}
	info := peer.AddrInfo{ID: p}
	for _, a := range c.Addrs {
		addr, err := ma.NewMultiaddr(a)
		if err != nil {
			return "", fmt.Errorf("address %q: %w", a, err)
		}
		info.Addrs = append(info.Addrs, addr)
	}
	s.watch(p)

	cctx, cancel := context.WithTimeout(ctx, time.Minute)
	defer cancel()
	if len(info.Addrs) == 0 && len(s.h.Peerstore().Addrs(p)) == 0 && s.n != nil && s.n.dht != nil {
		if s.n.waitBootstrapped(cctx) == nil {
			if found, err := s.n.dht.FindPeer(cctx, p); err == nil {
				info.Addrs = found.Addrs
			}
		}
	}
	if len(info.Addrs) > 0 {
		s.h.Peerstore().AddAddrs(p, info.Addrs, peerstore.TempAddrTTL)
	}
	if err := s.h.Connect(cctx, info); err != nil {
		return "", err
	}
	return bestPath(s.h, p), nil
}

// queueSend hands c to its peer's sender, which sends one message at a time,
// so messages to a peer leave in the order the app gave them.
func (s *service) queueSend(ctx context.Context, c command) {
	s.mu.Lock()
	q, ok := s.sends[c.Peer]
	if !ok {
		q = make(chan command, maxQueuedSends)
		s.sends[c.Peer] = q
		go func() {
			for {
				select {
				case <-ctx.Done():
					return
				case c := <-q:
					s.reply(c, s.send(ctx, c))
				}
			}
		}()
	}
	s.mu.Unlock()
	select {
	case q <- c:
	default:
		s.reply(c, fmt.Errorf("%d messages are already waiting for that peer", maxQueuedSends))
	}
}

func (s *service) send(ctx context.Context, c command) error {
	p, err := peer.Decode(c.Peer)
	if err != nil {
		return fmt.Errorf("peer id: %w", err)
	}
	if len(c.Message) == 0 || !json.Valid(c.Message) {
		return errors.New("send needs a JSON message")
	}
	if bestPath(s.h, p) == "none" {
		return errors.New("not connected to that peer")
	}
	return s.msg.send(ctx, p, c.Message)
}

func (s *service) delivered(from peer.ID, msg []byte) {
	if !json.Valid(msg) {
		s.ev.emit("error", map[string]any{"error": "a message that isn't JSON", "peer": from.String()})
		return
	}
	s.watch(from)
	s.ev.emit("message", map[string]any{"peer": from.String(), "message": json.RawMessage(msg)})
}

// watch reports p's path from now on.
func (s *service) watch(p peer.ID) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.watched[p]; !ok {
		s.watched[p] = "none" // a change is reported on the next check
	}
}

// watchPaths reports each watched peer's path when it changes: relayed →
// direct is a hole punch, → none a lost connection.
func (s *service) watchPaths(ctx context.Context) {
	t := time.NewTicker(250 * time.Millisecond)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		}
		s.mu.Lock()
		for p, last := range s.watched {
			if now := bestPath(s.h, p); now != last {
				s.watched[p] = now
				s.ev.emit("peer", map[string]any{"peer": p.String(), "path": now, "conns": describeConns(s.h, p)})
			}
		}
		s.mu.Unlock()
	}
}

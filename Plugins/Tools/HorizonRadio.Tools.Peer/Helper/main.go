// Command horizon-peer is Horizon Radio's peer-to-peer helper: peers find each
// other through the public IPFS DHT or the local network and connect directly
// (through NAT) without any server of ours.
//
//	horizon-peer serve                    the app's peer service (commands on stdin; see serve.go)
//	horizon-peer id                       print this machine's peer ID
//	horizon-peer probe listen             announce this peer and wait for probes
//	horizon-peer probe dial <peer-id>     find and connect to a listening peer
//
// Output is newline-delimited JSON on stdout.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"strings"
	"time"

	"github.com/libp2p/go-libp2p/core/peer"
)

func main() {
	if err := run(os.Args[1:]); err != nil {
		emit("error", map[string]any{"error": err.Error()})
		os.Exit(1)
	}
}

func usage() error {
	return fmt.Errorf("usage: horizon-peer serve [-discovery amino,lan] | id | probe listen | probe dial <peer-id>  [-key path] [-force-private] [-hold 3m] [-find-timeout 3m] [-find any|record]")
}

// serve runs the service until stdin closes (the app exited) or "shutdown".
func serve(ctx context.Context, keyPath string, forcePrivate bool, discoveryList string) error {
	var amino, lan bool
	for _, d := range strings.Split(discoveryList, ",") {
		switch strings.TrimSpace(d) {
		case "amino":
			amino = true
		case "lan":
			lan = true
		case "":
		default:
			return fmt.Errorf("unknown discovery %q (amino, lan)", d)
		}
	}
	n, err := startNode(ctx, nodeOptions{keyPath: keyPath, forcePrivate: forcePrivate, amino: amino, events: stdout})
	if err != nil {
		return err
	}
	defer n.close()

	var disc []discovery
	if amino {
		disc = append(disc, aminoDiscovery{n})
	}
	if lan {
		l, err := newLANDiscovery(n.host, true)
		if err != nil {
			return err
		}
		defer l.close()
		disc = append(disc, l)
	}
	s := newService(ctx, n.host, disc, stdout)
	s.n = n
	stdout.emit("ready", map[string]any{"peer_id": n.host.ID().String(), "discovery": discoveryList})
	return s.run(ctx, os.Stdin)
}

func run(args []string) error {
	if len(args) == 0 {
		return usage()
	}
	fs := flag.NewFlagSet("horizon-peer", flag.ContinueOnError)
	keyPath := fs.String("key", defaultKeyPath(), "identity key file")
	forcePrivate := fs.Bool("force-private", false, "behave as if behind NAT, to test relays and hole punching")
	hold := fs.Duration("hold", 3*time.Minute, "dial: how long to keep the connection open")
	findTimeout := fs.Duration("find-timeout", 3*time.Minute, "dial: how long to look for the peer")
	find := fs.String("find", "any", "dial: how to find the peer: any, or record (its published DHT record only)")
	discoveryFlag := fs.String("discovery", "amino,lan", "serve: how peers find each other: amino (the public IPFS DHT), lan (mDNS), or both")

	cmd, rest := args[0], args[1:]
	var sub string
	if cmd == "probe" {
		if len(rest) == 0 {
			return usage()
		}
		sub, rest = rest[0], rest[1:]
	}
	var positional []string
	if sub == "dial" && len(rest) > 0 && rest[0][0] != '-' {
		positional, rest = rest[:1], rest[1:]
	}
	if err := fs.Parse(rest); err != nil {
		return err
	}

	if cmd == "id" {
		sk, err := loadOrCreateKey(*keyPath)
		if err != nil {
			return err
		}
		id, err := peer.IDFromPrivateKey(sk)
		if err != nil {
			return err
		}
		emit("id", map[string]any{"peer_id": id.String(), "key": *keyPath})
		return nil
	}
	if cmd != "serve" && (cmd != "probe" || (sub != "listen" && sub != "dial")) {
		return usage()
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	if cmd == "serve" {
		return serve(ctx, *keyPath, *forcePrivate, *discoveryFlag)
	}
	n, err := startNode(ctx, nodeOptions{keyPath: *keyPath, forcePrivate: *forcePrivate, amino: true})
	if err != nil {
		return err
	}
	defer n.close()
	if err := n.waitBootstrapped(ctx); err != nil {
		return err
	}

	if sub == "listen" {
		return listen(ctx, n)
	}
	if len(positional) == 0 {
		return usage()
	}
	target, err := peer.Decode(positional[0])
	if err != nil {
		return fmt.Errorf("peer id: %w", err)
	}
	if target == n.host.ID() {
		return fmt.Errorf("that's this machine's own peer ID; use -key to give the dialer another identity")
	}
	return dial(ctx, n, target, dialOptions{findTimeout: *findTimeout, hold: *hold, recordOnly: *find == "record"})
}

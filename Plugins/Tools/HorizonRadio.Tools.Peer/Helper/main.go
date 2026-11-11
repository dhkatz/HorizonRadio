// Command horizon-peer is Horizon Radio's peer-to-peer helper. For now it only
// probes connectivity: whether two machines can find each other through the
// public IPFS DHT and connect directly (through NAT) without any server of ours.
//
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
	return fmt.Errorf("usage: horizon-peer id | probe listen | probe dial <peer-id>  [-key path] [-force-private] [-hold 3m] [-find-timeout 3m] [-find any|record]")
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
	if cmd != "probe" || (sub != "listen" && sub != "dial") {
		return usage()
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	n, err := startNode(ctx, nodeOptions{keyPath: *keyPath, forcePrivate: *forcePrivate})
	if err != nil {
		return err
	}
	defer n.close()

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

package main

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"time"

	"github.com/ipfs/go-cid"
	dht "github.com/libp2p/go-libp2p-kad-dht"
	dhtpb "github.com/libp2p/go-libp2p-kad-dht/pb"
	"github.com/libp2p/go-libp2p/core/peer"
)

const (
	provideLookupTimeout = time.Minute
	providePutTimeout    = 15 * time.Second
)

// provide stores a provider record for key on the closest peers to it in the
// public DHT, and reports how many took it.
//
// The DHT's own Provide waits for its lookup to finish, and a lookup has no
// per-peer timeout: one peer that accepts a stream but never answers holds it
// until the caller's deadline (seen on Amino: 5 minutes and no record stored).
// So the lookup is capped, the closest peers found by then are used, and each
// store gets its own timeout.
func provide(ctx context.Context, d *dht.IpfsDHT, key cid.Cid) (int, error) {
	lctx, cancel := context.WithTimeout(ctx, provideLookupTimeout)
	closest, err := d.GetClosestPeers(lctx, string(key.Hash()))
	cancel()
	if ctx.Err() != nil {
		return 0, ctx.Err()
	}
	if err != nil && !errors.Is(err, context.DeadlineExceeded) {
		return 0, err
	}
	if len(closest) == 0 {
		return 0, errors.New("the DHT lookup found no peers")
	}

	messenger, err := dhtpb.NewProtocolMessenger(d.MessageSender())
	if err != nil {
		return 0, err
	}
	self := peer.AddrInfo{ID: d.PeerID(), Addrs: d.FilteredAddrs()}

	var stored atomic.Int32
	var wg sync.WaitGroup
	for _, p := range closest {
		wg.Go(func() {
			pctx, cancel := context.WithTimeout(ctx, providePutTimeout)
			defer cancel()
			if messenger.PutProviderAddrs(pctx, p, key.Hash(), self) == nil {
				stored.Add(1)
			}
		})
	}
	wg.Wait()
	if stored.Load() == 0 {
		return 0, errors.New("no peer accepted the record")
	}
	return int(stored.Load()), nil
}

//go:build debug

package main

import (
	"net/http"
	_ "net/http/pprof"
)

// Debug builds (-tags debug) serve pprof on localhost:6061.
func init() {
	go func() { _ = http.ListenAndServe("127.0.0.1:6061", nil) }()
}

# Horizon Radio — native (`version.dll`) internals

How the injected C++ side is built and why. This is the code-level companion to
[`AGENTS.md`](../AGENTS.md), which holds the contributor runbook (build, the FH6
offset table and how to re-derive it after a game update, code-quality commands).
Read that first for the high-level architecture diagram and the offset chain;
this document covers how the pieces fit together at runtime.

For *why* static reverse-engineering doesn't work here (FH6 ships a packed
binary; inspect the live process via the discovery dump instead) see AGENTS.md →
*In-game title injection*.

## The two processes

```
HorizonRadio.UI (C#)  ──named-pipe IPC (newline-delimited JSON)──►  version.dll (C++, in FH6)
                      ──PCM pipe (raw s16 stereo frames)──────────►
```

The UI owns all source decoding (Spotify/librespot, local files, YouTube/yt-dlp)
and metadata enrichment. `version.dll` is a thin in-process agent: it takes PCM
and track metadata over two named pipes and splices them into the game's radio —
PCM via an FMOD DSP, title/artist via a direct write into the game's now-playing
struct. It owns no decoding logic.

## DLL runtime structure

`DllMain` does almost nothing: on `DLL_PROCESS_ATTACH` it spawns
`bridge_init_thread` and returns (work under the loader lock is forbidden). It
stays completely inert when our own UI loads the proxy (`host_is_companion_ui`),
so the IPC server never connects to itself.

### Threads

All worker threads are **detached** deliberately: joining under the loader lock
would deadlock, and on process exit the OS tears them down before
`DLL_PROCESS_DETACH` runs. `stop()` on the IPC/PCM servers is the only join, wired
on detach so an attached UI sees a clean pipe disconnect.

- **`bridge_init_thread`** — starts the IPC + PCM servers, resolves FMOD / the
  `RadioState` global / the `RadioStreamFmod` vtable, brings up the bridge and
  injector, then becomes the **periodic writer** and loops forever.
- **periodic writer** (the tail of `bridge_init_thread`) — the 20 Hz heartbeat.
  Each tick it polls in-game events, picks the radio-stream instance to attach
  to, writes our title into it, and installs/retargets/detaches the DSP. Fast in
  steady state (one read + compare); the expensive heap scan is cached and only
  re-run every ~5 s until instances are found.
- **discovery dumper** — opt-in via a trigger file; see *Discovery dump* below.
- **IPC + PCM server threads** — one each, owned by `IpcServer` / `PcmPipeServer`.

### Shared state

Cross-thread state lives in file-scope atomics (`g_bridge_for_push`,
`g_metadata_injector`, `g_radio_state_global`, `g_radio_stream_vtable`,
`g_on_target_station`, `g_have_track`) plus two mutex-guarded blocks
(`g_current_track`, `g_target_station`). The bridge/injector themselves are
function-local `static`s in `bridge_init_thread`: it blocks forever after setup,
so their lifetime is effectively the process.

`g_have_track` is the historical "we have audio worth playing" gate that the DSP
installer keys on. With no in-DLL source firing it, the first PCM chunk plants a
stub track so the install path proceeds; real metadata arrives later over IPC.

## Audio path

```
UI ──PCM pipe──► PcmPipeServer ──► FmodBridge.push_pcm ──► ring buffer
                                                              │ FMOD pulls
                                   DSP read callback ◄─────────┘
```

`PcmPipeServer` reads raw s16 stereo frames and hands them to `FmodBridge`, which
buffers them in a lock-free ring. `FmodBridge` installs an FMOD DSP on the radio
stream's channel; the game pulls audio through the DSP's read callback, which
drains the ring.

- **Resampling** — source PCM is 44.1 kHz, the game mixes at 48 kHz. The read
  callback linearly interpolates between buffered frames at the fractional phase,
  advancing by `44100/48000` per output frame. A 1:1 fast path skips this when
  rates match.
- **Normalizer** — per-block AGC + peak limiter on the output, reset on every
  track change so a hot intro doesn't inherit the previous track's curve.
- **Handle resolution / install / retarget** — `tick()` resolves the live FMOD
  channel handle from the stream and installs the DSP. When the handle changes
  (station switch) it removes the old DSP and reinstalls; when the channel dies
  (radio off) it clears the target so the DSP uninstalls cleanly — a stale handle
  would make `removeDSP` hit a destroyed channel on recovery.
- **`set_master_gain`** — scales output, clamped to `[0,1]`; driven by the UI's
  `set_gain` command (the events "duck / set volume" action).

All FMOD entry points are resolved lazily by byte-pattern signature (see
*Signature resolution*); every call into them is `seh_call`-guarded.

## Metadata path

```
UI ──IPC {"cmd":"set_track"}──► command callback ──► g_current_track
                                                         │ periodic writer
RadioStreamFmod instance ◄── MetadataInjector.write_to_instance ◄┘
```

The periodic writer re-applies the cached track every tick, so the title lands
as soon as the game's `RadioStreamFmod` instances exist (which is well after the
UI sends the first `set_track`).

### Finding the instances

`MetadataInjector` heap-scans for `make_shared<RadioStreamFmod>` control blocks by
vtable (resolved via MSVC RTTI), filtered by plausible refcount values and a
module pointer at +16 to reject `/OPT:ICF` false matches. The scan is whole-process
(`VirtualQuery` over committed `MEM_PRIVATE` regions) and capped to bound time;
real candidates are typically <10.

From a candidate instance the configured chain (`kFh6Metadata.chain_offsets`)
walks to the metadata block; the title/artist are MSVC `std::string` fields
written via `write_msvc_string`. The exact chain + field offsets and how to
re-derive them after a game update live in AGENTS.md.

### Which instance, and write/restore

Multiple chain-valid instances can exist (music + host segments share the vtable).
The writer picks the **one whose FMOD `System*` resolves** — i.e. the audible
station — so the title lands where the audio is, and locks onto it
(`preferred_instance`) to avoid ping-ponging.

[`TitleWriteController`](../HorizonRadio/include/horizon/inject/title_write_controller.hpp)
owns the write/restore bookkeeping for that single instance: snapshot the game's
original strings on first touch, write ours each tick, keep the restore value in
sync as the game advances its own track, and write the originals back when we stop
replacing the station (source stopped, or tuned away). It's extracted from the
writer thread and unit-tested with a fake injector. Its header comment documents
the per-tick contract.

### Station targeting

`set_target_station` gates both the DSP attach and the metadata write: we only
replace the station the user chose (empty / `*` = whatever's active). Off-target,
the DSP detaches and the game's own station plays untouched. The gate
(`g_on_target_station`) is recomputed each event poll from the live station name.

## Signature resolution

FH6 is packed, so everything is resolved at runtime from the decrypted in-memory
image (`PeImage` parses the loaded PE's sections).

- **`sigscan`** — byte-pattern matching with `??` wildcards over `.text`, plus
  alternative patterns (`a | b | c`) for prologues that vary across builds.
- **Anchored resolution** — for exported-by-name-ish functions, find the anchor
  string in `.rdata`, find the `lea reg, [rip+disp32]` that references it, then
  use `.pdata` to walk from that instruction to the enclosing function's start.
  More robust than a raw prologue pattern across game updates.
- **FMOD resolver** (`FmodResolver`) — resolves the handful of FMOD DSP/channel
  entry points from `kFh6` signatures; reports per-slot success and a diagnostic
  stage on failure (anchor missed / lea decode / no enclosing function / prologue
  mismatch / ambiguous).
- **Game resolver** (`GameResolver`) — resolves the `RadioState` singleton by
  sigscanning `radio_set_station_by_name` and decoding the RIP-relative `mov rbx,
  [rip+disp32]` that loads the global.

## SEH strategy

Every read or write of game memory is guarded by SEH (`__try`/`__except` on
`EXCEPTION_ACCESS_VIOLATION`, via `safe_read_*` / `seh_call` / per-instance
wrappers). An instance can be freed between the heap scan and the write, or a
spurious vtable match can point at unmapped pages; catching the AV lets us skip
the candidate instead of crashing the host.

MSVC forbids C++ objects with destructors in a function that uses `__try`
(C2712), so the SEH wrappers are kept destructor-free and forward to a separate
worker that does the real work (e.g. `process_one_instance` → `do_write_one`).

## In-game event detection

`poll_game_events` reads the resolved `RadioState` singleton each tick:

- **race start/finish** — two race-active bytes (both nonzero ⇒ in a race),
  edge-detected.
- **station changed** — the station-name `std::string`, reached by a two-hop
  pointer chain off the singleton; also drives the targeting gate.
- **radio on/off is NOT detectable** — the station name doesn't change on power
  off and the obvious counters are unreliable. Don't re-add it.

Edge-detection state is in function-local statics; the single-caller (periodic
writer) invariant makes that safe without locking. Offsets are in `kFh6Game`;
verify them against the one-shot dump the resolver emits.

## IPC wire protocol

Newline-delimited UTF-8 JSON over a Windows named pipe (`\\.\pipe\HorizonRadio`),
single connection (the UI is the only client). PCM rides a separate pipe
(`HorizonRadio.pcm`) as raw frames.

**DLL → UI events** (`"event"` field names the kind):

```
{"event":"hello","pid":N,"version":"x.y.z"}
{"event":"track","title":"...","artist":"...","album":null,
         "source_id":"local","source_display":"Local Files","art_b64":null}
{"event":"stats","installed":true,"frames_in":N,"frames_out":N,
         "underruns":N,"normalizer_gain":1.0,"limiter_gain":1.0}
{"event":"source_changed","id":"spotify","display":"Spotify Connect"}
{"event":"game_event","kind":"race_start"}
```

**UI → DLL commands** (`"cmd"` field):

```
{"cmd":"set_track","title":"...","artist":"...","album":"...",
        "source_id":"...","source_display":"...","external_id":"..."}
{"cmd":"set_gain","gain":0.3}
{"cmd":"set_target_station","station":"Horizon Pulse"}   // "" or "*" = any
```

`set_track` is the main path: it routes C#-decoded metadata into the game HUD via
the injector and resets the normalizer. The DLL only scans for the keys it needs
rather than pulling in a full JSON parser; values support standard escapes and
BMP `\u` sequences.

Threading: `publish_*` are callable from any thread and serialize on a mutex so
JSON lines can't interleave; they short-circuit when no client is connected. A
**snapshot callback** fires on every (re)connect and re-publishes current track +
source, so the UI doesn't sit on its placeholder when the user attaches
mid-playback.

## version.dll proxy

The game loads us as `version.dll` (its directory beats `System32` in the search
order), so we must export every entry point of the real `version.dll` or the
game crashes during module init. Each export is a thin trampoline that lazy-loads
`C:\Windows\System32\version.dll` and forwards.

We use trampolines rather than PE forwarder records because path-qualified
forwarders are MSVC linker syntax that clang+MinGW drops, and `.def` exports
without a path would recurse onto our own DLL. The export *mechanism* differs by
compiler (`<winver.h>` declares the entry points `dllimport`): clang+MinGW
re-declares them `dllexport` (a suppressible warning), MSVC uses normal linkage
plus a `/EXPORT:` linker pragma (the dllimport→dllexport mismatch is a hard error
there). All 17 forwarders live in `src/version_proxy.cpp`.

## Discovery dump

A diagnostic, opt-in via a trigger file (`horizon-radio.discover` next to the
DLL, deleted after each dump). It walks the live `RadioStreamFmod` candidates,
follows their heap pointers two hops, and flags MSVC-string / ASCII slots —
the tool for re-deriving the metadata chain after a game update. Output goes to
both DebugView and the UI Console (`discover` tag). Full re-derivation procedure:
AGENTS.md → *In-game title injection*.

## Building & cross-compiling

See AGENTS.md → *Building*. In short: C++ side is CMake + Ninja (`windows-x64`
preset, MSVC); it cross-compiles from Linux/macOS via clang + mingw-w64 + lld.
The C# and C++ builds are intentionally decoupled.

# AGENTS.md

Technical and maintenance notes for Horizon Radio. The README is user-facing;
this file holds the internals, build gotchas, and the runbooks you need when a
Forza update breaks something.

## Contributing workflow

These rules are mandatory for every change, human or agent.

- **ALWAYS start a new branch for a feature or fix.** Never commit work directly
  to `main`. Branch off the latest `main` and name the branch with a Conventional
  Commit type prefix, e.g. `feat/per-station-targeting`, `fix/metadata-leak`,
  `ci/pr-title-check`.
- **Commits MUST follow [Conventional Commits](https://www.conventionalcommits.org).**
  Format: `type(optional-scope): summary`, where `type` is one of `build`,
  `chore`, `ci`, `docs`, `feat`, `fix`, `perf`, `refactor`, `revert`, `style`, or
  `test`. Append `!` (or a `BREAKING CHANGE:` footer) for breaking changes.
- **PR titles MUST follow Conventional Commits too.** The
  [`PR title`](.github/workflows/pr-title.yml) workflow enforces this on every PR
  and fails the check otherwise.
- **You MUST run the linters on ALL projects before pushing a branch or opening a
  PR.** Both the managed and native checks under [Code quality](#code-quality)
  must pass — `dotnet format --verify-no-changes` and the build for the C# side,
  `clang-format` and the analysis build for the C++ side.
- **You SHOULD update [`CHANGELOG.md`](CHANGELOG.md) with any relevant changes**
  at a high level — user-facing changes or important internal changes (licensing,
  IPC contract, build process). Add entries under the `Unreleased` section.
  Purely internal churn (typo fixes, refactors with no observable effect) can be
  skipped.

## Architecture

```
Spotify / Local / YouTube ──► HorizonRadio.UI (C#, Avalonia)
                                   │  named-pipe IPC (newline-delimited JSON)
                                   ▼
                              version.dll (C++, injected into FH6)
                                   │  FMOD DSP bridge + metadata writer
                                   ▼
                              Forza Horizon 6 radio
```

- **HorizonRadio.UI / HorizonRadio.Core (C#)** — sources (librespot, local files,
  yt-dlp/ffmpeg), metadata enrichment, and the dashboard. Tool-specific details
  stay encapsulated in each source class; they are not leaked into shared code.
- **version.dll (C++)** — a `version.dll` proxy injected into FH6. It hosts an
  FMOD DSP that mixes our PCM into the game's radio channel, writes the current
  track's title/artist into the game's HUD, and exposes a named pipe to the UI.
- **IPC** — newline-delimited UTF-8 JSON. The DLL publishes `track`, `stats`,
  `source_changed`, `game_event`, and `debug` events; the UI sends `set_track`,
  `set_gain`, and `set_target_station` commands.

For the code-level internals of the native side — thread model, audio/metadata
data flow, signature resolution, the SEH strategy, and the full IPC wire
protocol — see [`docs/architecture.md`](docs/architecture.md).

## Building

The C++ side builds with CMake (`CMakeLists.txt`) via the `windows-x64`
preset; the C# side builds with `dotnet`. They are intentionally
decoupled so a CMake / .NET resolver mishap on one side can't take down
the other.

C++ DLL + tests:

```powershell
cmake --preset windows-x64
cmake --build --preset windows-x64-release      # → build/Release/version.dll
ctest --preset windows-x64-release
```

The preset assumes `cl.exe` is on `PATH` (CI uses
`ilammy/msvc-dev-cmd`; locally, run from a "Developer Command Prompt
for VS 2022" shell, or invoke `vcvarsall.bat x64` first). External C++
deps are pulled via CMake's `FetchContent` at configure time (currently
just doctest, header-only) — no package manager is involved.

### Cross-compiling from Linux or macOS

`version.dll` can also be built from a non-Windows host via clang +
mingw-w64. The clang/MinGW combination is what makes this work: clang
keeps SEH (`__try`/`__except`) and the MSVC-style secure CRT calls
intact; mingw-w64 supplies the Windows headers and import libs as a
normal package, so there's no manual MSVC-SDK download step.

```bash
# macOS
brew install llvm lld mingw-w64 ninja

# Linux (Debian/Ubuntu)
apt install clang lld mingw-w64 ninja-build

# Configure + build
cmake --preset macos-cross-x64                  # or linux-cross-x64
cmake --build --preset macos-cross-x64-release  # → build/Release/version.dll
```

The toolchain requires lld specifically (not mingw-w64's bundled BFD
ld): clang+gcc-libstdc++ produces RTTI section size mismatches that
BFD ld refuses but lld merges cleanly. clang+mingw also drops the C++
stdlib include search paths and SEH support — the toolchain file
(`cmake/toolchain-clang-mingw.cmake`) wires those back up.

Tests build under the cross preset too, but the resulting
`HorizonRadio.Tests.exe` is a Windows binary — running `ctest` from a
non-Windows host won't work without Wine. The Windows CI flow is the
authoritative test runner.

The 17 `version.dll` exports come from `src/version_proxy.cpp` —
plain `__declspec(dllexport)` trampolines that lazy-load
`C:\Windows\System32\version.dll`. They compile identically under
MSVC and clang+MinGW; if you add or remove a forwarded export, edit
that file, not the linker flags.

C# UI / Core — if `dotnet build` crashes with `MSB4014: The path is empty`, an
empty `MSBuild*` env var is to blame; clear it first:

```powershell
Remove-Item Env:MSBuildSDKsPath -ErrorAction SilentlyContinue
Remove-Item Env:MSBuildAdditionalSdkResolversFolder -ErrorAction SilentlyContinue
dotnet build HorizonRadio.UI\HorizonRadio.UI.csproj -c Release
```

### librespot

`librespot.exe` is no longer compiled in-tree. CI builds it (see
`.github/actions/build-librespot`) and publishes it to the permanent
`tools` blobstore (`publish-tools.yml`); the UI fetches it on demand
from the Tools tab. It is not bundled in the app zip.

To bump the pinned rev, edit the `Pin` step in
`.github/actions/build-librespot/action.yml` (cache keys rotate
automatically since they incorporate the rev). For local hacking on a
not-yet-released rev — e.g. testing a Spotify-protocol patch before
merging the pin bump — install the Rust toolchain and run the same
`cargo install` command the composite action runs, then point the
Spotify source's "librespot.exe path" at the resulting exe (or drop it
next to the UI exe — `DiscoverLibrespotExe` still probes there).

### Dev deploy loop

`deploy.ps1` (gitignored) builds Release and copies `version.dll` straight into
the game folder, bypassing the UI's bundle-then-install step. FH6 must be
**closed** to overwrite the loaded DLL.

```powershell
$env:FH6_DIR = "C:\...\ForzaHorizon6"   # once
.\deploy.ps1            # build + copy
.\deploy.ps1 -NoBuild   # copy the existing build only
```

## In-game title injection — and re-deriving it after a game update

The DLL writes our title/artist into the game's now-playing metadata block. The
chain and offsets live in `kFh6Metadata` (`include/horizon/fmod/signatures.hpp`):

```
RadioStreamFmod instance  +0x50 ─► track list  +0x08 ─► metadata block
metadata block:  +0x10 internal event id (NOT written)   +0x30 title   +0x50 artist
```

We write the title to **only the instance we attach audio to**, snapshot that
block's original strings, and restore them when we stop replacing the station
(tracking the game's own track changes so the restore isn't stale). Fields are
MSVC `std::string` (32-byte SSO/heap header).

**These offsets shift when Forza updates.** Symptom: titles stop appearing (the
periodic writer's write count stays 0). To re-derive them:

1. With the DLL deployed and a song playing on the target station, create an
   empty file `horizon-radio.discover` next to `version.dll`.
2. Open the UI **Console** tab and read the dump (tag `discover`). It lists the
   active `RadioStreamFmod` candidates, follows their heap pointers two hops,
   and flags any MSVC-string / ASCII slots it finds.
3. Find the candidate for the main radio bus (`/Master/Radio/Track`, not
   `…/TrackLFE`), follow it to the block whose string slots hold the song's
   real title/artist, and read off the chain + field offsets.
4. Update `kFh6Metadata.chain_offsets` and the field offsets; rebuild; redeploy.

The discovery dump is the only viable tool here: **the FH6 binary is packed**
(its `.text` only decrypts in memory at load), so static Ghidra decompilation
is garbage (`out`/`swi`/`halt_baddata`) and RTTI/strings aren't recoverable.
Inspect the live, decrypted process via the in-process dump instead. This is
not Denuvo — FH6 doesn't use Denuvo — just a selective code transformation.

## FMOD audio bridge

The DLL lazily resolves FMOD function pointers via byte-pattern signatures
(`kFh6` in `signatures.hpp`), then installs a DSP on the radio stream's channel.
For each candidate instance we resolve the FMOD `System*` from the stream
(`instance +0x10`, then the system chain) and hand both to the bridge; its
`tick()` installs/retargets the DSP. All reads are SEH-guarded so an instance
freed mid-scan can't crash the game.

## Event detection

`poll_game_events` reads the resolved `RadioState` singleton each tick:

- **Race start/finish** — two race-active bytes (both nonzero ⇒ in a race),
  edge-detected.
- **Station changed** — the station-name string off a pointer chain; also drives
  the targeting gate (`set_target_station`: only replace the chosen station, or
  any when empty/`*`).
- **Radio on/off is NOT detectable** — the station name doesn't change on power
  off and the obvious counters are unreliable; don't re-add it.

## Code quality

Managed code: `.editorconfig` + .NET analyzers.

```powershell
dotnet format HorizonRadio.UI.slnx --verify-no-changes
dotnet build HorizonRadio.UI.slnx /t:Rebuild
```

Native code: `.clang-format` + `.clang-tidy` (install LLVM).

```powershell
clang-format -i (rg --files HorizonRadio HorizonRadio.Tests -g '*.cpp' -g '*.hpp')
cmake --preset windows-x64-analysis
cmake --build --preset windows-x64-analysis-debug
```

The `analysis` preset flips on `CMAKE_CXX_CLANG_TIDY` so every TU is
linted during compile. It's ~3× slower than a plain build; keep it
off for the dev iteration loop.

# AGENTS.md

Technical and maintenance notes for Horizon Radio. The README is user-facing;
this file holds the internals, build gotchas, and the runbooks you need when a
Forza update breaks something.

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

## Building

`.slnx` solutions route through an SDK/Rust resolver that fails in some
environments — **build the project files directly** instead.

C++ DLL (MSVC from VS 2022 Build Tools; `vswhere` hides Build Tools unless you
pass `-products *`):

```powershell
$msb = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
  -products * -latest -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
& $msb HorizonRadio\HorizonRadio.vcxproj /p:Configuration=Release /p:Platform=x64 /m
```

C# UI / Core — if `dotnet build` crashes with `MSB4014: The path is empty`, an
empty `MSBuild*` env var is to blame; clear it first:

```powershell
Remove-Item Env:MSBuildSDKsPath -ErrorAction SilentlyContinue
Remove-Item Env:MSBuildAdditionalSdkResolversFolder -ErrorAction SilentlyContinue
dotnet build HorizonRadio.UI\HorizonRadio.UI.csproj -c Release
```

The first C++ build compiles `librespot` from source (~5–10 min cold). The
C++ test project (`HorizonRadio.Tests`, doctest) resolves includes via
`$(SolutionDir)`, so build it with `/p:SolutionDir=<repo>\`.

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
msbuild HorizonRadio.slnx /p:Configuration=Debug /p:Platform=x64 /p:RunCppAnalysis=true /m:1
```

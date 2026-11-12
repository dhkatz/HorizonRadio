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

## Repository layout

```
Assets/         README screenshots and artwork
HorizonRadio/   the app: Sdk (the plugin SDK), Sdk.UI (theme + shared controls), Core (headless engine),
                UI (Avalonia), Core.Tests, TitleModel
Plugins/        everything that plugs into Horizon Radio (.NET)
  Games/  Lyrics/  Metadata/  Sources/  Tools/  one project per plugin
    Sources/HorizonRadio.Source.Spotify/Native/   the audio tap DLL (CMake), loaded into Spotify Soloist
Mods/           code that runs inside a game, one folder per game, any toolchain
  ForzaHorizon6/   the version.dll mod (CMake; Bridge/ + Tests/ + docs/)
HorizonRadio.slnx   the .NET solution (the native mod builds separately with CMake)
```

The rule: **`Plugins/` integrates with Horizon Radio; `Mods/` integrates with a game.**
A game plugin (`Plugins/Games/HorizonRadio.Game.<Game>`) pairs with its mod
(`Mods/<Game>`) by name.

**Target layout:** the .NET projects are moving to SDK, Sdk.UI, Core, Platform, UI and Cli, in
the steps listed in [`docs/modular-architecture.md`](docs/modular-architecture.md). Read it before
adding a project, an API plugins call, OS-specific code, or logic in a view model.

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
  Every tool process the app starts is tied to it (`ChildProcesses.TieToApp`, a
  kill-on-close job object), so none outlives the app: a stray librespot stays
  logged in as the same Connect device and takes the next session's plays. The
  Console tab and the app's `Trace` lines are also written to
  `%LOCALAPPDATA%\HorizonRadio\logs` (`LogFile`, one file per session); log with
  `Trace.WriteLine`, not `Debug.WriteLine`, which no listener can capture. A
  source setting that's a credential is a `TextField` with `IsSecret`:
  `SourceConfigStore` keeps it as a secret setting (see [Settings](#settings)), written
  encrypted (`SecretText`, DPAPI for the Windows user, `dpapi:<base64>`), and the form masks it.
  Spotify playback uses librespot; for a track Spotify refuses librespot the audio of,
  the Spotify plugin can play it through Spotify Soloist instead (Spotify's own Connect
  client), taking what Soloist plays with `horizon_audio_tap.dll` (`Native/`, `AudioTap`):
  loaded into Soloist, it patches the process's shared WASAPI vtables
  (`IAudioClient`/`IAudioRenderClient`), copies each rendered block to
  `\\.\pipe\HorizonRadio.tap.<pid>` and releases it marked silent, so Soloist plays
  nothing out loud. `AudioTapConverter` turns its format (usually 48 kHz float) into the
  pipeline's. Windows applies an app's mixer volume before any per-process capture, so
  muting Soloist or capturing it from outside can't do this. `SpotifyPlayableItem` tries
  librespot first; on `SpotifyTrackUnavailableException.AudioKeyRefused` it plays through
  `SoloistPlaybackService`, and once Soloist has played a refused track, Soloist goes first
  (`SpotifyPlayerChoice`, `spotify-player.json`, kept across sessions) until librespot is
  due another try: a day later, or at once when its build changes. A play librespot delivers
  puts it back first. `SoloistPlaybackService` starts `soloist.exe` with the user's key (`Tools/HorizonRadio.Tools.Soloist`), loads the
  tap once Soloist is up (its control port open; loading it at launch raced Soloist's own start)
  and before Soloist opens any audio (its first play), waits for the tap's hooks (`AudioTap.Hooked`), and drives it over its WebSocket API. Soloist has no
  "finished" event and autoplays a related track after a single one, and becoming the
  active device resumes its previous track, so `SoloistTrack` starts a track at the first
  `track_changed` after Soloist acknowledges the `play` and ends it at the next one (or an
  idle/paused stop at the end), and audio only reaches the sink in between.
  An unpaired Soloist holds the track while the user pairs it (`PairingNeeded`, which the plugin
  turns into a notification through `INotifications`: a toast in the window); pairing
  hands the Spotify app's playback over, paused, so a pause the user didn't make here is undone
  (and a play that hasn't started is re-sent).
  A source factory is one of two kinds. A content source (`IContentSourceFactory`:
  local files, YouTube, Spotify links, internet radio) turns a locator into
  `PlayableItem`s and only ever plays through the queue (`QueuePlayback` +
  `QueueSource`), from every surface including the Sources tab. A self-driven
  source (`ISelfDrivenSourceFactory`: Spotify Connect, the test tone) is started
  through the playback router (`PlaybackRouter.StartSourceAsync`) as the player's
  content.
- **Your devices (`Core/Devices/`, `Core/Peers/`)** — paired PCs keep their synced settings in
  step, peer to peer, over the helper (`PeerHelper`, behind the SDK's `IPeerNetwork`, which its plugin
  provides as the `IPeerNetworkFactory` the app starts it with; tests use
  `MemoryPeerNetwork`). `MyDevices` owns it: pairing (a 12-character `PairingCode`: the first four
  pick the meeting topic, the other eight are a secret both sides prove with a PBKDF2-stretched
  HMAC, one attempt per code), the group (`DeviceSettings`: a shared group key, secret, from which
  the devices' meeting topic is derived; the device list, merged newest-first with removals kept),
  and sync: on meeting, each sends every synced setting (`SettingsStore.SyncedSettings`) and merges
  the other's (`MergeSynced`), then sends changes as they happen. Other data rides along as an
  `IDeviceData`, exported whole when devices meet and in part on a change, in pieces small enough
  for one message (a relayed connection carries 128 KB in all): mixes (`MixSync`, over
  `MixStore.SyncedMixes`/`MergeSynced`, each mix timed, a deleted one kept as `removed`; the
  store is safe from any thread, and raises `Changed` on the one that changed it) and history (`HistorySync`: plays are only added, so it merges as a union
  by id, 100 per message; removed ids and the last clearing are kept). Messages from a peer that isn't in
  the group are ignored. The helper starts only once the PC is paired or starts pairing. Anything
  that shows or uses a synced setting has to follow it changing underneath (`Watch`, or the store's
  `Changed`), since another PC can change it at any time.
- **Friends (`Core/Friends/`)** — `Friends` over the same helper as your devices
  (`SharedPeerNetwork`: started when either needs it; each feature ignores the other's messages).
  You are an `Identity`: an ECDSA P-256 key (`identity.key`, secret; handed to a PC when it pairs,
  so all your PCs are one you) and a name (`identity.name`, synced). Adding a friend runs the same
  code exchange as pairing (`PairingCode.TopicFor("friend")`) and keeps their public key as a synced
  setting (`friend.<user id>`), so they're a friend on all your PCs. Presence: each PC advertises
  `presence/<user id>`; a friend's PC finds it, connects, and each side sends a hello signed over
  both connections' peer ids, verified against the key kept for that friend; only then is its
  `friend.status` (what it's playing, from the runner's track) taken in. On exit the app tells the
  helper to stop first (`SharedPeerNetwork.Stop`), so friends see it leave at once rather than when
  the connection times out.
- **Listening parties (`Core/Party/`)** — `ListeningParty`, over the friends' verified connections
  (`Friends` passes any non-`friend.*` message from a proven friend's PC on as `PeerMessage`, and a
  friend's status carries whether they're hosting). The host tells its guests its player's state
  (`IPartyHostSource`; the app's is `PlayerPartySource`): the song (`PartySong`: a key per play, the
  host's own source for it, a station as `Live`), the position read at a time on the host's clock,
  and pause. It sends on a new song, a pause, a position jump (a seek), and every 5 s. On the queue a
  song is the item in play (`QueueSource.PlayingNow`), paused until it has started so guests wait
  while it loads, and nothing between songs (not the last one, paused). A guest learns the host's
  clock from ping/pong round trips (`PartyClock`, the fastest kept) and plays each song through
  `IPartyPlayer` (the app's is `QueuePartyPlayer`): the host's source if it works here, else this
  PC's search by title and artist, played next ahead of the queue (`QueueModel.PlayNext`) from where
  the host is the moment it starts (`QueueItem.JoinAt`, evaluated through `PumpContext.StartPosition`).
  It follows pause before anything else (a paused queue never starts its next song), seeks when more
  than 0.6 s off, and logs how far off it is every 10 s (`[hzn-party] in step with the host to …`).
  A station (`PlayableItem.LiveOrigin`) is tuned into, not followed.
  The queue is shared, as in a jam: the host's is the party's. The host sends it (`party.queue`,
  `IPartyHostSource.Queue`: its explicit songs keyed by queue item id, plus its mix's name and a peek)
  whenever it changes. A guest's `QueueModel` is shared (`QueueModel.Share`): its own songs and mix
  are set aside until `Unshare`, the host's list is shown (`Mirror`), and every user change (add,
  remove, move, play now, clear, Next, Previous) goes to the owner (`IQueueOwner`), which the party
  sends to the host as `party.ask` and the host applies to its own queue (`ChangeQueueAsync`). A
  shared queue's engine only plays what the party puts on (`PlayNext`), but loads the host's next
  song ahead. A song from the other side is a `PartySongItem`: found on this PC
  (`PartySongFinder`: the same source, else search by title and artist) the first time it's loaded
  ahead, looked up for the list, or played, then played as what was found.
- **Lyrics (`Plugins/Lyrics/`, `Core/Lyrics/`)** — plugins register `ILyricsProvider`s
  (Unison, LRCLIB, and VocaDB, whose metadata plugin registers one too; when VocaDB has the
  song but no lyrics it reads the Vocaloid Lyrics
  Wiki page that links that VocaDB song id, `VocaloidLyricsWiki`). `LyricsService` asks them all at once and keeps the first synced
  answer in `SortOrder`, else the first unsynced one with variants (romanized,
  translated), else any; a lookup where a provider failed isn't remembered. Karaoke
  fill is laid out by `Karaoke.Words` (word-timed syllables grouped into words; a
  line-timed line spread over its characters) and drawn by `KaraokeText`. The query
  carries the playing audio's length (`IPlaybackProgress.Duration`): synced lyrics are
  only accepted when timed to that length (±2 s on LRCLIB), because a file timed to
  another cut drifts. Lyrics follow `IPlaybackProgress.SongPosition`: the position for a
  track with a length; for live radio, the time since the song's title change *in the
  audio*. `IcyStreamReader` reports each title with the audio byte it arrived at, and
  `StreamClock` turns bytes into decoded time (the `icy-br` rate, or the measured one
  when the header is nominal). The stream is read seconds ahead of playback, so
  `RadioPlayableItem` holds each title until its audio plays: titles change with the
  song, in game too. Stations time their title changes loosely (seconds early or late),
  and the song playing when a station is tuned in has no position at all, so when a
  radio song has synced lyrics, `LiveSongSync` (`Core/Fingerprints/`) finds its place
  from the audio: `RadioPlayableItem` keeps what's heard (`LiveAudioBuffer`, mono at
  11025 Hz) and exposes it as `ILiveSongAudio` (via `IPlaybackProgress.LiveAudio`); a
  recording of the song (the VocaDB videos the resolver returns as `Playables`, then
  YouTube search hits) is fetched whole through an `IReferenceAudioSource` (the YouTube
  source: yt-dlp downloads to stdout, piped into ffmpeg), both are fingerprinted with
  Chromaprint (AcoustID.NET), and `RecordingMatch` slides one over the other. The
  thresholds were measured on a real station: the same recording differs in 1–8% of
  bits, another version of the song 13–18% (and lands a few tenths off, so it's only a
  fallback), other songs 35%+; a repeated section fits twice and waits for a longer
  stretch. The fit is refined to ~0.015 s by re-fingerprinting at sub-frame shifts.
  Uploads pad the music differently (0.01–7.5 s of silence measured), so a song's time
  counts from the recording's first sound (`Silence`), which puts every upload within
  ~0.02 s of the others. A recording that opens with a prelude with sound (a music
  video's scene) is caught the first time a station is heard starting the song: the
  station settles on the recording far past its first sound, so the song starts where
  the station changed its title (`StoredReference.StationStart`, kept for tuned-in
  plays). Short of that, settling late is the station's crossfade (0.7–3.3 s measured),
  not a different start.
  Fingerprints are stored per song (`ReferenceStore`, `%LOCALAPPDATA%\HorizonRadio\fingerprints`),
  so a song is downloaded once. With matching off or no match, radio lyrics follow the
  title change and can be nudged earlier/later (`LyricsSettings.RadioOffset`).
- **Metadata matching (`Sdk/Metadata/`, `Core/Metadata/`)** — every catalog plugin scores results with
  `SearchTerms.MatchScore`, which only accepts a song whose name and artist agree with the
  query. A query with no artist, or one naming only a singing voice
  (`Voicebanks.IsVoicebankCredit`: "Hatsune Miku"), can't confirm a match, so
  `MetadataResolver` asks the catalogs that know which version is the original first
  (`IMetadataProvider.IdentifiesOriginals`; VocaDB, which picks the voice's most popular
  original with exactly that name) and asks the others with the artist they found.
  Each catalog caches its answers (`MetadataCache`); a miss is kept for two weeks, so when
  matching improves, bump `MetadataCache.CurrentMatchVersion`: misses from older matching
  are then looked up again at once.
- **Threads** — Core knows nothing of the window: no service posts to a UI thread. Each keeps its
  own state safe (a lock, or a loop of its own: the router, the queue engine, devices, friends,
  parties) and raises events on the thread that made the change; a view model that updates bound
  state from one marshals it (`Dispatcher.UIThread.Post`). Local preview (`PreviewController`) runs
  its state machine under a lock and hands device callbacks to the thread pool.
- **Plugins and the container** — `App` builds one DI container: Core's services
  (`AddHorizonCore`), the host's, then the plugins (`PluginRegistration.AddPlugins`, fed by
  `PluginDiscovery`'s assembly scan). A plugin is an `IPlugin` (its id, name and order) and,
  usually in the same class, an `IPluginModule` (public, parameterless) that registers what it
  contributes: sources (`IAudioSourceFactory`), metadata providers (`IMetadataProviderFactory`:
  factories, since they're built from the user's settings) and lyrics providers
  (`ILyricsProvider`). Tool plugins (`IToolPlugin`: a tool's descriptor and installer) are found
  the same way and ordered like the rest. The app collects each kind from the container (`GetServices<T>()`;
  the sources as the `SourceCatalog` service, which also searches them, and the metadata providers as
  `MetadataCatalog`) and
  orders it by `SortOrder`, then id (`InDisplayOrder`; `IOrdered`). Contributions are built by the
  container, so their constructors take the host's services (`IPluginLog`, `IProcesses`,
  `IAppFolders`, `IToolLocator`, `IMetadataCache`, `IMetadataTrace`, `IAudioCaches`,
  `IPluginSettingsStore` (a plugin's own settings), `INotifications` (a toast, or a log line
  headless),
  `ISourceSettings` (a source's saved settings, for work outside a player such as search),
  `ITitleModel` (the optional title model, read each time: it's replaced on install; the host's
  only knows the setting, and `HorizonRadio.TitleModel`'s module wraps it with the installed GGUF),
  `IConfirmations`) and the module's own; tool plugins register their wrappers the same way
  (`YtDlpClient`). Run a tool to its end with `IProcesses.RunToEndAsync` (logs the command and its
  stderr), and stream one's audio with `SubprocessPcmSource`. A game plugin (`IGamePlugin`) is the exception for now: the container builds it,
  and it builds its integration. A module may also replace a host service or wrap it
  (`Decorate`); modules run after the host in `Order`, then type name, the last registration wins,
  and the Console tab (tag `plugins`) says what each replaced or wrapped. The container is built
  with `ValidateOnBuild`, so a constructor asking for something nobody registers fails at startup.
  Plugins reference only the SDK (and other plugins they build on), never Core.
- **Game plugins (`Plugins/Games/`)** — the Horizon Radio side of a game
  integration. An `IGamePlugin` (SDK) builds one `IGameIntegration`: the game's
  audio output, its event sources, connection state, and optional capabilities
  (station targeting, in-game volume, now-playing display). The app talks to the
  active game only through that interface, and exactly one runs at a time.
  The active game is a user setting (`GameSelection.Chosen`: a plugin id or
  `"none"`). `NoGameIntegration` (Core) is the built-in "No Game": no output (local
  speakers become the only destination), no events, pages, or capabilities.
  `GameSelection.Resolve` picks the startup game: the saved one, else a game the
  user already has settings for, else the first-run picker, which runs before
  anything game-related is composed. Switching games saves and relaunches, since
  the audio pipeline, events, and pages are wired once at startup.
  `HorizonRadio.Game.ForzaHorizon6` wraps the IPC/PCM pipe clients, Forza Data
  Out telemetry, the mod installer, and the station list. Code that runs *inside*
  a game lives in `Mods/<Game>/` instead.
  - A game plugin can add **pages** (`IGameIntegration.Pages`), listed in the
    sidebar under the game's name, and a setup **notice** shown as a banner. The
    SDK stays UI-free: a page is just a view model, and the shell's `ViewLocator`
    finds its view by naming convention (`…ViewModels.FooViewModel` →
    `…Views.FooView`) in the plugin's own assembly. FH6 contributes Mods and
    Stats. Plugin UI packages must match the UI project's versions.
  - A game owns its game-specific settings, persisted per plugin
    (`IPluginSettingsStore.ForPlugin(id)` → `plugin.<id>.<key>` in the settings store). FH6's Stations
    page assigns each station the game's music, the app's player, or a mix
    (`StationMap`); as the player tunes, the plugin routes the app's playback
    through `IGameHost.Playback` (`PlaybackRouter`). Mixes stay game-agnostic.
  - **Playback routing:** one engine (`SourceRunner`), one owner
    (`PlaybackRouter`), several channels — the player's queue and one
    `QueueSource` per mix. Only the router starts, stops or parks a source (the
    runner's mutators are `internal`). The game's routes, the queue getting work
    (`QueuePlayback.EnsureActiveAsync`), a self-driven source started or stopped
    from the Sources tab, the player bar or a bound action (`StartSourceAsync`,
    `StopAsync`), and mix edits are commands that one loop applies in order against
    an explicit state (Silent, OnPlayer, OnMix); rapid tuning collapses to the
    latest route. Leaving a channel parks it (`SourceRunner.ParkAsync`; the engine
    notes its own place as it stops); returning resumes per the mode: Resume (same
    track, mid-track where the item can seek, via `PumpContext.StartPosition`),
    Restart, or Live (fast-forward by elapsed time, manual pause locked). A new mode
    for the station that's on only changes the mode. While a mix station is on,
    player additions wait in the queue. Stop parks a station (the game routing to
    it again resumes it); a self-driven source started on a station takes the
    player's place. A channel keeps the mix it was built from, so editing or
    deleting a mix drops its channel (an on-air one once it stops). The shuffle
    preference (`SetShuffleAsync`) reaches every channel, on or off, as it
    (re)starts.
  - **The queue engine** (`QueueSource`) is one state machine (Idle, Picking,
    Playing, Parked) that only its run loop moves on, under one lock. Transport,
    play-now and mix switches only record a request and cancel the step under way
    (one pick plus the play after it); the loop acts on the merged request between
    steps. `QueueModel.Generation` lets it tell a jump it already honoured from a
    newer one, and drop a track the old context resolves after a switch.
    Once a track has started (and whenever the queue changes), it loads the next one ahead
    (`PlayableItem.PrefetchAsync`, 2 s later): a track stepped back over, the queue's next, or the
    context's (`IQueueContext.PeekNextAsync`; a mix loads its next entry then, and `NextAsync`
    hands out that same item). A YouTube item downloads itself there (`YtDlpClient.DownloadAudioAsync`)
    into the `AudioCache` (`cache\audio\youtube`: a folder per video, moved into place only when
    whole; one download per song at a time, two at once; past the size setting the songs used
    longest ago go), and plays and seeks from the file; one that isn't downloaded yet streams, and
    downloads meanwhile. It seeks by starting ffmpeg again at the new place.
  - The FH6 plugin also bundles its mod: its csproj flows
    `Mods/ForzaHorizon6/build/Release/version.dll` into the app's build output
    (`ExcludeFromSingleFile`), and the UI's publish step copies it next to the exe.
- **version.dll (C++)** — a `version.dll` proxy injected into FH6. It hosts an
  FMOD DSP that mixes our PCM into the game's radio channel, writes the current
  track's title/artist into the game's HUD, and exposes a named pipe to the UI.
- **IPC** — newline-delimited UTF-8 JSON. The DLL publishes `track`, `stats`,
  `source_changed`, `game_event`, and `debug` events; the UI sends `set_track`,
  `set_gain`, and `set_target_station` commands.

For the code-level internals of the native side — thread model, audio/metadata
data flow, signature resolution, the SEH strategy, and the full IPC wire
protocol — see [`Mods/ForzaHorizon6/docs/architecture.md`](Mods/ForzaHorizon6/docs/architecture.md).

## Building

Code that runs inside a game lives under `Mods/<Game>/`, each with its own
toolchain; everything that plugs into Horizon Radio itself is .NET. The FH6
mod (`version.dll`) is a self-contained CMake project in `Mods/ForzaHorizon6/`
(`Bridge/` = the DLL, `Tests/` = the doctest exe) built via the `windows-x64`
preset; the C# side builds with `dotnet` from the repo root. They are
intentionally decoupled so a CMake / .NET resolver mishap on one side can't
take down the other.

C++ DLL + tests (run from `Mods/ForzaHorizon6`):

```powershell
cd Mods/ForzaHorizon6
cmake --preset windows-x64
cmake --build --preset windows-x64-release      # → Mods/ForzaHorizon6/build/Release/version.dll
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

# Configure + build (from Mods/ForzaHorizon6)
cmake --preset macos-cross-x64                  # or linux-cross-x64
cmake --build --preset macos-cross-x64-release  # → Mods/ForzaHorizon6/build/Release/version.dll
```

The toolchain requires lld specifically (not mingw-w64's bundled BFD
ld): clang+gcc-libstdc++ produces RTTI section size mismatches that
BFD ld refuses but lld merges cleanly. clang+mingw also drops the C++
stdlib include search paths and SEH support — the toolchain file
(`Mods/ForzaHorizon6/cmake/toolchain-clang-mingw.cmake`) wires those back up.

Tests build under the cross preset too, but the resulting
`HorizonRadio.Tests.exe` is a Windows binary — running `ctest` from a
non-Windows host won't work without Wine. The Windows CI flow is the
authoritative test runner.

The 17 `version.dll` exports come from `Bridge/src/version_proxy.cpp` —
plain `__declspec(dllexport)` trampolines that lazy-load
`C:\Windows\System32\version.dll`. They compile identically under
MSVC and clang+MinGW; if you add or remove a forwarded export, edit
that file, not the linker flags.

C# UI / Core — if `dotnet build` crashes with `MSB4014: The path is empty`, an
empty `MSBuild*` env var is to blame; clear it first:

```powershell
Remove-Item Env:MSBuildSDKsPath -ErrorAction SilentlyContinue
Remove-Item Env:MSBuildAdditionalSdkResolversFolder -ErrorAction SilentlyContinue
dotnet build HorizonRadio\HorizonRadio.UI\HorizonRadio.UI.csproj -c Release
```

### The peer helper (Go)

Multiplayer and device sync run over `horizon-peer.exe`, a go-libp2p helper in
`Plugins/Tools/HorizonRadio.Tools.Peer/Helper/` (Go; `go.mod` pins the toolchain). Like the
native DLLs it builds separately, and `HorizonRadio.Tools.Peer.csproj` bundles it next to the
app when it's been built:

```powershell
cd Plugins/Tools/HorizonRadio.Tools.Peer/Helper
go build -trimpath -ldflags "-s -w" -o build/horizon-peer.exe .
go test ./...      # peers on go-libp2p's simulated network (NAT, relay, hole punching)
```

The app drives it through `PeerHelper` (JSON lines over stdin/stdout; the protocol is in the
helper's README). Without Go the app still builds, and `PeerHelperIntegrationTests` don't run
locally (CI builds the helper first, and fails them if it's missing). Before pushing Go changes,
`gofmt -l .` must print nothing and `go vet ./...` must pass; CI also runs the tests with `-race`.

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

### Profiles: two instances on one machine

Every file the app keeps (settings, history, mixes, caches, logins, logs) lives under one
root, `AppPaths.Root` (`Core/IO/AppPaths.cs`): `%LOCALAPPDATA%\HorizonRadio`. Build new
paths with `AppPaths.In(...)`, never from `SpecialFolder.LocalApplicationData`. Started with
`--profile <name>`, the app uses `HorizonRadio\profiles\<name>` instead, so a second instance
with its own settings and logins can run beside the first (testing multiplayer on one
machine):

```powershell
HorizonRadio.UI.exe --profile test
```

Downloaded tools stay shared (`AppPaths.SharedRoot\tools`). A profile's window title and its
Spotify Connect devices are marked with its name (`AppPaths.Label`: "Horizon Radio [test]"). Only
one instance can drive FH6 (the mod's pipes take one client), so pick "No Game" for the other.

### Settings

Every setting lives in one file, `settings.json` in `AppPaths.Root`, behind one store
(`Core/Settings/SettingsStore.cs`). A setting is declared next to the code that owns it, with a
key, a scope and a default, and read and written through the store:

```csharp
public static readonly Setting<bool> Karaoke = new("lyrics.karaoke", SettingScope.Synced, true);

settings.Get(LyricsSettings.Karaoke);
settings.Set(LyricsSettings.Karaoke, false);   // saved ~300 ms later; Changed fires now
using var watch = settings.Watch(LyricsSettings.Karaoke, on => ...);
```

- **Scope** says what syncing your devices may do with it: `Synced` (a preference that
  follows you), `Device` (a path, an audio device, the game installed here), `Secret`
  (text, encrypted with DPAPI, never leaves the machine). Pick it when declaring a setting.
- Each value is stored with its scope and the time it last changed (to the millisecond), for
  last-writer-wins sync: `SyncedSettings()` hands another device every synced setting, and
  `MergeSynced` takes in the ones changed there later (a tie goes to the larger value, so both
  sides agree; Device and Secret settings never move). A removed synced setting is kept as
  `"removed": true` with its time, so a sync can't bring an older value back. A setting that's
  unset reads as its default and isn't written.
- Keys only known at run time use the raw members (`TryGetRaw`, `SetRaw`,
  `KeysWithPrefix`) or a `Setting<T>` built for the key. A settings form (a `ConfigField`
  schema) is one setting per field (`ConfigFormSettings`): each source's is
  `source.<id>.<field>` (`SourceConfigStore`), each metadata provider's
  `metadata.provider.<id>.<field>` (`MetadataConfigStore`), both device-only with secret fields
  as secrets. Each plugin's settings are `plugin.<id>.<key>` (`PluginSettings`): device-only unless the plugin
  names the keys that are preferences (`IPluginSettingsStore.ForPlugin(id, synced)`; FH6's station
  assignments and switch mode are), and it follows changes through `IPluginSettings.Changed`;
  each event rule `event.<kind>` (`EventRuleStore`), and the input bindings one list,
  `controls.bindings` (`InputBindingStore`), since a binding replaces others.
- Enums are stored by name. Every write is saved without being asked: there are no
  `SaveToDisk` calls for settings.
- The store is opened before anything else at startup (`App`), registered in DI, and
  disposed last, which writes anything still pending.
- `settings.json` replaced `sources.json`, `plugin-settings.json`, `metadata-config.json`,
  `events-config.json`, `controls-config.json` and `diagnostics\settings.json`.
  `LegacySettingsImport` brings each file over once, records which it has handled
  (`settings.imported`, so a file added to its list later still comes over), and leaves
  them in place. History, mixes and the caches are data, not settings, and keep their files.

## In-game title injection — and re-deriving it after a game update

The DLL writes our title/artist into the game's now-playing metadata block. The
chain and offsets live in `kFh6Metadata`
(`Mods/ForzaHorizon6/Bridge/include/horizon/fmod/signatures.hpp`):

```
RadioStreamFmod instance  +0x50 ─► track list  +0x08 ─► metadata block
metadata block:  +0x10 internal event id (NOT written)   +0x30 title   +0x50 artist
```

We write the title to **only the instance we attach audio to**, snapshot that
block's original strings, and restore them when we stop replacing the station
(tracking the game's own track changes so the restore isn't stale). Fields are
MSVC `std::string` (32-byte SSO/heap header).

**These offsets shift when Forza updates.** Symptom: titles stop appearing (the
periodic writer's write count stays 0). First, check the UI **Console** tab
(tag `bridge`). On every connect the DLL reports the game build (`.text` size,
known or UNKNOWN) and which pieces resolved (`fmod=… metadata=… radio_state=…`).
Anything `MISSING` after the ~10 min retry window is a signature that drifted.
To re-derive them:

1. With the DLL deployed and a song playing on the target station, create an
   empty file `horizon-radio.discover` next to `version.dll`.
2. Open the UI **Console** tab and read the dump (tag `discover`). It lists the
   active `RadioStreamFmod` candidates, follows their heap pointers two hops,
   and flags any MSVC-string / ASCII slots it finds.
3. Find the candidate for the main radio bus (`/Master/Radio/Track`, not
   `…/TrackLFE`), follow it to the block whose string slots hold the song's
   real title/artist, and read off the chain + field offsets.
4. Update `kFh6Metadata.chain_offsets` and the field offsets; rebuild; redeploy.
5. Once titles and audio work again, add the new build's `.text` size (from the
   `bridge` Console line) to `kFh6KnownBuilds` in `signatures.hpp`.
6. Check the station codes: with something playing, tune through every station
   and read the Console's `titles` lines (`tuned=<station> audible=<event id>@…`).
   Each event id's `HZ6_R<n>_` code must match `kFh6StationCodes` in
   `station_codes.hpp`; if they've moved, titles go quiet (a mismatch never
   writes), so update the table.

Cross-reference: [matkhl/FH6-Spotify-Radio](https://github.com/matkhl/FH6-Spotify-Radio)
(GPL-3.0, archived) resolves the same structures. Its FMOD and RadioState
signatures match ours byte for byte as of its June 2026 release. Its metadata
chain differs (`+0x58 → +0x18` vs our `+0x50 → +0x08`) and may be another route
to the same block, so if ours breaks, try theirs in the discovery dump.

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
dotnet format HorizonRadio.slnx --verify-no-changes
dotnet build HorizonRadio.slnx /t:Rebuild
```

Native code: `.clang-format` + `.clang-tidy` in `Mods/ForzaHorizon6/` (install LLVM).

```powershell
cd Mods/ForzaHorizon6
clang-format -i (rg --files Bridge Tests -g '*.cpp' -g '*.hpp')
cmake --preset windows-x64-analysis
cmake --build --preset windows-x64-analysis-debug
```

The `analysis` preset flips on `CMAKE_CXX_CLANG_TIDY` so every TU is
linted during compile. It's ~3× slower than a plain build; keep it
off for the dev iteration loop.

The Spotify plugin's audio tap (`Plugins/Sources/HorizonRadio.Source.Spotify/Native/`) is
its own CMake project with the same presets and the same `.clang-format`/`.clang-tidy`
(keep them in step with the mod's):

```powershell
cd Plugins/Sources/HorizonRadio.Source.Spotify/Native
clang-format -i (rg --files include src Tests -g '*.cpp' -g '*.hpp')
cmake --preset windows-x64; cmake --build --preset windows-x64-debug; ctest --preset windows-x64-debug
cmake --build --preset windows-x64-release --target horizon_audio_tap   # what the plugin bundles
cmake --preset windows-x64-analysis; cmake --build --preset windows-x64-analysis-debug --target horizon_audio_tap
```

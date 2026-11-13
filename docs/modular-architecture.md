# Modular architecture

Where the .NET side of Horizon Radio is heading, and the order we're getting there in. The
app is moving from "one UI project that wires everything, a Core that plugins reach into" to
a small set of projects with one direction of dependency, so that:

- plugins build against a small, stable SDK instead of the whole engine;
- the engine runs without the window (a headless player, a CLI);
- macOS and Linux can be supported by adding platform code, not by untangling Windows code.

`AGENTS.md` describes the code as it is today; this file describes the target. Each step of the
[roadmap](#roadmap) moves code toward it, so check the roadmap to see which parts already exist.

## Projects

| Project | What it is | References |
|---|---|---|
| `HorizonRadio.Sdk` | The plugin contract: plugin, source, metadata, lyrics, game and tool interfaces, the services the host provides (`IPluginLog`, `IProcesses`, `IAppFolders`, `IToolLocator`…), the plain models they pass (`Track`, `PlayableItem`, `ConfigField`…), and helpers that need nothing beyond .NET. | nothing of ours; one package, the DI abstractions |
| `HorizonRadio.Sdk.UI` | The app's look: color tokens, text styles, list-row styles, and the controls pages are built from (`PageHeader`, `Section`, `SettingRow`, `Notice`, `EmptyState`, `StatusAvatar`, `PathPicker`, `SongRow`, `ArtTile`…). | Avalonia, ShadUI, Material.Icons |
| `HorizonRadio.Core` | The engine: playback, queue, mixes, history, metadata, lyrics, settings, devices, friends, party; the command list every front end drives; the interfaces it reaches the OS through; and starting an instance (`Core/Hosting/`: plugin discovery, startup order, the control endpoint). | `Sdk` |
| `HorizonRadio.Platform` | The OS behind Core's interfaces: audio output, secret storage, child processes, app folders. One project, a folder per OS, picked at run time; it registers like a plugin module, replacing Core's portable defaults. | `Core` |
| `HorizonRadio.UI` | The Avalonia front end: views and view models, plus components that need the app's view models (the song right-click menu, `SongMenu`). | `Core`, `Platform`, `Sdk.UI`, plugins |
| `HorizonRadio.Cli` | `hzr`, the command-line front end. | `Core`, `Platform`, plugins |
| `Plugins/*` | Sources, metadata, lyrics, tools, games. A plugin may reference another plugin it builds on (Spotify on librespot). | `Sdk`, plus `Sdk.UI` for plugins with pages |

```
HorizonRadio.UI  ─┬─ Core ── Sdk
                  ├─ Platform ── Core
                  ├─ Sdk.UI
                  └─ Plugins/* ── Sdk (+ Sdk.UI for pages)
HorizonRadio.Cli ─┬─ Core
                  ├─ Platform
                  └─ Plugins/*
```

## Rules

Each rule says where a kind of code goes; the reason follows it.

- **A plugin references `HorizonRadio.Sdk*` and other plugins.** What a plugin needs from the
  host (app folders, launching processes, logging, the tool locator, diagnostics) it takes in its
  constructor: the host's DI container builds plugin classes, so a test host, the CLI, or another
  OS supplies its own by registering it.
- **A plugin registers what it adds, and its own services,** in an `IPluginModule` (usually the
  `IPlugin` class itself): its sources, metadata and lyrics providers are registrations the host
  collects (`GetServices<IAudioSourceFactory>()`), not members of the plugin. A contribution with
  user settings is a factory (sources, metadata providers); one without is the thing itself
  (lyrics providers). Spotify builds its connection and players itself; the host only discovers
  plugins and runs them, so it never names a plugin's internals.
- **A plugin may replace or wrap a host service.** Modules run after the host's registrations,
  in a fixed order (`Order`, then type name), so for a single service the last registration wins;
  `Decorate` wraps one instead. The host logs each replacement and warns when two modules replace
  the same service. Contracts with many implementations (sources, metadata providers) are
  collected, so a plugin adds one alongside the others. For a replacement to take effect
  everywhere, Core reaches anything replaceable through its SDK interface, not a static.
- **A view model only translates between the engine and the screen.** Logic the CLI would also
  need is a Core service the view model calls (`MixPlayer.PlayAsync(mix, mode)`), and a question
  the UI asks the user (replace or add?) is a parameter of that service.
- **Core doesn't know what front end it runs under.** It has no UI thread and no way to post to
  one: each service keeps its own state safe (a lock, or a loop of its own) and raises its events
  on whichever thread made the change. A front end that needs its own thread (the window's)
  marshals in its handlers. So the window, the CLI and a test host all run the same engine.
- **Core and the SDK stay OS-neutral.** OS-specific code lives in `HorizonRadio.Platform` behind
  an interface Core declares, marked `[SupportedOSPlatform]` so the platform analyzer flags an
  unguarded call.
- **Pages build from `Sdk.UI`.** Colors come from its tokens, text from its styles, repeated
  layouts from its controls, so the app and plugin pages look and behave the same.
- **Commands are plain service methods**, plus a small catalog of named actions for key
  bindings, event rules and the control endpoint. No mediator or command-bus framework.

## What goes in the SDK

The SDK is the contract between the host and its plugins; Core is the host's implementation. A
type goes in the SDK only when a plugin has to name it, and then only as one of:

- what a plugin implements (`IAudioSourceFactory`, `IMetadataProvider`, `IGamePlugin`,
  `IToolPlugin`);
- what the host offers plugins, as an interface (`IPluginLog`, `IProcesses`, `INotifications`,
  `ISecretProtector`, `IGameHost`);
- the plain data passed across (`Track`, `ContentRef`, `ConfigField`, `MixInfo`);
- a helper that keeps no host state and needs nothing beyond .NET (search-term matching, lyrics
  parsing, `AtomicFile`).

Anything that keeps the app's data or knows how other parts of the engine work stays in Core,
and plugins see at most an interface over it. Every SDK type is a promise to plugins built
against it, while Core can change freely behind it.

**No plugin references Core.** The projects that do (the front ends, `HorizonRadio.Platform`)
are parts of the host, not plugins. A plugin that seems to need Core is a sign that an SDK
interface is missing.

**When a plugin needs something the engine does** (build or play a mix, make sure a tool is
installed), the SDK gets a narrow interface for it, as `IGameHost.Playback` (`IPlaybackRouter`)
already lets a game steer playback and list the user's mixes:

- Its signatures use SDK models, never Core's: the router hands out `MixInfo`, not `Mix`, so
  Core can change how mixes are kept without breaking a plugin.
- Core implements it over the service that does the work (step 9), so a plugin goes through the
  same steps the window does (saving, syncing to your devices, tidying caches) and can't skip one.
- It's registered in the container like the other host services, so a test or a module can
  replace it.
- It says which thread its events come on and whether its calls return before the work is done,
  since a plugin can't read Core to find out.
- It's added when a plugin needs it, not ahead of time: each one has to stay compatible.

A plugin can also **contribute** actions: its commands join the command list the way its
sources join the source catalog, so they can be bound to keys, used in event rules and called
from `hzr`. Plugins call the host through typed interfaces; named commands are for users and
remote control.

## Front ends and the control endpoint

One instance of the engine runs per profile: the window, or `hzr serve` (headless). The running
instance opens a local control endpoint (a Unix domain socket, which .NET supports on every OS;
newline-delimited JSON, like the mod's IPC and the peer helper), takes commands from the
command list, and streams events (what's playing, queue changes). `hzr` sends its command
through the endpoint when an instance is running, and otherwise runs it in-process, starting a
headless instance for playback commands. With an instance running, everything goes through it:
only one process may write the settings, mixes and history, drive the game, or be the Spotify
device.

## Operating systems

- **Plugins** declare the OSes they run on with `[SupportedOSPlatform]` on the plugin class;
  discovery skips a plugin this OS can't run (the FH6 integration is Windows-only).
- **Tools** are downloaded per runtime identifier (OS and architecture, `osx-arm64`): each
  installer detects the platform and fetches the matching build, and a tool with no build for
  the platform shows as not available.
- **Sources** follow their tools: a source copes with a missing tool already, so it rarely needs
  its own declaration.

## Later, by design

These are agreed but wait until something needs them:

- The window attaching to a running headless instance (so music keeps playing with the window
  closed). View models reach the engine through interfaces, which keeps this a transport change.
- Splitting a game plugin into its engine and its pages (`Game.ForzaHorizon6` and
  `Game.ForzaHorizon6.UI`), so a headless instance loads no Avalonia. Needed once the CLI loads
  game plugins.
- Splitting `HorizonRadio.Platform` per OS, once a dependency forces it (Windows media controls
  need the `net10.0-windows` target).

## Roadmap

Each step leaves the app working and lands as its own PR or short stack.

1. [x] Move `Plugins/HorizonRadio.Plugins.Abstractions` to `HorizonRadio/HorizonRadio.Sdk`, with
   its types in `HorizonRadio.Sdk.*` namespaces.
2. [x] `HorizonRadio.Sdk.UI`: color tokens and text styles; the app and FH6 reference it, and
   hard-coded colors go.
3. [x] `Sdk.UI` controls: page layout (`PageHeader`, `Section`, `SettingRow`), status
   (`Notice`, `EmptyState`, `StatusAvatar`, `StatusDot`), `PathPicker`, list rows (`listRow`,
   `SongRow`, `ArtTile`).
4. [x] A confirm service (in the SDK for plugins, used by the app) and confirmation on every
   destructive action.
5. [x] Plugins take host services from the container and register their own; plugin helpers
   move into the SDK; plugins drop their Core reference. (This was two steps, a richer
   `IPluginContext` then plugin-registered services; constructor injection does both, so the
   context goes instead of growing.)
   - [x] The self-contained helpers (search-term matching, lyrics parsing, audio format and
     playlists, tool hash checks, the peer-network contract, build info) are in the SDK; the
     LRCLIB, Unison, local-files and test-tone plugins reference only the SDK.
   - [x] Plugin namespaces match their assemblies (`HorizonRadio.Lyrics.Lrclib`; they were
     under `HorizonRadio.Core`), so every Core type a plugin uses takes a `using` line and shows.
   - [x] The host's container builds the plugin classes; `IPluginModule`s add a plugin's services
     and may replace or wrap the host's; the SDK declares the host's services (log, processes,
     app folders, tool locator), implemented over Core's existing code.
   - [x] Tool installers: `ToolInstallerBase` and `AtomicFile` are in the SDK, and the host says
     where each install goes (`ToolInstallTarget`); the ffmpeg and title-model plugins reference
     only the SDK.
   - [x] Metadata and lyrics plugins take the metadata cache and diagnostics (`IMetadataTrace`) in
     their constructors; provider factories and lyrics plugins no longer get `IPluginContext`. The
     iTunes, MusicBrainz and VocaDB plugins reference only the SDK (Spotify's metadata still rides
     on the Spotify source's connection).
   - [x] Contributions are registrations: a plugin is an `IPlugin` plus an `IPluginModule` that
     registers its sources, metadata providers and lyrics providers, each ordered by its own
     `SortOrder`; `ISourcePlugin`, `IMetadataPlugin` and `ILyricsPlugin` are gone.
   - [x] YouTube, with yt-dlp: the factory takes its services by constructor, `YtDlpClient` is a
     service the yt-dlp plugin registers, `AudioCache` and `SubprocessPcmSource` are in the SDK
     (the host hands out caches, `IAudioCaches`), a source reads its saved settings through
     `ISourceSettings`, and `YouTubeRuntime` is gone. The YouTube and yt-dlp plugins reference only
     the SDK.
   - [x] Radio: its services come by constructor (`RadioServices`), the directory client is a
     service, `LiveAudioBuffer` is in the SDK, and the title model is `ITitleModel`. It references
     only the SDK.
   - [x] Spotify, with librespot and Soloist: the plugin registers its connection, players and
     settings (`SpotifySettings`), its sources and the Spotify metadata provider take them by
     constructor, and `SpotifyRuntime` and the app's Spotify wiring are gone (the Connect receiver
     frees the driven librespot itself). The Spotify, librespot and Soloist plugins reference only
     the SDK. Left for later, both since done: the login token's DPAPI encryption (the host's
     `ISecretProtector`, step 8) and the Soloist pairing toast (`INotifications`, step 6).
   - [x] The peer helper: its plugin registers the app's peer network (`IPeerNetworkFactory`, in
     the SDK, so a plugin can bring another), started through `IProcesses`; it references only the
     SDK.
   - [x] The audio helpers and the title model reach plugins through the host: caches
     (`IAudioCaches`), subprocess audio and the heard-audio buffer in the SDK, `ITitleModel`.
     Fingerprinting stays in Core, which is all that uses it.
   - [x] FH6 takes its settings (`IPluginSettingsStore`), confirmations and log by constructor, and
     `IGamePlugin.Create()` no longer gets a context: `IPluginContext` is gone. FH6 already reached
     the engine only through `IGameHost`. No plugin references Core.
6. [x] `App.axaml.cs` stops naming plugin internals: the catalogs (sources, metadata, tools) are
   services, and Core reaches replaceable services through their interfaces.
   - [x] `App` names no plugin: tool plugins come from the container like the rest (the static
     tool list is gone), and plugins tell the user things through `INotifications` (the Soloist
     pairing prompt is the Spotify plugin's).
   - [x] `SourceCatalog` and `MetadataCatalog` are services built from the container (the source
     catalog carries its `UnifiedSearch`; the mix resolver, the queue and the router expose it, so
     most of the app reaches it through what it already holds).
   - [x] `ToolCatalog` is a service from the tool plugins and does what `ToolsPaths` and
     `ToolResolver` did (where tools install, finding a configured, bundled or installed copy); the
     host's `IToolLocator` answers from it, and whether a source has its tools is the source
     catalog's question (`SourceCatalog.EnsureToolsAvailable`).
   - [x] Core reaches what a plugin can replace through its interface (the Console log, process
     launching, folders, the tool locator), so a replacement applies to Core's own use too. (Done
     with step 7's last item: Core starts tool processes and finds tools only through the host's
     `IProcesses` and `IToolLocator`, and logs and keeps its files through `IPluginLog` and
     `IAppFolders`.)
7. [x] `Core/Hosting/`: the startup sequence, plugin discovery, plugin context, game host and
   tool registry move out of `HorizonRadio.UI`.
   - [x] Plugin discovery (`Core/Hosting/PluginDiscovery`), the tool registry (a service, beside the
     tool catalog) and the game host (`Core/Games/GameHost`) are in Core. (The plugin context is
     gone: plugins take services by constructor.)
   - [x] Core has no UI thread: local preview keeps its state machine under a lock of its own and
     the mix store is safe from any thread, so a merge from another device no longer goes through
     the window's dispatcher. Front ends marshal the events they show.
   - [x] Starting up is Core's: `HorizonHost` (log file, settings, the container with the plugins,
     migrations, the game to run) and `HorizonEngine` (the engine's services for that game, wired
     together, started once the front end has subscribed, and stopped in the order that matters).
     The title model is a module wrapping the host's `ITitleModel`. `App` only adds the window; a
     headless host runs the same engine.
   - [x] Core's own services log and find their folders through `IPluginLog` and `IAppFolders`
     (the stores, the caches, the fingerprint store), so a replacement reaches Core too, and a test
     host can run the whole engine against a temporary folder.
     - [x] Folders: the host runs on an `AppFolders` (the profile's, or any folder:
       `HorizonHostOptions.Folders`), the settings and the log open in it, and the stores and caches
       (mixes, history, mix entries, metadata, fingerprints) take their paths from `IAppFolders`.
       A store built in memory no longer saves over the app's file.
     - [x] Logging: Core's services (actions and event rules, lyrics, metadata, song sync,
       search, the input backends) write to the Console through `IPluginLog`, taken from the
       container. (The window's own lines move with its workflows, step 9.)
     - [x] Metadata diagnostics: `MetadataTrace` is a service, the host's `IMetadataTrace`, which
       the resolver records its lookups in; it writes to the host's folder and Console log.
     - [x] A test runs the whole engine, headless, in a temporary folder (`HeadlessEngineTests`:
       it plays, stops, and the next host on that folder finds the history, mixes and settings).
8. [x] Platform interfaces in Core; `HorizonRadio.Platform` with the Windows implementations.
   - [x] `HorizonRadio.Platform`: an `IHostPlatform` per OS, picked at run time
     (`HostPlatforms.Current()`) and passed to the host (`HorizonHostOptions.Platform`), which
     registers it over Core's portable defaults and before the front end and the plugins. The
     local speakers are the first service (`ISpeakers`: WASAPI on Windows, none by default), so
     NAudio leaves Core. App folders stay in Core: .NET already finds the per-user data folder on
     each OS.
   - [x] Child processes: Core's `IProcesses` only starts a process; the Windows platform wraps it
     to tie each one to the app's lifetime (`ChildProcesses`, a kill-on-close job object).
   - [x] Secrets: the SDK's `ISecretProtector` (DPAPI on Windows), which the settings store and
     the Spotify login keep their secrets with; the platform hands it to the host before the
     container, since the settings open first. Core has no OS calls left, and no NAudio or DPAPI
     package.
9. [x] Workflows move from view models into Core services; the command list.
   - [x] Adding to the queue: `QueuePlayback.EnqueueAsync(ContentRef, playNow)` finds the source,
     queues it and tells the Console. Search, history, a song's menu, quick play, the Queue page
     and the Sources tab call it; each had its own copy, with its own error handling. A
     self-driven source starts by id with its saved settings (`PlaybackRouter.StartSourceAsync(id)`).
   - [x] Mixes: `MixLibrary` saves, deletes and adds a song to a mix (saving the file, trimming
     the entry lookups, telling the Console), and clears a deleted mix's key bindings, also when
     another PC deleted it (only the Controls page did); `MixSwitcher.PlayAsync(id, mode)` plays
     one in place of the queue's mix or added to it, the mode being the window's question.
   - [x] History: `HistoryIdentifier` (started with the engine) identifies the radio songs in the
     history and stores where to replay them, or marks them to report; the History page only
     looks up cover art, and asks for a retry when it's shown.
   - [x] Tools: `ToolInstalls` (a service) installs, uninstalls and checks for updates
     (`ToolFreshnessChecker`, moved from the UI), rescans the registry and tells the Console; the
     Tools page only shows progress and asks before uninstalling.
   - [x] Synced preferences the window passed on: the router follows the shuffle setting, and
     `MetadataPipeline` rebuilds the resolver when the provider order or a field's provider changes
     (the Metadata page saves through it).
   - [x] What else only the window did: `OutputGuard` (the engine's) pauses once when the chosen
     output can't be heard and says so (`OutputLost`, which the window toasts), and the engine turns
     the local speakers on when there's no game.
   - [x] The command list: `CommandList` names the event-rule and key-binding actions
     (`playback.next`, `mix.switch <id>`), runs them for bindings and rules alike, says why one
     couldn't be done, and names them for the Events and Controls pages. Bindings and rules keep
     saving them as before (`EventAction`), so synced ones still read on an older version.
   Plugins' own commands join when a plugin needs one, as the SDK rule above says.
10. [ ] The control endpoint, with the commands it needs from the services above (adding to the
    queue, playing a mix, shuffle, stop) joining the command list: their arguments take the
    endpoint's message shape, so they're added with it.
11. [ ] `hzr`: remote commands, then `hzr serve`. The CLI project emits the same
    `PluginAssembly_*` metadata the UI's does, which plugin discovery reads to load the plugins.
12. [ ] OS declarations for plugins and per-platform tool downloads; macOS and Linux platform code.

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
- **Core and the SDK stay OS-neutral.** OS-specific code lives in `HorizonRadio.Platform` behind
  an interface Core declares, marked `[SupportedOSPlatform]` so the platform analyzer flags an
  unguarded call.
- **Pages build from `Sdk.UI`.** Colors come from its tokens, text from its styles, repeated
  layouts from its controls, so the app and plugin pages look and behave the same.
- **Commands are plain service methods**, plus a small catalog of named actions for key
  bindings, event rules and the control endpoint. No mediator or command-bus framework.

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
5. [ ] Plugins take host services from the container and register their own; plugin helpers
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
   - [ ] Spotify (librespot, Soloist; `SpotifyRuntime` goes), then the peer helper. Until then
     Spotify's audio processes use `StaticHostServices`.
   - [ ] The audio helpers (the audio cache, subprocess audio, fingerprints) and the title
     extractor, handed out by the host.
   - [ ] The remaining engine coupling (FH6 and Spotify on the queue engine and mixes) goes
     through SDK interfaces, the last Core references go, and so does `IPluginContext`.
6. [ ] `App.axaml.cs` stops naming plugin internals: the catalogs (sources, metadata, tools) are
   services, and Core reaches replaceable services through their interfaces.
7. [ ] `Core/Hosting/`: the startup sequence, plugin discovery, plugin context, game host and
   tool registry move out of `HorizonRadio.UI`.
8. [ ] Platform interfaces in Core; `HorizonRadio.Platform` with the Windows implementations.
9. [ ] Workflows move from view models into Core services; the command list.
10. [ ] The control endpoint.
11. [ ] `hzr`: remote commands, then `hzr serve`.
12. [ ] OS declarations for plugins and per-platform tool downloads; macOS and Linux platform code.

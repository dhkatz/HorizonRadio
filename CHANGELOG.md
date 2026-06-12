# Changelog

All notable changes to Horizon Radio are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Mixes** — cross-source playlists. Build an ordered list of entries from
  different sources (a YouTube video or playlist, a local folder / M3U / file)
  and play them back-to-back as one continuous radio stream, with two-level
  shuffle (the entries, and the items within each collection). Create and manage
  them in the new **Mixes** tab and tune to one from the player bar. Mixes
  replace the old single-source profiles, which migrate automatically to
  one-entry mixes on first launch.
- The in-game station Horizon Radio replaces is now a global control in the
  player bar (moved out of the Sources tab), and a mix can optionally override
  it for the station it plays on.
- **Quick Play**: play a one-off URL, folder, M3U, or file from a content source
  without building a saved mix — pick the source from the player-bar picker (a
  quick-play dialog pops up) or use the Quick Play box on the Sources tab.

### Changed

- The Sources tab now configures a source's engine (tool paths, behavior) only —
  what to play moved to mixes. Content sources (Local, YouTube) play through
  mixes rather than being started directly; self-driven sources (Spotify
  Connect, the test tone) still start from the Sources tab.
- Profiles are replaced by mixes throughout: the Profiles tab is now the Mixes
  tab, and the profile-switch actions/hotkeys became mix-switch.

## [0.3.1] - 2026-06-11

### Changed

- `version.dll` is no longer bundled inside the single-file exe; it ships
  only as the loose copy the Mods tab deploys. The single-file host no
  longer extracts a redundant proxy copy into its temp dir on launch.
  (The loose copy can still be pulled into the process by the DLL search
  order, but the `DllMain` guard keeps it inert there.)

### Fixed

- The companion app no longer shows "Connected" when Forza isn't running.
  The bundled `version.dll` (a proxy DLL) was getting loaded into the app's
  own process by the normal DLL search order, where its `DllMain` started
  the IPC server — so the UI connected to itself. `version.dll` now stays
  inert unless it's loaded by the game, never inside our own UI.

## [0.3.0] - 2026-06-11

### Added

- About tab (entry point in the sidebar footer) showing the app version,
  channel, and commit, with links to the repo / releases / issues /
  license. Builds are now stamped with a `Channel` (stable / nightly /
  dev) the app reads at runtime.
- App update check: on launch the app checks GitHub for a newer build on
  its channel (stable → latest release; nightly → the rolling `nightly`
  prerelease) and surfaces it via a footer badge + a one-time toast.
  Currently "Update" opens the release page; an in-place updater follows.
  Dev builds never check. Offline/indeterminate stays silent.
- librespot is now installable on demand from the Tools tab, downloaded
  from a permanent, version-addressed tool blobstore (the `tools` GitHub
  release) and verified against a SHA-256 the app ships in an embedded
  `tools.manifest.json`. Pins live in one place (the manifest), read by
  both the app build and the librespot CI build.
- Nightly builds: a daily workflow publishes a rolling `nightly`
  prerelease of `main`. External tools resolve from the durable
  blobstore, so an installed nightly keeps working even after newer
  nightlies replace it. See `docs/tool-provisioning.md`.
- The Tools tab now checks installed tools for updates in the background
  on launch and flags stale ones. A sidebar badge and a one-time toast
  surface when an update is available; a "Check for updates" button
  re-runs the check on demand. yt-dlp and ffmpeg compare against their
  upstream's current build; librespot compares against the app's pinned
  build — never against upstream (that drift is the maintainer's job).
  The check is hash-based and failure-silent: offline or otherwise
  indeterminate tools are left unflagged rather than shown as stale.
- Starting a source whose required tools (yt-dlp, ffmpeg, librespot)
  aren't installed now shows a clear "Tool required" prompt naming the
  missing tool(s) and pointing to the Tools tab, instead of a generic
  failure. The check runs before the current source is stopped, so a
  misconfigured switch no longer interrupts what's already playing.

### Changed

- C++ side now builds with CMake (`CMakeLists.txt` + `CMakePresets.json`)
  instead of MSBuild `.vcxproj` files. The `windows-x64` preset uses
  Ninja Multi-Config so a future clang-cl / cross-compiler swap is a
  compiler change, not a generator change. `HorizonRadio.slnx` is
  removed; the C# UI continues to build via `HorizonRadio.UI.slnx`.
- Dropped vcpkg entirely. External C++ deps (currently just doctest)
  are now fetched via CMake's `FetchContent` at configure time, so
  the `vcpkg/` submodule, `vcpkg.json`, and the bootstrap step in CI
  are all gone.
- `version.dll` can now be cross-compiled from Linux or macOS using
  clang + mingw-w64 (see the `linux-cross-x64` / `macos-cross-x64`
  presets and `cmake/toolchain-clang-mingw.cmake`). The 17 system
  `version.dll` export forwarders moved out of `dllmain.cpp`'s
  `#pragma comment(linker)` block and into plain dllexport
  trampolines in `src/version_proxy.cpp`, which both MSVC and
  clang+MinGW accept.
- librespot is no longer compiled in-tree on every C++ build. A
  dedicated GitHub Actions composite action
  (`.github/actions/build-librespot`) builds it once per pinned rev,
  caches the result, and publishes it to the permanent, version-
  addressed `tools` blobstore. It is no longer bundled in the app zip
  or attached per-release — the app fetches it on demand from the
  Tools tab, the same as yt-dlp and ffmpeg.

## [0.2.0] - 2026-06-01

### Added

- Shuffle toggle on the Now Playing page for local-file and YouTube playback.
  The choice is remembered across runs; toggling mid-playback keeps the current
  track and shuffles the rest, and a shuffled order reshuffles when it wraps.
- Live per-station targeting: replace the radio on a chosen station only, or any
  station when left empty.
- Now-playing title/artist injection into the Forza Horizon 6 radio HUD.
- Console tab in the UI, an event system with game-state detection, and grouped
  event categories.
- Contribution guardrails: a `PR title` GitHub workflow that enforces
  Conventional Commit PR titles, plus a contributing workflow section in
  `AGENTS.md`.

### Changed

- Relicensed from MIT to GPL-3.0-or-later.
- Reorganized the sidebar, regrouped events by category, and restyled the mod
  banner.

### Fixed

- librespot autoplay is forced on so Spotify keeps playing past the end of the
  queue.
- Spotify title-timing now lines up with the audio.
- Plugged a metadata string leak when restoring the game's original HUD text.

## [0.1.1] - 2026-05-28

### Changed

- Bundle the .NET assemblies into a single executable for cleaner releases.

## [0.1.0] - 2026-05-28

### Added

- Initial release of Horizon Radio.

[Unreleased]: https://github.com/dhkatz/HorizonRadio/compare/v0.3.1...HEAD
[0.3.1]: https://github.com/dhkatz/HorizonRadio/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/dhkatz/HorizonRadio/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/dhkatz/HorizonRadio/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/dhkatz/HorizonRadio/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/dhkatz/HorizonRadio/releases/tag/v0.1.0

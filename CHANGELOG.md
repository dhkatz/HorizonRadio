# Changelog

All notable changes to Horizon Radio are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
  (`.github/actions/build-librespot`) builds it once per pinned rev
  and caches the result, and every GitHub release now carries a
  standalone `librespot.exe` asset.

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

[Unreleased]: https://github.com/dhkatz/HorizonRadio/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/dhkatz/HorizonRadio/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/dhkatz/HorizonRadio/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/dhkatz/HorizonRadio/releases/tag/v0.1.0

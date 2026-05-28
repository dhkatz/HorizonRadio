# Horizon Radio

Play your own music through Forza Horizon 6's in-game radio.

## Features

- Radio Replacement
  - Streams through the actual in-game radio so station switching, volume, and HUD all keep working
- Sources
  - Local (folders, M3U playlists, single files)
  - Spotify Connect
- Track Metadata Sources
  - MusicBrainz
  - Spotify
- **One-click mod installer**
  - Detects your Forza installation and handles the DLL
- Live dashboard with throughput, gain, and underrun stats

## Setup

In FH6's audio settings:
- **Radio DJ: Off**
- **Streamer Mode: On**

## Installation

1. Download the latest zip from [Releases].
2. Unzip anywhere and run `HorizonRadio.UI.exe`.
3. Open the **Mods** tab → Install.
4. Launch Forza Horizon 6.

[Releases]: https://github.com/dhkatz/horizon-radio/releases

## Building from source

The repo has two solutions — keep them separate (cross-toolchain MSBuild quirks otherwise).

| Solution               | Builds              | Requires                                        |
|------------------------|---------------------|-------------------------------------------------|
| `HorizonRadio.slnx`    | C++ DLL + librespot | VS 2022 Build Tools (MSVC v143), Rust toolchain |
| `HorizonRadio.UI.slnx` | C# UI               | .NET 10 SDK                                     |

```powershell
git clone --recurse-submodules https://github.com/dhkatz/horizon-radio.git
cd horizon-radio
.\vcpkg\bootstrap-vcpkg.bat
msbuild HorizonRadio.slnx /p:Configuration=Release /p:Platform=x64
dotnet build HorizonRadio.UI.slnx
```

The first build compiles `librespot` from source (~5–10 min cold).

To produce a release zip locally:

```powershell
dotnet publish HorizonRadio.UI/HorizonRadio.UI.csproj -c Release -r win-x64 --self-contained
# Output: build/release/HorizonRadio.zip
```

## License

MIT.

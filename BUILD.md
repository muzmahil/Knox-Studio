# Building Knox Studio (dev)

See [`README.md`](README.md) for the short version and the packaging commands. This file
covers the details — per-platform prerequisites and manual step-by-step builds.

## Requirements (macOS)

- macOS 13+, Xcode 15+ (clang, CoreAudio SDK).
- CMake 3.24+ and Ninja — `brew install cmake ninja`.
- .NET SDK 10.
- Avalonia templates, only if you are scaffolding new projects —
  `dotnet new install Avalonia.Templates`.

## Quick start (macOS)

```bash
scripts/build.sh
```

The script builds the native engine (universal arm64 + x86_64), builds the managed side,
and runs the smoke test.

## Running the app

```bash
dotnet run --project src/managed/Knox.App -c Release
```

## Layout

```
src/native/knox.engine/        # C++ engine + C ABI (knox_engine.dll / libknox_engine.dylib)
  include/nota/knox_engine.h   # the public boundary (C ABI)
  src/                         # Engine, audio/MIDI backends, SPSC queue, DSP
  pluginhost/                  # JUCE-based VST3/AU hosting (the only JUCE-aware module)
src/managed/Knox.Domain/       # pure domain types
src/managed/Knox.Application/  # ports (interfaces) and use cases
src/managed/Knox.Infrastructure/  # P/Invoke interop, .knox persistence, services
src/managed/Knox.Presentation/ # view-models
src/managed/Knox.App/          # Avalonia application (Views, Themes, Locales)
src/managed/Nota.Mcp/          # MCP server
tests/Knox.SmokeTest/          # end-to-end check: C# -> C ABI -> engine -> audio device
```

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for how these fit together and the rules that
govern the boundary between them.

## Building the parts by hand

```bash
# native
cmake -G Ninja -S src/native/knox.engine -B src/native/knox.engine/build \
  -DCMAKE_BUILD_TYPE=Release
cmake --build src/native/knox.engine/build

# managed
dotnet build Knox.sln -c Release
```

The managed projects copy the right native artifact for the platform out of the CMake
build directory and place it next to the binary: `libknox_engine.dylib` +
`knox-scanworker` (macOS), `knox_engine.dll` + `knox-scanworker.exe` (Windows), or
`libknox_engine.so` + `knox-scanworker` (Linux). Override the paths with
`-p:KnoxEngineLib=...` / `-p:KnoxScanWorker=...`.

On memory-constrained machines and containers, cap parallelism so the large JUCE
translation units are not OOM-killed: `export CMAKE_BUILD_PARALLEL_LEVEL=2`.

## Building on Windows (x64)

### Requirements

- Windows 10/11 x64.
- Visual Studio 2022 (Desktop C++ workload: MSVC v143 + Windows SDK); for ARM64 also the
  **"MSVC v143 - ARM64 build tools"** component.
- CMake 3.24+ and Ninja (both ship with the VS 2022 "C++ CMake tools", or
  `winget install Ninja-build.Ninja`).
- .NET SDK 10.

### One-Click Build & Packaging (`build.bat`)

Knox Studio provides a unified root build script that handles C++ compilation, managed solution build, smoke test verification, single-file publishing (`Knox Studio.exe`), and modern installer generation:

```cmd
build.bat --nopause
```

Artifacts produced in `dist/`:
- `dist/publish-x64/Knox Studio.exe` (Self-contained, single-file DAW)
- `dist/setup.exe` (Modern custom installer)
- `dist/publish-x64/uninstall.exe` (Modern uninstaller)

### PowerShell Alternative

Run from a **"Developer PowerShell for VS 2022"** so `cl` and `ninja` are on PATH:

```powershell
pwsh scripts/build-win.ps1            # native (x64/WASAPI) + managed + smoke
pwsh scripts/package-win.ps1 x64      # installer packaging
```

### Platform details

- Audio is **WASAPI** through vendored miniaudio (`vendor/miniaudio`); supports shared and low-latency **exclusive mode**.
- MIDI input is **WinMM** through vendored RtMidi (`vendor/rtmidi`).
- Plugin hosting is **VST3** (AU is macOS-only).
- Settings, logs and projects are written to `%APPDATA%\Knox Studio` (the counterpart of
  `~/Library/Application Support/Knox Studio`).
- The engine builds as `knox_engine.dll`; the C ABI is exported via
  `__declspec(dllexport)`.

## Building on Linux

### Requirements

- `build-essential`, CMake 3.24+, Ninja, .NET SDK 10.
- Native dev packages:

```bash
sudo apt install build-essential cmake ninja-build libasound2-dev libx11-dev \
  libxext-dev libxrandr-dev libxinerama-dev libxcursor-dev libfreetype6-dev \
  libfontconfig1-dev
```

### Quick start

```bash
scripts/build-linux.sh    # native (PulseAudio/ALSA + RtMidi/ALSA) + managed + smoke
scripts/package-linux.sh  # portable AppImage in dist/
```

### Platform details

- Audio is **PulseAudio/ALSA** through miniaudio, which `dlopen`s them at runtime — so
  `libpulse` and `libasound` are not needed at link time, only `libasound2-dev` for
  RtMidi.
- JUCE (plugin hosting) is what pulls in the X11, freetype and fontconfig libraries.
- The AppImage is built for the host architecture. To produce a Linux build from
  macOS or Windows, run the build inside a Linux container — see the Docker commands in
  [`README.md`](README.md).


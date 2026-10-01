# 🎹 Knox Studio

<div align="center">


### **A Modern, High-Performance, Cross-Platform Digital Audio Workstation (DAW)**
*Next-generation music production software built with a real-time C++20 DSP engine and GPU-accelerated Avalonia .NET 10 UI.*

[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-blue?style=for-the-badge&logo=linux&logoColor=white)](#-prerequisites)
[![UI Framework](https://img.shields.io/badge/UI-Avalonia%2012%20(.NET%2010)-purple?style=for-the-badge&logo=dotnet)](#-architecture-overview)
[![Engine](https://img.shields.io/badge/Engine-C%2B%2B20%20%2F%20Lock--Free%20DSP-00599C?style=for-the-badge&logo=cplusplus)](#-real-time-audio-engine--dsp)
[![Plugin Host](https://img.shields.io/badge/Plugins-VST3%20%7C%20AU%20Host-orange?style=for-the-badge)](#-robust-vst3--au-plugin-hosting)
[![License](https://img.shields.io/badge/License-AGPL--3.0-red?style=for-the-badge)](LICENSES/AGPL-3.0-only.txt)

[**Features**](#-features) • [**Quick Start**](#-quick-start) • [**Architecture**](#-architecture-overview) • [**Plugins**](#-robust-vst3--au-plugin-hosting) • [**Documentation**](#-documentation) • [**Contributing**](#-contributing)

</div>

---

## 🌟 Overview

**Knox Studio** is a professional-grade, open-source digital audio workstation (DAW) designed for speed, flexibility, and pristine audio quality. Engineered from the ground up for modern operating systems, Knox Studio unifies non-linear live clip launching, precision multi-track linear arrangement, and modular CV/DSP node routing into an intuitive, responsive interface.

At its core, a **lock-free, zero-allocation C++20 audio engine** delivers sub-millisecond latency, robust crash protection for third-party plugins, high-quality audio time-stretching, and a suite of 35+ built-in synthesis and mixing processors.

---

## ✨ Features

### 🎛️ Three Integrated Workflows
* **Arrangement Timeline**: Multi-track linear sequencing with fluid zooming, non-destructive slip/trim editing, curve automation, track grouping/submixing, and **Adaptive Snapping**.
* **Session Grid**: Non-linear clip launcher optimized for live performance, improvisation, and fast beat sketching.
* **Modular CV & DSP Graph**: Cable-patchable visual modulation system featuring LFOs, Envelope Followers, Step Sequencers, Math nodes, and Macro controllers assignable to any parameter.

### 🔊 Studio-Grade Audio & Warping Engine
* **Non-Destructive Audio Editing**: Instant **Reverse Clip**, multi-point **Normalize** (0 dB, -0.1 dB, -1.0 dB, -6.0 dB), **Invert Phase (Polarity)**, and automatic de-clicking fades.
* **Signalsmith Time-Stretch & Warping**: Complex & Complex Pro warp modes with high-accuracy transient detection and beat grid alignment.
* **Master Soft-Limiter & True-Peak Protection**: Transparent $C^1$-continuous master saturation avoiding digital harshness and aliasing under heavy summing.
* **Zero-Latency Multi-Track Recording**: Record audio and sample-accurate MIDI simultaneously, with punch-in/out loop recording and track resampling.
* **Comprehensive Metering**: High-precision Peak, RMS, LUFS loudness, and True-Peak meters across every channel strip.

### 🎹 Sample-Accurate MIDI & Piano Roll
* **Modern Piano Roll Editor**: Velocity editing, multi-note stretch/squeeze, scale highlighting, fold mode, drum grid mode, and smooth 2D right-click navigation.
* **Audio-to-MIDI Neural & DSP Analysis**: Extract melodic pitch tracks, chord progressions, transient slices, or drum kits directly from audio files.
* **Hardware Integration**: Full support for USB/Bluetooth MIDI controllers, MPE (MIDI Polyphonic Expression), and computer keyboard typing-piano input.

### 🔌 Robust VST3 & AU Plugin Hosting
* **Native GUI Embedding & Bridging**: Host your favorite VST3 and Audio Unit (macOS) instruments and effects with full parameter automation and MIDI Learn.
* **Crash-Isolated Scan Worker**: Out-of-process plugin indexing (`knox-scanworker`) guarantees that buggy plugins never freeze or crash the host application.
* **Configurable Buffer Sizes**: Support for standard and high-performance buffer lengths (32 to 8192 frames) with low-overhead direct rendering.

### 🧩 35+ Built-in Instruments & Audio Effects
* **Synthesizers**: *Aurora* (Wavetable), *Flux* (Vector/Spectral), *Operator* (4-Operator FM), *Monolith* (Subtractive Analog), *GrainSynth* (Granular Texture), *Collision* (Physical Modeling), and *Knox Rhythm* (Hybrid Drum Synthesizer & Sampler).
* **Audio Processors**: Dynamic EQ, 3-Band Parametric EQ, Tube Saturator, Vintage Optical Compressor, Multimode AutoFilter, Reverb, Tape Delay, Beat Repeat, BitCrush, and Utility.
* **MIDI FX & Racks**: Arpeggiator, Chord Engine, Scale Quantizer, Velocity Shaper, and multi-chain Instrument/Drum/Effect Racks with 8 assignable Macro knobs.

### 🎨 Deep UI Customization & Visual Themes
* **Arrangement Grid Theming**: Choose from pre-tuned dark themes (*Default Studio, Deep Obsidian, Warm Charcoal, Midnight Navy, Cool Slate, Cyber Dark*) or specify any custom HEX color for timeline lanes and division markers.
* **Custom Background Wallpaper**: Place your own artwork, studio logo, or mood images directly onto the arrangement canvas with real-time **opacity control (0%–100%)** and multiple layout modes (**Stretch**, **Fit**, **Fill/Cover**, **Center**, **Grid/Tile**).
* **Audio Track & Clip Geometry**: Customize clip corner styling (**Rounded 4px**, **Sharp Rectangle 0px**, **Smooth Pill Capsule**) and interior fills (**Default Contrast**, **Frosted Glass Translucent**, **Vibrant Flat**).
* **High-Visibility Black Waveforms & Text**: Optional dark typography and crisp black waveform rendering for ultra-clean contrast on bright clip colors.

### 🌐 Multilingual & Rich Presence
* **Seamless Localization**: Instant runtime switching between **English** and **Turkish (Türkçe)** with persistent user settings.
* **Discord Rich Presence**: Live broadcast of active project details, track count, tempo (BPM), and editing status.

---

## 🏗️ Architecture Overview

Knox Studio follows a **Clean Architecture** model, strictly separating high-level presentation and application logic from real-time audio computation via a pure **C ABI**:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                      MANAGED LAYER (.NET 10 / C#)                       │
│  ┌──────────────┐  ┌───────────────────┐  ┌──────────────────────────┐  │
│  │   Knox.App   │─▶│ Knox.Presentation │─▶│     Knox.Application     │  │
│  │ (Avalonia UI)│  │   (View-Models)   │  │   (Use Cases & Ports)    │  │
│  └──────────────┘  └───────────────────┘  └─────────────┬────────────┘  │
│                                                         │               │
│                                           ┌─────────────▼────────────┐  │
│                                           │       Knox.Domain        │  │
│                                           │    (Pure Domain Types)   │  │
│                                           └──────────────────────────┘  │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │ Knox.Infrastructure: Interop (P/Invoke) & Project Serialization   │  │
│  └───────────────────────────────────┬───────────────────────────────┘  │
└──────────────────────────────────────┼──────────────────────────────────┘
                                       │ Pure C ABI (knox_engine.h)
┌──────────────────────────────────────▼──────────────────────────────────┐
│                      NATIVE ENGINE (C++20 / JUCE)                       │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │ knox.engine: Real-time Audio Graph, DSP, Transport, Lock-Free SPSC│  │
│  │ Audio Backends: WASAPI (Windows), CoreAudio (macOS), ALSA/Pulse   │  │
│  └───────────────────────────────────┬───────────────────────────────┘  │
│                                      │ Isolated Static Library          │
│  ┌───────────────────────────────────▼───────────────────────────────┐  │
│  │ pluginhost: JUCE VST3 & AU Host + knox-scanworker (Out-of-Process)│  │
│  └───────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────┘
```

> For full architectural details, real-time safety constraints (AR-1 to AR-9), and thread boundary rules, see [**ARCHITECTURE.md**](ARCHITECTURE.md).

---

## 🚀 Quick Start

### Prerequisites

| Platform | Compilers & SDKs | Audio & System Dependencies |
| :--- | :--- | :--- |
| **Windows** | Visual Studio 2022 / Build Tools (MSVC C++20), .NET 10 SDK, CMake ≥ 3.24 | Windows 10/11 SDK (WASAPI / WinMM built-in) |
| **macOS** | Xcode 15+ Command Line Tools, .NET 10 SDK, CMake ≥ 3.24 | CoreAudio & CoreMIDI (built-in) |
| **Linux** | GCC 13+ / Clang 16+, .NET 10 SDK, CMake ≥ 3.24 | `libasound2-dev libx11-dev libxext-dev libxrandr-dev libxinerama-dev libxcursor-dev libfreetype6-dev libfontconfig1-dev` |

---

### Building and Running

#### 🪟 Windows
```cmd
:: Automated one-click native build, smoke tests, and executable packaging:
build.bat --nopause

:: Or launch directly from .NET CLI:
dotnet run --project src/managed/Knox.App -c Release
```

#### 🍎 macOS
```bash
# Build Universal (Apple Silicon / Intel) binaries and run:
chmod +x scripts/*.sh
scripts/build.sh
dotnet run --project src/managed/Knox.App -c Release
```

#### 🐧 Linux (Ubuntu / Debian / Fedora / Arch)
```bash
# Install dependencies (Ubuntu/Debian example)
sudo apt update && sudo apt install -y build-essential cmake ninja-build libasound2-dev \
  libx11-dev libxext-dev libxrandr-dev libxinerama-dev libxcursor-dev libfreetype6-dev libfontconfig1-dev

# Build and run
scripts/build-linux.sh
dotnet run --project src/managed/Knox.App -c Release
```

---

## 🧪 Automated Testing

Verify the native C ABI engine and DSP subsystem via the built-in smoke test suite:
```bash
dotnet run --project tests/Knox.SmokeTest -c Release
```

---

## 📦 Distributables & Installers

Pre-configured scripts output distribution packages into the `dist/` directory:
* **Windows**: `dist/publish-x64/Knox Studio.exe` (Standalone executable) and `dist/setup.exe` (Modern Installer).
* **macOS**: `dist/Knox-<version>-universal.dmg` (Universal Application Bundle).
* **Linux**: `dist/Knox-<version>-<arch>.AppImage` (Self-contained AppImage).

---

## 📁 Repository Structure

```
├── assets/                    # Application icons, screenshots, and visual assets
├── docs/                      # Technical manuals and design specifications
├── LICENSES/                  # AGPL-3.0 License & Third-party dependency registry
├── scripts/                   # Automated build, test, and packaging scripts
├── build.bat                  # One-click Windows build and installer pipeline
├── src/
│   ├── managed/               # .NET 10 / C# Application
│   │   ├── Knox.Domain/       # Pure domain models (Time, Note, Track, Project)
│   │   ├── Knox.Application/  # Audio Engine ports, Use Cases, Interfaces
│   │   ├── Knox.Infrastructure# P/Invoke bindings, File IO, Discord RPC
│   │   ├── Knox.Presentation/ # Reactive ViewModels (MVVM)
│   │   └── Knox.App/          # Avalonia Views, Custom Controls, Themes
│   └── native/
│       └── knox.engine/       # C++20 Core Audio Engine & DSP
│           ├── include/nota/  # Public C ABI Headers (knox_engine.h)
│           ├── src/           # Synths, FX, Mixer, Transport, SPSC Queues
│           └── pluginhost/    # Isolated JUCE VST3/AU Host & scanworker
└── tests/
    └── Knox.SmokeTest/        # Automated End-to-End Test Suite
```

---

## 🤝 Contributing

Contributions from audio developers, DSP engineers, designers, and musicians are welcome!
Please review [**CONTRIBUTING.md**](CONTRIBUTING.md) before submitting pull requests.

---

## 📄 Documentation

* 📐 [**Architecture Guide**](ARCHITECTURE.md)
* 🛠️ [**Build Guide & Packaging**](BUILD.md)
* 🎛️ [**Full Feature Catalog**](FEATURES.md)
* 📜 [**Changelog**](CHANGELOG.md)
* 🎨 [**Design Guidelines**](DESIGN.md)

---

## 📜 License

Knox Studio is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0-only)**.
See [LICENSES/AGPL-3.0-only.txt](LICENSES/AGPL-3.0-only.txt) and [LICENSES/README.md](LICENSES/README.md) for full license details and third-party notices.

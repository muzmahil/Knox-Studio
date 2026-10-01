<!-- SPDX-License-Identifier: AGPL-3.0-only -->
<!-- Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms. -->

# 🏛️ Knox Studio Architecture & Developer Guide

This document is the authoritative technical manual for Knox Studio DAW. It explains how the native DSP audio engine, the C ABI interop layer, and the managed Avalonia (.NET) application work together.

---

## 🧭 Table of Contents

1. [Architectural Overview & Principles](#1-architectural-overview--principles)
2. [Repository & Codebase Map](#2-repository--codebase-map)
3. [Architecture Rules (AR-1 to AR-9)](#3-architecture-rules-ar-1-to-ar-9)
4. [The C ABI & Interop Layer](#4-the-c-abi--interop-layer)
5. [Audio Engine & DSP Pipeline](#5-audio-engine--dsp-pipeline)
6. [Threading Model & Lock-Free Messaging](#6-threading-model--lock-free-messaging)
7. [Modulation Matrix & CV Graph](#7-modulation-matrix--cv-graph)
8. [Audio Warping, Time-Stretching & Clip DSP](#8-audio-warping-time-stretching--clip-dsp)
9. [Plugin Hosting & Out-of-Process Isolation](#9-plugin-hosting--out-of-process-isolation)
10. [Project Persistence (.knox Bundle)](#10-project-persistence-knox-bundle)
11. [Localization & Multi-Language Architecture](#11-localization--multi-language-architecture)
12. [Step-by-Step: Adding a New Device](#12-step-by-step-adding-a-new-device)

---

## 1. Architectural Overview & Principles

Knox Studio is split into two halves meeting at a **pure C ABI**:

```
+--------------------------------------------------------------------------+
|                      MANAGED LAYER (.NET 10 / C#)                        |
|                                                                          |
|  Knox.App             Avalonia 12 UI, Custom Controls, Themes, Locales   |
|  Knox.Presentation    Reactive ViewModels (MVVM), State Synchronization  |
|  Knox.Application     Ports (Engine Interfaces), App Use Cases, DTOs     |
|  Knox.Domain          Pure Domain Models (Time, Pitch, Clip, Track)      |
|  Knox.Infrastructure  P/Invoke Interop, .knox Persistence, Discord RPC   |
+------------------------------------+-------------------------------------+
                                     |  Pure C ABI (nota/knox_engine.h)
+------------------------------------+-------------------------------------+
|                      NATIVE ENGINE (C++20)                               |
|                                                                          |
|  knox.engine (Core)   Graph, Transport, Tracks, Devices, DSP, SPSC Queue |
|  Audio Backends       WASAPI (Windows), CoreAudio (macOS), ALSA/Pulse    |
|  MIDI Backends        WinMM / ALSA (RtMidi), CoreMIDI                    |
|  pluginhost           JUCE VST3/AU Host + knox-scanworker (Isolated)     |
+--------------------------------------------------------------------------+
```

### Core Design Principles:
1. **Clean Architecture / Inversion of Control**: Managed dependencies flow strictly inward: `App -> Presentation -> Application -> Domain`. Infrastructure implements Application ports.
2. **Strict C ABI Boundary**: No C++ types, standard library containers, or exceptions cross the managed/native boundary.
3. **Real-Time Safety**: The audio render thread never blocks, allocates memory, or performs file/socket I/O.
4. **JUCE Isolation**: JUCE is isolated exclusively inside the `pluginhost/` static library. The engine core is completely JUCE-free.

---

## 2. Repository & Codebase Map

### 📦 Managed Projects (`src/managed/`)

| Project | Namespace | Purpose & Key Classes |
| :--- | :--- | :--- |
| **`Knox.Domain`** | `Knox.Domain.*` | Pure mathematical & musical types with zero dependencies (`Beats`, `SampleTime`, `MidiNote`, `TrackKind`). |
| **`Knox.Application`** | `Knox.Application.*` | Engine abstraction ports (`IAudioEngine`), DTOs (`TrackInfo`, `ClipInfo`, `DeviceParam`), Project models. |
| **`Knox.Infrastructure`** | `Knox.Infrastructure.*` | `Interop/KnoxEngine.cs` (P/Invoke bindings across 8 slices), `ProjectService.cs` (`.knox` serializer), `DiscordRpcService.cs`, `KnoxPaths.cs`. |
| **`Knox.Presentation`** | `Knox.Presentation.*` | View-models: `MainViewModel`, `TrackViewModel`, `ClipViewModel`, `DeviceChainViewModel`, `SessionViewModel`, `ModularViewModel`. |
| **`Knox.App`** | `Knox.App.*` | Avalonia Views: `ArrangementView.cs`, `SessionView.cs`, `ClipEditorView.cs`, `AudioClipEditorView.cs`, `BrowserView.cs`, custom controls (`Knob.cs`, `Fader.cs`, `MeterBar.cs`, `AuroraViz.cs`), and `LocaleManager.cs`. |

### ⚙️ Native Engine (`src/native/knox.engine/`)

| Directory / File | Role & Key Responsibilities |
| :--- | :--- |
| **`include/nota/knox_engine.h`** | The public C ABI interface header defining every exported function and C-compatible struct. |
| **`src/Engine.h` & `Engine_*.cpp`** | Central engine facade: `Engine_Tracks.cpp` (clips/tracks), `Engine_Render.cpp` (audio loop), `Engine_Devices.cpp`, `Engine_Automation.cpp`, `Engine_Modulation.cpp`, `Engine_Session.cpp`. |
| **`src/Graph.h`** | Immutable audio graph snapshot holding all active tracks, device chains, and routing matrices. |
| **`src/Transport.h`** | Single authoritative source for BPM, sample rate, playhead ticks, and loop points. |
| **`src/CommandQueue.h`** | Lock-free Single-Producer Single-Consumer (SPSC) ring buffer transferring commands between message and audio threads. |
| **`src/Track.h` & `Track.cpp`** | Audio and Instrument track structures, clip containers, and peak extraction. |
| **`src/Instrument.h` & `*Synth.h`** | Built-in synths: `Aurora` (wavetable), `Flux` (vector), `Operator` (FM), `Monolith`, `GrainSynth`, `BassSynth`, `Collision`. |
| **`src/Device.h` & `*.h`** | Built-in audio effects: `Eq.h`, `DynamicEq.h`, `Compressor.h`, `Amp.h`, `AutoFilter.h`, `Delay.h`, `Reverb.h`, `Ceiling.h`. |
| **`src/Warp.h` & `WarpStream.cpp`** | Signalsmith Stretch offline audio cache and transient warp marker engine. |
| **`pluginhost/`** | JUCE-based VST3 and AudioUnit plugin hosting bridge (`PluginHostBridge.h`). |
| **`pluginhost/scanworker/`** | Separate executable (`knox-scanworker`) that probes plugins out-of-process to prevent host crashes. |

---

## 3. Architecture Rules (AR-1 to AR-9)

| Rule | Definition |
| :--- | :--- |
| **AR-4** | **Lock-Free SPSC Queue**: All commands sent from the UI/message thread to the audio thread are passed through a pre-allocated lock-free ring buffer (`CommandQueue.h`). |
| **AR-5** | **Immutable Graph Snapshot**: When tracks, routing, or devices are modified, a new immutable graph is published on the message thread. The audio thread swaps the pointer atomically at block boundaries. |
| **AR-6** | **Zero Real-Time Hazards**: The audio render thread must NEVER call `malloc`/`free`, take mutex locks, perform file/socket I/O, or print debug logs. |
| **AR-7** | **Pure C ABI Boundary**: `extern "C"` functions, primitive types, UTF-8 strings, and opaque pointers only. |
| **AR-8** | **Sample-Accurate Timing**: MIDI events, automation points, and modulation updates are resolved with sample-accurate offsets per audio buffer. |
| **AR-9** | **Transport as Single Source of Time**: `Transport` is the sole source of musical time. No sub-system independently calculates tempo or beats. |

---

## 4. The C ABI & Interop Layer

The C ABI contract is defined in `include/nota/knox_engine.h`. The managed wrapper in `Knox.Infrastructure/Interop/KnoxEngine.cs` splits P/Invoke definitions into logical partial classes:

```
KnoxEngine.cs (Lifecycle, Master, Transport)
├── KnoxEngine.Tracks.cs       (Track creation, reordering, volume, pan, mute, solo)
├── KnoxEngine.Clips.cs        (MIDI & Audio clip adding, trimming, warp markers, reverse/normalize)
├── KnoxEngine.Devices.cs      (Built-in synths, effects, parameter getting/setting)
├── KnoxEngine.Plugins.cs      (VST3/AU hosting, state blobs, editor windows)
├── KnoxEngine.Session.cs      (Session grid slots, clip launching, scene triggers)
├── KnoxEngine.Automation.cs   (Lanes, bezier curve points, parameter binding)
└── KnoxEngine.Modulation.cs   (CV nodes, LFOs, macro knobs, virtual cables)
```

### ABI Call Example:
```c
// Native C header (knox_engine.h)
NOTA_API int32_t nota_track_set_volume(EngineHandle handle, int32_t track_id, float volume_db);

// Managed C# P/Invoke (KnoxEngine.Tracks.cs)
[DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
private static extern int nota_track_set_volume(IntPtr handle, int trackId, float volumeDb);
```

---

## 5. Audio Engine & DSP Pipeline

Every audio block follows a strictly ordered, real-time render cycle in `Engine_Render.cpp`:

```
┌────────────────────────────────────────────────────────────────────────┐
│                         AUDIO RENDER CALLBACK                          │
├────────────────────────────────────────────────────────────────────────┤
│ 1. Drain SPSC Command Queue (Apply live parameter tweaks & notes)      │
│ 2. Advance Transport (Compute current beat, tempo, sample offsets)     │
│ 3. Sample-Accurate MIDI Scheduling (Resolve active clips & live inputs)│
│ 4. Render Tracks (In topological graph order):                         │
│    ├── A. Clip Audio Playback / Instrument Synth Voice Generation      │
│    ├── B. Process Track Insert Device Chain (EQ -> Comp -> Saturation) │
│    ├── C. Apply Track Volume & Pan                                     │
│    └── D. Route Audio to Parent Groups and Send / Return Buses         │
│ 5. Process Return Tracks & Group Submixes                              │
│ 6. Process Master Bus Device Chain                                     │
│ 7. Apply True-Peak Limiter / Master Volume                             │
│ 8. Push Output Buffers to Hardware Driver (WASAPI / CoreAudio / ALSA)  │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 6. Threading Model & Lock-Free Messaging

Knox Studio utilizes a 3-tier thread architecture:

```
[ UI / Message Thread ] (Avalonia)
        │
        │ 1. Mutate Graph / Send Command
        ▼
   [ SPSC Ring Buffer ] (CommandQueue.h) ─── Atomic Pointer Swap ───┐
        │                                                           │
        │ 2. Real-Time Processing (Zero Alloc / Zero Lock)          ▼
        ▼                                                   [ Current Graph ]
[ Audio Render Thread ] (WASAPI / CoreAudio)                 (Immutable)
        │
        │ 3. Output Peaks & Metering
        ▼
   [ SPSC Meter Buffer ]
        │
        ▼
[ UI Polling Clock (30-60 Hz) ] (Updates Faders, Waveforms, Knobs)
```

---

## 7. Modulation Matrix & CV Graph

The Modular view allows arbitrary parameter modulation:
- **CV Sources**: LFO (Sine, Triangle, Saw, Square, Random, S&H), Envelope Follower, ADSR, Step Sequencer, Math Nodes, Macro Controls.
- **Modulation Cables**: Connect any CV output to any device parameter with adjustable bipolar depth (`-1.0` to `+1.0`).
- **Processing**: Evaluated at block rate (or sub-block rate) with parameter smoothing in `Engine_Modulation.cpp`.

---

## 8. Audio Warping, Time-Stretching & Clip DSP

Knox Studio implements high-quality time-stretching and audio clip manipulation:
- **Signalsmith Stretch Engine**: Complex and Complex Pro modes maintain transient fidelity and formant preservation across sample-rate and tempo shifts.
- **Offline Render Cache**: Warped clips render once to an internal cache; the real-time audio thread streams directly from RAM.
- **Audio Clip DSP Suite**:
  - **Reverse**: Native reverse audio buffer generator with immediate waveform sync.
  - **Normalization**: Peak detection and gain compensation (-0.1 dB, -1.0 dB, -6.0 dB, 0 dB).
  - **Phase Inversion & De-Click**: 5 ms micro-fades and polarity flipping for pristine audio transitions.

---

## 9. Plugin Hosting & Out-of-Process Isolation

- **JUCE Bridge**: Built as `nota_pluginhost.lib` and accessed via `PluginHostBridge.h`.
- **Out-of-Process Scanner**: `knox-scanworker` scans system VST3/AU directories independently. If an unstable plugin crashes or hangs, the scan worker terminates safely without affecting the main DAW application.
- **Native GUI Windows**: Plugin editor windows are embedded seamlessly into the OS windowing hierarchy (Win32 child HWND on Windows, NSView on macOS, X11 Window on Linux).

---

## 10. Project Persistence (.knox Bundle)

Knox projects are stored as clean bundle directories (`.knox` or `.knoxproj`):

```
MySong.knox/
├── project.json         # Human-readable JSON containing all tracks, clips, automation
├── project.json.bak     # Atomic backup copy
├── samples/             # Audio files referenced by the project
└── plugin-states/       # Opaque binary state blobs for hosted VST3/AU plugins
```

Serialization is owned entirely by `Knox.Infrastructure/Persistence/ProjectService.cs`.
Autosave and crash recovery state are persisted to `%APPDATA%\Knox Studio\project.knox`.

---

## 11. Localization & Multi-Language Architecture

Knox Studio includes a reactive localization system (`LocaleManager`):
- JSON locale dictionaries located at `Locales/en.json` and `Locales/tr.json`.
- UI strings bind reactively via `L10n.Get("key")` or markup extensions.
- Active language preference is stored in `%APPDATA%\Knox Studio\knox.settings`.

---

## 12. Step-by-Step: Adding a New Device

Want to create a new DSP effect (e.g. `StereoFlanger`)? Follow these 4 steps:

### Step 1: Write Native DSP (`src/native/knox.engine/src/Flanger.h`)
```cpp
#pragma once
#include "Device.h"

namespace nota {
class Flanger : public Device {
public:
    Flanger() {
        setParamCount(4);
        setParam(0, 0.5f); // Rate
        setParam(1, 0.7f); // Depth
        setParam(2, 0.3f); // Feedback
        setParam(3, 0.5f); // Mix
    }
    void process(float* left, float* right, int numSamples) override {
        // Real-time DSP algorithm here (zero malloc / zero locks!)
    }
};
}
```

### Step 2: Register in Device Catalog (`Engine_Devices.cpp` & `knox_engine.h`)
Add `DeviceKind::Flanger = 25` and instantiate it in `Engine::addTrackDevice`.

### Step 3: Add Managed Interop (`Knox.Presentation`)
Add ViewModel mapping in `DeviceChainViewModel.cs` to expose observable properties.

### Step 4: Create Avalonia Card Control (`Knox.App/Controls/FlangerCard.cs`)
Build the UI using Knox's `Knob`, `Fader`, and Ember Graphite styling!

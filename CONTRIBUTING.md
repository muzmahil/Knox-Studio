<!-- SPDX-License-Identifier: AGPL-3.0-only -->
<!-- Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms. -->

# Contributing to Knox

Thank you for your interest in contributing to Knox! Whether you are building high-performance DSP algorithms, creating custom Avalonia UI controls, fixing bugs, or improving documentation, this guide will help you get started quickly and effectively.

---

## 🧭 Table of Contents

1. [Code of Conduct & Spirit](#-code-of-conduct--spirit)
2. [Licensing Your Contribution](#-licensing-your-contribution)
3. [Architecture & Non-Negotiable Rules](#-architecture--non-negotiable-rules)
4. [Development Workflow & Setup](#-development-workflow--setup)
5. [Code Style & Conventions](#-code-style--conventions)
6. [Testing & Smoke Tests](#-testing--smoke-tests)
7. [Submitting a Pull Request](#-submitting-a-pull-request)
8. [Adding New DSP Devices & Synths](#-adding-new-dsp-devices--synths)

---

## 🤝 Code of Conduct & Spirit

Knox is designed to be an open, inclusive, and high-performance digital audio workstation. We value constructive feedback, clean code, respectful discussion, and high attention to detail.

---

## 📜 Licensing Your Contribution

Knox is licensed under **AGPL-3.0-only** (see [LICENSES/](LICENSES/)).

> **Important:** By submitting a pull request, you explicitly agree that your contributions are licensed under **AGPL-3.0-only**.

- **Third-Party Dependencies:** Any new external dependency must be compatible with AGPL-3.0. Do not introduce copyleft or restrictive dependencies without prior discussion. Every approved dependency must be registered in `LICENSES/third-party.md`.
- **Source Headers:** Every new C++, C#, or script file must begin with the standard license header:
  ```c
  // SPDX-License-Identifier: AGPL-3.0-only
  // Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.
  ```

---

## 🛡️ Architecture & Non-Negotiable Rules

Knox operates in a **hard real-time audio environment**. Actions that might seem harmless in standard desktop applications (e.g., allocating memory, taking a lock, calling file I/O) will cause audio dropouts (XRuns) and glitching on the audio thread.

Before modifying engine internals, read [**ARCHITECTURE.md**](ARCHITECTURE.md). You **must** adhere to these core rules:

| Rule | Description |
| :--- | :--- |
| **AR-4: Lock-Free SPSC** | Message-to-audio thread communication must go through a **lock-free single-producer single-consumer ring buffer** (`CommandQueue.h`). Never block the audio thread. |
| **AR-5: Immutable Graph** | The audio graph is an **immutable snapshot** (`Graph.h`). Structural edits (adding tracks, reordering devices) take place on the message thread and publish a fresh snapshot to the audio thread. |
| **AR-6: Zero Real-Time Hazards** | **The audio callback thread must NEVER allocate (`malloc`/`new`), free (`delete`), acquire mutexes/locks, perform file/socket I/O, or print logs.** |
| **AR-7: Pure C ABI Boundary** | The boundary between C++ and .NET is strictly a **C ABI** (`extern "C"` functions, primitive types, UTF-8 strings, pointers to opaque structs). No C++ types or exceptions cross the line. |
| **AR-8: Sample-Accurate MIDI** | All MIDI events and parameter automations are timestamped with sample offsets within the audio block. |
| **AR-9: Single Time Source** | `Transport` is the authoritative single source of musical time (BPM, playhead position, loop points, sample rate). |
| **JUCE Isolation** | JUCE is strictly confined to `src/native/knox.engine/pluginhost`. The core engine and DSP classes must remain 100% JUCE-free. |

---

## 💻 Development Workflow & Setup

### 1. Fork & Clone
```bash
git clone https://github.com/<your-username>/knox.git
cd knox
```

### 2. Build the Project
- **Windows (All-in-One)**:
  ```cmd
  build.bat --nopause
  ```
  Or PowerShell:
  ```powershell
  pwsh scripts/build-win.ps1
  ```
- **macOS**:
  ```bash
  scripts/build.sh
  ```
- **Linux**:
  ```bash
  scripts/build-linux.sh
  ```

### 3. Run the DAW
```bash
dotnet run --project src/managed/Knox.App -c Release
```

---

## 🎨 Code Style & Conventions

### C++ (DSP & Native Engine)
- Standard: **C++20**.
- Memory Management: Zero dynamic memory allocation on the audio render thread. Pre-allocate voice tables, delay lines, and wavetables in constructors or during `prepareToPlay()`.
- Headers: Use `#pragma once`. Place declarations in `.h` and implementations in `.cpp`.
- Naming: `camelCase` for methods/variables, `PascalCase` for types/classes, `kConstantName` for constants.

### C# (.NET & Avalonia)
- Framework: **.NET 10** with modern C# features (records, pattern matching, file-scoped namespaces).
- Layering: Maintain clean architecture boundaries (`Domain -> Application -> Infrastructure / Presentation -> App`).
- UI State: Do not poll the engine on high-frequency render loops; use timer-driven view-model synchronization.
- Views & Controls: Follow the **Ember Graphite** design language (warm dark palette, brass accent `#D8A03D`, consistent borders, clean typography).

---

## 🧪 Testing & Smoke Tests

Every pull request must pass the automated smoke test suite:
```bash
dotnet run --project tests/Knox.SmokeTest -c Release
```

The smoke test validates:
- Audio engine offline rendering & RMS signal flow.
- Instrument & effect state persistence and `.knox` project serialization round-trips.
- Time-stretch / Warping algorithms.
- Audio clip operations (Reverse, Normalize, Phase Inversion).
- Plugin hosting, parameter automation, and MIDI routing.
- Undo/redo stacks and track grouping hierarchies.

If you introduce a new feature or fix a bug, **add a corresponding test case to `tests/Knox.SmokeTest/Program.cs`**.

---

## 🚀 Submitting a Pull Request

1. **Branch Naming**: Use descriptive branch names (e.g., `feat/granular-pitch-envelope`, `fix/wasapi-sample-rate`, `docs/architecture-update`).
2. **Commit Messages**: Write clear, imperative commit messages (e.g., `Fix: prevent clip editor deselection on resize drag`).
3. **Smoke Test Verification**: Ensure `dotnet run --project tests/Knox.SmokeTest -c Release` passes cleanly.
4. **Pull Request Description**:
   - Explain what problem the PR solves.
   - List the platforms you tested on (Windows, macOS, Linux).
   - If UI changes were made, attach screenshots or short screen recordings.
   - Update `CHANGELOG.md` under `[Unreleased]` for user-facing changes.

---

## 🎹 Adding New DSP Devices & Synths

To add a new built-in instrument or effect, follow these four layers:

1. **Native DSP (`src/native/knox.engine/src/`)**:
   - Implement your DSP class deriving from `Device` or `Instrument`.
   - Register a unique numeric `DeviceKind` or `InstrumentKind`.
2. **C ABI (`src/native/knox.engine/include/nota/knox_engine.h`)**:
   - Expose parameter getters/setters or specialized configuration functions via the C ABI.
3. **Managed Interop & Application (`src/managed/Knox.Infrastructure/` & `Knox.Application/`)**:
   - Add P/Invoke signatures in `KnoxEngine.Devices.cs`.
   - Implement domain/view-model mapping in `Knox.Presentation`.
4. **Avalonia UI (`src/managed/Knox.App/`)**:
   - Create a dedicated device card control (e.g. `src/managed/Knox.App/Controls/MyDeviceCard.cs`).
   - Style with Ember Graphite theme tokens.

---

Thank you for helping make Knox the ultimate open-source DAW! 🎧✨


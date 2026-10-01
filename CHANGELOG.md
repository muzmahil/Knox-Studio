<!-- SPDX-License-Identifier: AGPL-3.0-only -->
# Changelog

Every notable change to Knox Studio is recorded in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the
project follows [semantic versioning](https://semver.org/): MAJOR.MINOR.PATCH. The
version in VERSION is the single source of truth. Entries run newest first.

Entry categories: Added, Changed, Fixed, Removed.

## [Unreleased]

### Added
- **Arrangement Grid Theming**: Select from curated timeline themes (Obsidian, Charcoal, Navy, Slate, Cyber) or custom HEX colors with dynamic bar, beat, and snap line styling.
- **Custom Background Wallpapers**: Custom background image picker with real-time opacity slider (0%–100%) and multi-mode layout rendering (Stretch, Fit, Fill/Cover, Center, Grid/Tile).
- **Track & Clip Styling Options**: Customizable clip corner radius (Rounded 4px, Rectangle 0px, Pill capsule), interior shader styles (Default, Glass, Flat), and high-contrast black track text and waveform rendering.
- **Extended Plugin Buffer Lengths**: Extended plugin buffer size settings (32 to 16384 frames) with a standardized default of 4096 frames for optimal 48kHz audio stability.

### Fixed
- **Global Transport & Navigation Shortcuts**: Fixed keyboard shortcut capture across all views and child panels via global tunneling routing.
- **Browser Panel Margin Collapse**: Fixed residual left column gap when hiding the sidebar browser.
- **Master Saturator & DSP Summing**: Fixed threshold slope transition on master saturation curve and improved denormal protection across audio processing blocks.


## [0.38.0] — 2026-09-16

### Highlights
- **Knox Studio Rebranding & Single-File Binary**: Rebranded to Knox Studio with self-contained single-file executable Knox Studio.exe.
- **Modern Custom Installer & Uninstaller**: Modern Avalonia installer (setup.exe) and clean standalone uninstaller (uninstall.exe).
- **Audio Clip DSP Processing**: Reverse Audio Clip, Multi-target Normalization, Polarity/Phase Inversion, Quick De-Click Micro-Fades, and Audition Preview.
- **Adaptive Dynamic Snapping**: Zoom-responsive grid and snapping ladder across both Arrangement Timeline and Piano Roll.
- **Piano Roll Smooth 2D Navigation**: Fixed zoom scaling jumps via stationary parent host coordinates.
- **Multilingual Support**: Real-time Turkish (Türkçe) and English language localization.
- **System Standardization**: Dedicated %APPDATA%\Knox Studio path, knox-YYYYMMDD.log log naming, project.knox recovery, .knox/.knoxproj project format and .knoxpreset preset format.

### Added
- **Audio Clip Reverse & DSP Actions**: Added native 
ota_clip_reverse_audio engine operation and non-destructive reverse buffer generation. Added Normalize presets (0 dB, -0.1 dB, -1.0 dB, -6.0 dB), Invert Phase (Polarity), and 5 ms Quick De-Click fades to AudioClipEditorView.
- **Audio Clip Audition Preview**: Added a dedicated audition toggle button in the Audio Clip editor with transport-aware playback.
- **Adaptive Snapping**: Automatic zoom-dependent subdivision selection (from 1/128th note up to 4 bars) across the arrangement timeline and piano roll.
- **Multilingual Localization**: Added LocaleManager with runtime language switching for English and Turkish (	r.json), integrated with preferences and localized UI strings.
- **Modern Setup & Uninstaller**: Created custom setup application (setup.exe) and dedicated uninstaller (uninstall.exe) with silent CLI and GUI support, complete shortcut and registry lifecycle management.
- **One-Click Build Script**: Added root uild.bat for all-in-one C++ compilation, managed solution build, smoke test verification, single-file publish, and installer generation.

### Changed
- **Branding & Paths**: Unified all paths under %APPDATA%\Knox Studio, log files to knox-YYYYMMDD.log, and recovery to project.knox.
- **Piano Roll Right-Click Drag Zoom**: Refined 2D right-click drag zoom using stationary parent/host coordinates (_lastHostPos), resolving jumping and erratic scaling when zooming small notes.
- **Sample Browser Preview**: Enhanced browser sample audition with play/stop toggle and auto-stop when the main transport plays.

### Removed
- **Legacy Gamepad Support**: Removed macOS-only Gamepad driver in favor of enhanced standard MIDI / MPE / Keyboard controllers.
- **Legacy Batch Uninstaller**: Removed outdated uninstall.bat.

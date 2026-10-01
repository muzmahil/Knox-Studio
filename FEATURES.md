<!-- SPDX-License-Identifier: AGPL-3.0-only -->
# Knox Studio — full feature list

A consolidated list of what Knox Studio (a cross-platform DAW) can do, as of version
**0.38.0** (plus changes in development on `main`). This document describes what is
implemented in the code, not what is planned. Sources: `CHANGELOG.md`, `README.md`,
`ARCHITECTURE.md`.

Contents
- [Platforms and distribution](#platforms-and-distribution)
- [Project and transport](#project-and-transport)
- [Views](#views)
- [Tracks and mixer](#tracks-and-mixer)
- [MIDI and virtual instruments](#midi-and-virtual-instruments)
- [Audio: recording, clips, warp, and DSP](#audio-recording-clips-warp-and-dsp)
- [Automation](#automation)
- [Built-in devices](#built-in-devices)
- [Plugin hosting](#plugin-hosting)
- [Browser and assets](#browser-and-assets)
- [Export](#export)
- [Windows and interface](#windows-and-interface)
- [Preferences and storage](#preferences-and-storage)
- [Other](#other)

---

## Platforms and distribution

- **macOS** (≥ 13.0, universal arm64 + x86_64): CoreAudio, CoreMIDI, **AU + VST3**
  hosting. Distributed as a universal `.dmg` / `.app` (ad-hoc signed).
- **Windows** (x64): WASAPI (shared + **exclusive mode** for minimum latency), WinMM
  MIDI, **VST3** hosting. Distributed as a self-contained single-file executable
  (`Knox Studio.exe`), packaged with a modern custom installer (`setup.exe`) and standalone
  uninstaller (`uninstall.exe`).
- **Linux**: PulseAudio/ALSA (via miniaudio), ALSA MIDI (RtMidi), **VST3** hosting.
  Distributed as a portable **AppImage** (`Knox-<version>-<arch>`).

Architecture: engine and DSP in C++20 (CMake + Ninja), UI in .NET 10 / Avalonia 12 (C#),
with a C ABI + P/Invoke boundary between them. Plugin hosting is isolated in a separate
JUCE module; the engine core is JUCE-free.

---

## Project and transport

- **Project**: create / open / save (`.knox` / `.knoxproj` bundles), autosaves (`project.knox`),
  undo/redo for every editing operation.
- **Adaptive (Dynamic) Snapping**: Zoom-responsive grid and snapping ladder (from 1/128 up to 4 bars)
  across both Arrangement timeline and Piano Roll editor.
- **Transport**: play / stop / record, playhead position.
  - **Stop returns to the launch point** (a seek sets the start anchor).
  - **Follow** — the arrangement follows the cursor (playhead stays centred).
  - **Launch quantize** (Q): None … 1/16 … 4 Bars — applied when launching slots and
    scenes in Session.
  - **Position**: clicking the readout toggles bars ↔ time (mm:ss.ms).
- **Tempo (BPM)** and **time signature** (edited by dragging; the denominator snaps to a
  power of two), grid and quantization.
- **Loop region** (on/off and range; Cmd/Ctrl+L loops the selection).
- **Metronome** and **count-in** before recording.
- **Transport shortcuts** (Space play/stop, Enter stop, R/M/A, clip copy-paste, typing
  notes, undo/redo) work in detached windows too.

---

## Views

Three top-level views, switched from the toolbar (**Arrangement · Session · Modular**);
the mixer opens as a separate floating window (**View → Mixer**, ⌘M).

- **Arrangement View** — a linear timeline: place, move, trim and duplicate clips; adaptive
  grid and snap to bars/beats; scroll and zoom; recording into the arrangement (audio and
  MIDI); split at the cursor or across a selection (Cmd/Ctrl+E cuts every track in the
  selection); clip scrubbing on the ruler.
- **Session View** — a clip grid (tracks × scenes): launch and stop a clip, launch a whole
  scene, launch quantization, recording into a clip slot (MIDI or audio), loop clips with
  a configurable length, and moving material between Session and Arrangement.
- **Modular View** — a signal-graph editor for the track's chain: MIDI FX → instrument →
  effects shown as nodes you can expand, bypass, duplicate, delete and reorder right on the
  canvas (node positions are saved with the project), plus a **Global view** that shows
  tracks as islands with cross-track connections. Its centrepiece is **CV modulation** —
  drag a modulator's CV output onto any device, instrument or MIDI-FX parameter to link
  them with a patch cable; click an edge to edit depth and mode, select and delete edges.
  Modulators: **LFO** (with phase), an **envelope follower**, **MIDI→CV**
  (velocity/gate/note), **ADSR** (gated by notes), **Macro** (a manual control → CV) and
  **Math** (two CV inputs → one output); any parameter can also be a CV source
  (param → param). A **CV Scope** oscilloscope reads any modulator signal, and CV ports are
  drawn only where they actually work.

---

## Tracks and mixer

- **Track types**: Audio, MIDI/Instrument, Return, Master, **Group** (nestable submixes —
  ⌘/Ctrl+G to group, ⌘/Ctrl+Shift+G to ungroup).
- **Per track**: volume, pan (bipolar bar), mute, solo, arm (ready to record).
  Double-click resets to the default.
- **Reordering** by dragging the name row (with an accent insertion line); returns stay
  after regular tracks; the group hierarchy is saved with the project.
- **Routing**:
  - Input (record source) and output (to master, a bus, or a group).
  - **MIDI routing between tracks** — an instrument track can take MIDI from another
    instrument track ("MIDI In") and play the same material through its own device
    (layering). The source's MIDI output is passed on **after its MIDI effects**, so an
    arpeggiator or other MIDI FX drives the receiver. Selectors live in the track header,
    the mixer channel I/O, and the context menu. A muted source keeps sending MIDI.
    Recording on the receiver prints the incoming MIDI into a clip. Saved with the
    project.
  - **Send/Return** buses (sends to returns) with a return list.
- **Level meters** (peak/RMS) on every channel and on the master, plus true peak.
- **Device chain** per track: instrument + effects + MIDI effects, laid out in a row.

---

## MIDI and virtual instruments

- **MIDI clip**: notes (pitch, start, length, velocity).
- **Piano Roll**: draw, move, stretch and delete notes; quantization; adaptive grid length; a
  velocity editor; transposing the selection.
  - **Smooth 2D Zooming**: Right-click drag zooming computed from stationary parent host coordinates
    eliminates jumping or erratic scaling.
  - Scroll and zoom are remembered per clip; a new clip centres on its own notes.
  - A MIDI clip trimmed in the arrangement mutes note tails at its boundary.
  - Live edits (moving a note, changing its length) reach the engine immediately, and a
    whole drag is a single undo step.
- **Input**: MIDI keyboard / MPE controller (live play and recording), and the **computer keyboard**
  plays MIDI (the S–K row, sharps on W E T Y U) — including while a hosted plugin window has focus.
- **Highlighting** of the pressed key on the roll's keyboard and as a bar along its row.
- **Audio→MIDI** (right-click an audio clip → Convert):
  - **Convert Melody** — monophonic pitch detection (YIN) → a new Knox Synth track.
  - **Convert Harmony** — polyphonic (STFT + spectral peak picking) → chords.
  - **Convert Drums** — hit detection + kick/snare/hat classification → a 3-pad kit and a
    MIDI pattern.
  - **Slice to New MIDI Track** — slices the audio at transients (or into 16 beats) across
    the pads of a new Drum Rack, plus a MIDI clip.
- **MIDI effects** — see [Built-in devices](#built-in-devices).

---

## Audio: recording, clips, warp, and DSP

- **Recording** from an input (microphone, line, interface) onto an audio track, with
  monitoring (in/auto/off).
- **Recording from an internal bus** (a group, return or master output into a new audio
  track) — dropped frames are padded with silence, so there is no cumulative drift.
- **Import** of audio files by drag and drop: WAV / AIFF / FLAC / MP3.
- **Audio clip operations & DSP**:
  - **Reverse Clip** — non-destructive reverse buffer generation in the native engine with
    instant UI preview.
  - **Normalize (0 dB, -0.1 dB, -1.0 dB, -6.0 dB)** — peak scanning and precise gain computation
    with automatic clip gain updating.
  - **Invert Phase (Polarity)** — instant sample polarity inversion.
  - **Quick De-Click Fades** — automatic 5 ms micro-fades at start/end to eliminate zero-crossing clicks.
  - **Audition Preview Button** — dedicated preview play/stop control in the Audio Clip editor.
- **Audio clip basics**: start and end, waveform drawing, trimming, split, fade in/out, clip
  gain. Changing the length shows the material actually being revealed or hidden.
- **Warp / time-stretch**: **Complex** and **Complex Pro** modes (the latter with formant
  preservation, correct even when the sample rate and the device rate differ), plus
  transient detection. A grid-snap toggle governs trimming a warped clip.
- **Changing the sample rate** does not break third-party plugins or warped clips — the
  caches are rebuilt once when the rate changes.

---

## Automation

- **Automation lanes** on tracks, with curved segments (Alt+drag) and points (double-click
  to add, double-click a point to remove).
- **Draw mode** (the `A` key): drawn automation changes the device parameter in real time,
  and the whole drag is one undo step.
- **The lane follows focus** — touch any knob or fader on any device (built-in, plugin, or
  mixer) and the automation lane switches to that parameter.
- **Already-automated parameters are highlighted** in the target picker (a brass dot, and
  they sort to the top of the list).
- **Selection and paste**: shift-select a range, Cmd/Ctrl+D to duplicate, and paste that
  does not leave redundant points behind.
- **Recording** automation (arm), and reading it during playback and scrubbing (volume and
  pan controls follow the automation).
- **MIDI Learn** — the MIDI button in the top right enters learn mode: mappable controls
  light up, and a click plus a move on the controller creates the binding. Mappable
  targets cover every built-in instrument, effect and MIDI FX parameter (switches, ADSR
  and filter included), track volume/pan/mute/solo, the master, the transport, rack
  macros, and chain and Drum Rack pad mute/solo. The **Map** tab in the browser exposes
  range, inversion and deletion. Saved with the project.

---

## Built-in devices

Every built-in device uses the same 700×260 shell with a shared header: on/bypass dot,
name, subtitle, preset picker, **A/B comparison** (parameter snapshots), stereo meter,
voice count (on synths), move arrows, delete, and the ⠿ drag handle. All support factory
and user presets (`.knoxpreset`), automation, persistence and cloning.

### Instruments
- **Knox Synth** — subtractive synthesizer (the basic one).
- **Knox Sampler** — sampler (one-shot and loop), voice modes Poly 16 / Mono / Choke, loop
  crossfade, reverse loop, filter key-tracking, Vel→Vol; Sample · Pitch · Env · Filter
  tabs; an editable waveform.
- **Knox Grain** — granular synthesizer.
- **Knox Volt** — subtractive, with a 7×6 modulation matrix, 8 macros, extended LFOs
  (shapes, fade-in, tempo sync), oscillator start phase, 12/24 dB filter slope, Mono/Poly.
- **Knox Aurora** — wavetable synth: two wavetable oscillators (16 frames per bank, warp
  Off/Sync/Bend/PWM/Fold) plus a sub oscillator and unison, two filters with routing, two
  envelopes, two LFOs, an 8×7 mod matrix, 4 macros, built-in drive/chorus/reverb,
  Mono/Poly.
- **Knox Operator** — FM synth, 4 operators, 11 algorithms (with interactive diagrams and
  drag-to-reroute), spectrum display, VEL→FM, Glide, Mono.
- **Knox Pendulum** — a "pendulum" sequencer: swinging balls generate notes, with swing
  curves (Linear/Pendulum/Ease/Bounce), bipolar Rate, a held chord, Scale, Hold, First
  Note, Reset, Humanize and Pan Spread.
- **Knox Bass** — bass synth, mono (legato with glide) and poly modes.
- **Knox Physical** — physical modelling.
- **Knox Flux** — vector-morphing analog synth: an XY pad with four "timbre worlds"
  (WARM/GLASS/MOOG/GRAIN), **React** (sidechain modulation from another track:
  Filter/Pitch/Space/vector), 5 macro knobs (Age/Motion/Filter/Env/Space), and a sidechain
  scope.
- **Knox Rhythm** — drum machine: 8 voices (analog and FM kick, noise snare, metallic
  hats, clap, rim, tom, perc), a 16-step sequencer (locked to the transport), 4 pattern
  banks (A–D), accents, per-step velocity, Swing/Humanize, and a per-voice **Sample** mode
  (load your own one-shot). Six factory kits (808/909/Trap/House/Lo-Fi/Techno).

### Audio effects
- **Knox EQ-8** (kind 0) — 8-band parametric.
- **Knox Compressor** (1) — 5 character models (Clean/Glue/Punch/Opto/FET), soft knee,
  look-ahead, Peak/RMS, auto-release, auto-gain, Range, sidechain HP/LP + Listen, MIX;
  transfer plot and gain-reduction history.
- **Knox Reverb** (2) — Hall/Room/Plate/Chamber, RT60, HF damp, pre-delay, size,
  diffusion, low/high cut, width, tail modulation, Freeze; an interactive tail graph.
- **Knox Delay** (3) — independent L/R times (ms or tempo-synced 1/16…1/2 with triplets
  and dotted values), Link, feedback, spread, ping-pong, tape WOW modulation, Freeze; an
  echo-tap graph.
- **Knox Utility** (4) — gain, L/R balance, stereo width (mid/side 0–400%), channel mode
  (Stereo/Left/Right/Swap), bass mono, mute, phase invert; goniometer/vectorscope,
  correlation meter, meters, Gain match.
- **Knox Valve** (5) — tube amplifier and saturator.
- **Knox Auto Filter** (6) — LP/BP/HP/NO, 12/24 slope, Clean/Analog, ENV + LFO modulation
  (with tempo sync and stereo phase), sidechain; an interactive response graph.
- **Knox Auto Shift** (7) — real-time vocal pitch correction: pitch detection, Key +
  Scale, Follow scale device, correction amount and speed, Range (guards against octave
  jumps), Formant, Mix; a PITCH TRACE visualiser.
- **Knox Vintage** (8) — degradation and saturation: 6 era modes
  (Vinyl/Cassette/Reel/VHS/Tube/Analog), wow/flutter, noise, crackle, wear.
- **Knox Beat Repeat** (9) — beat repeat with Mix/Insert/Gate modes, Chance, Gate, Repeat,
  Latch, INTERVAL/GRID, Pitch/Decay/Volume, FILTER, Mix; a TIMELINE visualiser.
- **Knox Orbit** (10) — auto-pan and tremolo, L/R phase (0° = tremolo …
  180° = pan), 5 LFO shapes, Shape, Mix.
- **Knox Crush** (12) — bit crusher: Bit Depth, Sample Rate, Drive, Wet, Anti-Alias;
  Digital/Analog/Fold modes; GRIT (Dither/Jitter/Noise); an OUTPUT filter; quantizer and
  aliasing-spectrum visualisers.
- **Knox Ceiling** (14) — look-ahead brickwall limiter: drive, ceiling, release (with
  auto), Clean/Punch/Glue characters, lookahead, stereo link, sidechain; a LIVE strip with
  GR, input/output history, LUFS-S / LUFS-I / true peak.
- **Knox Dynamic EQ-8** (13) — 8-band parametric with dynamics: each band Static/Duck/Lift,
  threshold, range, attack/release, sidechain, Solo band; a dual curve (static plus
  momentary) and a band table with GR.
- **Knox Strata** (15) — multi-layer overdub looper: layers with waveform, level and mute;
  Record/Overdub/Play/Stop, Undo/Clear, Feedback, input gain, speed/reverse, quantize,
  count-in, set-tempo, Export; audio layers persist into the project.
- **Knox Forge** (17) — multi-stage saturator: 3 stages
  (Tube/Diode/Tape/Fuzz/Digital/Fold), Serial/Parallel/Mid-Side/Multiband routings,
  Amount/Tone/Wet, Bias/Width, LFO→Drive and Env→Tone; a transfer curve and a harmonics
  chart; **OVERSAMPLE** (Off/2×/4×/8×).
- **Knox EQ-3** (16) — 3-band performance EQ: Low/Mid/High faders with a 0 dB detent and
  KILL buttons, two crossover frequencies, 24/48 dB/oct slope (Linkwitz-Riley); a response
  curve with a real-time spectrum.
- **Knox Level** (18) — automatic loudness matching (LUFS): LUFS measurement (BS.1770),
  AUTO/MATCH, true-peak safe, sidechain (match to reference); loudness history and
  IN/OUT/TP/correlation meters.
- **Knox Shutter** (19) — noise gate and ducker: Threshold/Return (hysteresis),
  Attack/Hold/Release, Floor, Lookahead, Flip (ducker), band-pass sidechain with Listen; a
  signal graph, IN/GR meters, LED.
- **Knox Chorus** — modulation (chorus) effect.
- **Forge**, **Valve** and **Vintage** each carry an **OVERSAMPLE** selector
  (Off/2×/4×/8×) to suppress aliasing under heavy drive.

### MIDI effects
- **Knox Arp** — arpeggiator: step sequencer with Velocity/Length/Chance/Ratchet/Transpose
  lanes, Order, Oct 1–4, Dir (↑↓↕?), Hold/Retrig, Free/Sync, Gate/Swing.
- **Knox Scale** — snap to a scale: Root, Major/Minor/Dorian/Phryg/Penta/Custom, Fold
  (Nearest/Down/Up), NOTE MAP, Range, Follow Key, Learn/Clear.
- **Knox Length** — note lengths: Sync/ms/Gate %, Vel→Len, Key→Len,
  Random, Legato, clip length limit; a GATE visualiser.
- **Knox Velocity** — velocity transformation: Curve/Compand/Fixed, Drive, Random, Out
  Range, Random Dir, and a Last 12 histogram.
- **Knox Random** — randomization: Chance, Gauss/Even/Walk, Lock seed / Re-roll,
  Note/Velocity/Timing/Skip/Octave amounts, Distribution, Rate, Stay in scale.
- **Knox Chord** — chord generator: Maj7/Min7/Sus4/5th/Custom, 6 voices with offset and
  velocity, Strum, Keep root, Fold in scale, Spread; a preview keyboard.

### Racks
- **Knox Instrument Rack** — 8 named macros (mapped with Linear/Exp/Log/S curves), chains
  with **key and velocity zones**, gain/meter/M·S, horizontal device cards (GUI / Params),
  a zone map, rack output (Volume/Glide), Fold/Save.
- **Knox Drum Rack** — 4×4 pads (banks C1–C4, up to 64 pads), Pads and Mixer views, per-pad
  Volume/Pan/Tune/Decay, choke groups (monophonic cut), Swing/Humanize, hot-swap.
- **Knox Audio Effect Rack** — **Parallel / Series / Select** modes (by input level),
  Dry/Wet + Gain, PDC, Fold, Save; chains with gain/meter/M·S; named macros and mappings;
  the "+ Device" menu lists built-in effects and a Plug-ins submenu.
- Devices inside chains appear as cards with knobs, a **Full** button (the device's
  complete UI in a popup — the native GUI for a plugin, the tabbed editor for the
  Sampler), a ✕ delete button, and drag-to-reorder.

---

## Plugin hosting

- **AU** (macOS) and **VST3** (all platforms) — instruments and effects.
- **Scanning** and a catalogue of installed plugins (a separate `knox-scanworker`), scan
  paths in Preferences, and Rescan.
- **Loading** into a track or rack chain, with the plugin's **native GUI** (an editor
  window that opens on top and focused, and closes when the device is removed).
- **State save and restore** in the project, and **bypass**.
- **PDC** — plugin delay compensation (including a toggle in the Audio Effect Rack).
- **Transport sync** — plugins receive tempo, position, play state and loop boundaries
  through the host playhead (synced devices, tempo delays and LFOs, arps, loopers).
- **The computer keyboard plays MIDI** while a hosted plugin window has focus.
- Plugin parameters are **MIDI-learnable** and **automatable**.

---

## Browser and assets

- Tabs: **Samples** (files, with folder tree navigation), **Projects**, **Presets** (a tree
  of category → device → preset), **Plugins** (AU/VST3), **Instruments / Audio Effects /
  MIDI Effects**, and **Map** (MIDI Learn mappings).
- **Preview** a sample from the browser with transport auto-stop sync, and **drag and drop** onto a track, into the grid,
  or into a rack chain.
  - Dropping an instrument onto an existing track **replaces the instrument** in place
    (clips, devices and volume are kept); dropping onto empty space creates a new track.
    Racks are not replaced in place.
- **Favourites** (★) and **tags** (assign and clear, an editor with a title and colour,
  filter chips in the header); favourited devices sort to the top of their lists.
- **Context menus**: Projects — Open / Reveal in Explorer/Finder / Delete (to the Recycle Bin/Trash, with
  confirmation); Files — Reveal; Presets — Reveal.
- **Hints for empty tabs** — explaining what the tab is and how to add content to it.

---

## Export

- **Export the master** to WAV: range, sample rate, bit depth
  (**pcm16 / pcm24 / float32**).
- **Export stems** (individual tracks).
- **Normalize −1 dBTP** — against the true (inter-sample) peak, estimated with 4×
  oversampling: one render to a temporary file, peak measurement, then a rewrite with the
  exact make-up gain, so there is no risk of overshooting the ceiling because a second
  render differed. Stems share a single gain (taken from the full mix peak), so they still
  sum to −1 dBTP and keep their balance.
- **Dither** — TPDF dithering before quantizing to 16 bit (offered only for 16 bit; 24 bit
  and float do not need it).
- **Add 1-bar release tail** — a decay tail after the end of the project.
- **Bit-identical** chunked rendering (caches rebuilt once when the rate changes) — no
  clicks at block boundaries, and warped clips keep their length at any export rate.
- **Progress** for long operations: export and conversion show a modal progress dialog;
  audio import shows a thin bar in the status bar.

---

## Windows and interface

- **A single frameless window style**: dark centred title, drag by the header, the macOS
  traffic lights in a left inset (Windows gets its own custom dark title bar; Linux uses the system
  frame).
- **What's New** — a window showing changelog entries newer than `LastSeenVersion`, once
  after the first launch on a new version.
- **About** — the app and engine versions, the copyright notice, and a pointer to the
  third-party attribution notices (`LICENSES/THIRD-PARTY-NOTICES.md`).
- **Preferences** — a consistent design (the house checkboxes, sunken fields).
- **Edit Tags** — the browser's tag editor.
- **Devices/Clip panel in its own window** — the ⧉ button in the bottom panel's header
  detaches it (the selected clip's piano roll on top, the device chain below, both at
  once). The content moves across as-is: edits, meters and graphs all keep working live.
- **Popup rack editors** (full UI / Params).
- **A shortcut list** in Preferences → Shortcuts, grouped by section (Transport,
  Arrangement & Editing, Piano roll, Play notes, Mouse).

---

## Preferences and storage

- **Audio**: device, sample rate, buffer size (latency); **WASAPI exclusive mode**
  (Windows).
- **MIDI**: which MIDI inputs are enabled (the house checkboxes).
- **Language / Localization**: Real-time switching between English and Turkish (Türkçe).
- **Plugins**: scan paths, Rescan.
- **Library**: library folders.
- **Appearance**: Theme selection (Ember dark / Light).
- **Shortcuts**: the shortcut list.
- **Storage Locations**:
  - Windows: `%APPDATA%\Knox Studio\` (settings `knox.settings`, logs `logs/knox-YYYYMMDD.log`, crash recovery `project.knox`).
  - macOS: `~/Library/Application Support/Knox Studio/`.
  - Linux: `~/.config/knox-studio/`.

---

## Other

- **Undo/redo** for every editing operation.
- **Semantic versioning** with a single source of truth (`VERSION`), a changelog in Keep a
  Changelog format, and `vX.Y.Z` git tags.
- **License**: AGPL-3.0-only.
- **Building**: `build.bat` (Windows all-in-one), `scripts/build.sh` (macOS), `scripts/build-win.ps1` (Windows),
  `scripts/build-linux.sh` (Linux); packaging via `build.bat`, `bundle-mac.sh`, `package-dmg.sh`,
  `package-win.ps1`, `package-linux.sh`.
- **Release CI**: a GitHub Actions workflow builds and packages every target (x64 + arm64
  across macOS, Windows and Linux) on a `vX.Y.Z` release tag, and drafts the GitHub release
  with the matching `CHANGELOG.md` section as its body.
- **Tests**: an engine smoke test after every build (`Knox.SmokeTest`).
- **Third-party notices**: bundled/linked dependencies and their copyrights are recorded in
  [`LICENSES/THIRD-PARTY-NOTICES.md`](LICENSES/THIRD-PARTY-NOTICES.md).


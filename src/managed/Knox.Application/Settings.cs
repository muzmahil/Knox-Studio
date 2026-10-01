// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// App settings contract. The interface + data live in Application (ViewModels
// depend on them); the JSON-on-disk implementation is SettingsService in
// Infrastructure.

using System.Collections.Generic;

namespace Knox.Application;

public enum ToolbarSide { Left, Right }

public sealed class Settings
{
    public ToolbarSide ToolbarSide { get; set; } = ToolbarSide.Left;
    /// <summary>Browser sample library folder ("" = default ~/Music/Knox Samples). M7-4.</summary>
    public string SamplesFolder { get; set; } = "";
    /// <summary>Additional sample / audio library folders to scan and display in Browser.</summary>
    public List<string> ExtraSampleFolders { get; set; } = new();
    /// <summary>Browser projects folder ("" = default ~/Documents/Knox Projects). M7-4.</summary>
    public string ProjectsFolder { get; set; } = "";
    /// <summary>Additional project folders to scan in Browser.</summary>
    public List<string> ExtraProjectFolders { get; set; } = new();
    /// <summary>Last app version whose "What's New" the user has already seen ("" = never).
    /// Compared against the running app version on launch to show the changelog once.</summary>
    public string LastSeenVersion { get; set; } = "";
    /// <summary>Recently opened/saved project bundle paths, most-recent first. Shown on the
    /// welcome screen; capped and pruned of missing folders when displayed.</summary>
    public List<string> RecentProjects { get; set; } = new();
    /// <summary>Show the welcome screen (recent projects launcher) on startup. On by default;
    /// toggled from the welcome screen's "Show on startup" checkbox.</summary>
    public bool ShowWelcomeOnStartup { get; set; } = true;
    /// <summary>Startup action when launched ("Welcome", "NewProject", "LastProject").</summary>
    public string StartupAction { get; set; } = "Welcome";
    /// <summary>Application language code ("en", "tr", etc.).</summary>
    public string Language { get; set; } = "en";

    // Audio Hardware & Driver Settings
    public string AudioDriverType { get; set; } = "WASAPI"; // WASAPI, ASIO
    public string AsioDriverName { get; set; } = "";
    public bool AudioAutoClose { get; set; } = false;
    public double AudioBufferOffsetPercent { get; set; } = 0.0;
    public string AudioPriority { get; set; } = "Highest"; // Normal, High, Highest, Realtime
    public bool AudioSafeOverloads { get; set; } = true;
    public string AudioPlaybackTracking { get; set; } = "Mixer"; // Driver, Hybrid, Mixer
    public double AudioPlaybackOffsetPercent { get; set; } = 0.0;
    public bool AudioUsePolling { get; set; } = false;
    public bool AudioUseHardwareBuffer { get; set; } = true;
    public bool AudioUse32BitBuffer { get; set; } = true;

    // CPU Processing Settings
    public bool CpuMultithreadedGenerators { get; set; } = true;
    public bool CpuMultithreadedMixer { get; set; } = true;
    public bool CpuSmartDisable { get; set; } = false;
    public bool CpuAlignTickLengths { get; set; } = true;

    // Mixer Engine Settings
    public string MixerResamplingQuality { get; set; } = "24-point sinc"; // Linear, 6-point hermite, 16-point sinc, 24-point sinc, 64-point sinc, 128-point sinc, 512-point sinc
    public int MixerBrowserPreviewTrack { get; set; } = 0; // 0 = Master, 1..N = track id
    public int MixerMetronomeTrack { get; set; } = 0;      // 0 = Master, 1..N = track id
    public bool MixerPlayTruncatedNotes { get; set; } = true;
    public bool MixerResetPluginsOnTransport { get; set; } = false;

    // Extended MIDI Settings (FL Studio style)
    public Dictionary<string, int> MidiOutputPorts { get; set; } = new();
    public Dictionary<string, int> MidiInputPorts { get; set; } = new();
    public Dictionary<string, string> MidiControllerTypes { get; set; } = new();
    public bool MidiSendMasterSync { get; set; } = false;
    public bool MidiSendAllNotesOffOnStop { get; set; } = true;
    public int MidiMasterSyncOffsetMs { get; set; } = 0;
    public string MidiSyncType { get; set; } = "MIDI clock"; // MIDI clock, MTC 24 fps, MTC 25 fps, MTC 30 fps
    public string MidiLinkNoteOnVelocity { get; set; } = "Velocity"; // Velocity, (none), Mod wheel, Fixed 100, Fixed 127
    public string MidiLinkReleaseVelocity { get; set; } = "Release"; // Release, (none), Linear Ramp
    public bool MidiPickupMode { get; set; } = false;
    public bool MidiAutoAcceptController { get; set; } = true;
    public bool MidiFootPedalNoteOff { get; set; } = true; // Sustain pedal (CC 64)
    public int MidiOmniPreviewChannel { get; set; } = 0; // 0 = --- (off), 1..16
    public int MidiSongMarkerJumpChannel { get; set; } = 0;
    public int MidiPerformanceModeChannel { get; set; } = 0;
    public int MidiGeneratorMutingChannel { get; set; } = 0;
    public bool MidiToggleOnRelease { get; set; } = false;
    public string MidiExternalClockSync { get; set; } = "Off"; // Off, Auto-detect, MIDI Clock In
    public int MidiExternalSyncOffsetMs { get; set; } = 0;
    public bool MidiVelocitySensitivity { get; set; } = true;
    public int MidiFixedVelocityValue { get; set; } = 100; // 1..127
    public string MidiVelocityCurve { get; set; } = "Linear"; // Linear, Soft, Hard, Compressed, Fixed
    public Dictionary<string, string> MidiActionMappings { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> DeviceMidiActionMappings { get; set; } = new();

    public int DefaultBpm { get; set; } = 120;
    public int DefaultTimeSigNumerator { get; set; } = 4;
    public int DefaultTimeSigDenominator { get; set; } = 4;
    public int AutoSaveIntervalMinutes { get; set; } = 5;
    public int CountInBars { get; set; } = 1;
    public bool CountInOnRecord { get; set; } = true;
    public bool CountInOnPlayback { get; set; } = false;

    // Appearance & Customization
    public string ThemeVariant { get; set; } = "SpaceGrey"; // SpaceGrey, Obsidian, WarmCharcoal
    public string ThemeAccent { get; set; } = "StudioBlue";  // StudioBlue, EmberGold, CyberCyan, Emerald, Crimson
    public double UiScale { get; set; } = 1.0;
    public string GridContrast { get; set; } = "Normal";     // Subtle, Normal, High
    public string KnobStyle { get; set; } = "Tactile";       // Tactile, Minimal, Vintage
    public bool GlowEffects { get; set; } = true;
    public int MeterFps { get; set; } = 60;

    // LCD Display Customization
    public string LcdColorTheme { get; set; } = "Cyan"; // Cyan (Studio Ice Blue), Amber, Green, White, Red, Purple, Gold
    public string LcdFontFamily { get; set; } = "Default"; // Default, Consolas, Courier New, Segoe UI, Lucida Console, JetBrains Mono, Arial, Trebuchet MS, Roboto Mono, or custom
    public string LcdTimeFormat { get; set; } = "Bars";  // Bars, Time, Dual
    public bool LcdShowStatusBadge { get; set; } = false;
    public bool LcdShowKey { get; set; } = true;
    public bool LcdShowQuantize { get; set; } = true;
    public bool LcdShowTelemetry { get; set; } = true;

    // Arrangement Grid & Background Customization
    public string ArrangementGridColor { get; set; } = "Default"; // Default, Obsidian, Charcoal, Navy, Slate, Cyber, Custom
    public string ArrangementGridCustomColor { get; set; } = "#18191C";
    public string ArrangementBgImagePath { get; set; } = "";
    public double ArrangementBgImageOpacity { get; set; } = 0.20; // 0.0 .. 1.0
    public string ArrangementBgImageMode { get; set; } = "Stretch"; // Stretch, Fit, Fill, Center, Grid

    // Audio Track & Clip Styling
    public string ClipShape { get; set; } = "Rounded"; // Rounded, Rectangle, Pill
    public string ClipInteriorStyle { get; set; } = "Default"; // Default, Glass, Flat
    public bool ClipDarkTextAndWaveform { get; set; } = false; // Black track text & waveform

    // Editing & Workflow
    public string ArrangementSnap { get; set; } = "Adaptive (Dynamic)";
    public bool BrowserAutoPreview { get; set; } = false;
    public double PianoRollRowHeight { get; set; } = 16.0;
    public double DefaultClipLength { get; set; } = 4.0;
    public string PianoRollDefaultSnap { get; set; } = "1/16";
    public bool FollowPlayheadDefault { get; set; } = true;
    public int PlayheadFollowMode { get; set; } = 0; // 0 = Paged (Default), 1 = ContinuousLeft, 2 = ContinuousCenter
    public bool MiddleMousePanInvert { get; set; } = false;
    public bool CompOverwriteMode { get; set; } = true;

    // Integrations & Discord Rich Presence
    public bool DiscordRpcEnabled { get; set; } = true;

    // VST & Plug-in Hosting Settings
    public bool VstScanValidation { get; set; } = true;
    public List<string> DisabledPluginIds { get; set; } = new();

    /// <summary>Use a connected gamepad as a live note source. Off by default; the
    /// pads play through the armed/audition instrument track like the computer
    /// keyboard. See Preferences → Gamepads.</summary>
    public bool GamepadEnabled { get; set; }
    /// <summary>Base-octave shift for gamepad notes, in semitones ÷ 12 (d-pad
    /// up/down live-shifts this)</summary>
    public int GamepadOctave { get; set; }
}

public interface ISettingsService
{
    Settings Current { get; }
    void Save();
    /// <summary>Fired after Save() so views can re-apply settings live.</summary>
    event Action? Changed;

    /// <summary>The sample library folder to browse (resolved default, created). M7-4.</summary>
    string ResolvedSamplesFolder();
    /// <summary>The projects folder to browse (resolved default, created). M7-4.</summary>
    string ResolvedProjectsFolder();
    /// <summary>App-managed presets folder (created). M7-4.</summary>
    string PresetsFolder();
}

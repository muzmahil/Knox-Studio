// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// M4.1-A: root view model. Owns the engine lifetime + the playhead clock and
// exposes child VMs. As later sub-steps land, more of the window (arrangement,
// browser, device chain) moves under here; for now the arrangement/toolbar/
// keyboard stay in code-behind but drive the engine via this VM.

using CommunityToolkit.Mvvm.ComponentModel;
using Knox.Application;

namespace Knox.Presentation;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IPeriodicTimer _clock;
    private readonly IPeriodicTimer _autosave;
    private readonly EngineBuildInfo _build;
    private readonly ILogSink _log;
    private readonly IDiscordRpcService? _discordRpc;
    private int _lastXrunCount;
    private int _discordTickCounter;
    private string _projectName = "Untitled";

    public IAudioEngine Engine { get; }
    public TransportViewModel Transport { get; }
    public BrowserViewModel Browser { get; }
    public ISettingsService Settings { get; }
    public IDiscordRpcService? DiscordRpc => _discordRpc;

    /// <summary>Fired ~30 Hz with the playhead position in beats.</summary>
    public event Action<double>? PlayheadUpdated;
    /// <summary>Fired ~30 Hz only while recording (to refresh captured notes).</summary>
    public event Action? RecordingTick;
    /// <summary>Fired every ~30 s so the window can autosave a crash-recovery snapshot (M7-7).</summary>
    public event Action? AutosaveRequested;

    [ObservableProperty] private string _engineInfo = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private ToolbarSide _toolbarSide = ToolbarSide.Left;

    // Background-operation strip in the status bar (see MainWindow.RunBackgroundAsync).
    // Visible only while BackgroundBusy; Progress is 0..100, or Indeterminate when the
    // phase length isn't known yet.
    [ObservableProperty] private bool _backgroundBusy;
    [ObservableProperty] private string _backgroundLabel = "";
    [ObservableProperty] private double _backgroundProgress;
    [ObservableProperty] private bool _backgroundIndeterminate = true;

    /// <summary>
    /// When true, the playhead clock stops touching the engine. Set during
    /// offline export (M6-4) so the render thread has exclusive engine access
    /// while the audio backend is stopped.
    /// </summary>
    public bool SuspendEnginePolling { get; set; }

    public MainWindowViewModel(IAudioEngine engine, TransportViewModel transport,
                               BrowserViewModel browser, ISettingsService settings,
                               IPeriodicTimerFactory timers, EngineBuildInfo build,
                               ILogSink log, IDiscordRpcService? discordRpc = null)
    {
        Engine = engine;
        Transport = transport;
        Browser = browser;
        Settings = settings;
        _build = build;
        _log = log;
        _discordRpc = discordRpc;

        Engine.Start();
        Engine.SetBpm(120);
        Engine.SetTimeSignature(4, 4);
        // EngineInfo = $"v{_build.Version} · {Engine.SampleRate:0} Hz";
        EngineInfo = $"{Engine.SampleRate:0} Hz";
        _log.Info($"Engine started · {Engine.SampleRate:0} Hz");

        Transport.Message += m => { if (m.Length > 0) StatusText = m; };

        ApplyToolbarSide();
        Settings.Changed += ApplyToolbarSide;

        ApplyMidiVelocitySettings();
        Settings.Changed += ApplyMidiVelocitySettings;

        _clock = timers.Create(TimeSpan.FromMilliseconds(16), OnTick);
        _clock.Start();

        // Crash-recovery autosave (M7-7): the window captures + writes a snapshot,
        // gated by a manifest diff so idle sessions don't churn the disk.
        _autosave = timers.Create(TimeSpan.FromSeconds(30), OnAutosaveTick);
        _autosave.Start();
    }

    public void UpdateProjectName(string projectName)
    {
        _projectName = string.IsNullOrWhiteSpace(projectName) ? "Untitled" : projectName;
        _discordRpc?.UpdatePresence(_projectName, Engine.TrackCount, (double)Transport.Bpm, Engine.IsPlaying, Engine.IsRecording);
    }

    private void ApplyToolbarSide() => ToolbarSide = Settings.Current.ToolbarSide;

    private void OnTick()
    {
        if (SuspendEnginePolling) return; // export in progress — render thread owns the engine
        Engine.Poll(); // materialise recorded notes
        Transport.Tick();
        PlayheadUpdated?.Invoke(Engine.PositionBeats);
        if (Engine.IsRecording) RecordingTick?.Invoke();
        ReportDropouts();

        _discordTickCounter++;
        if (_discordTickCounter >= 30)
        {
            _discordTickCounter = 0;
            _discordRpc?.UpdatePresence(_projectName, Engine.TrackCount, (double)Transport.Bpm, Engine.IsPlaying, Engine.IsRecording);
        }
    }

    private void OnAutosaveTick()
    {
        if (!SuspendEnginePolling && Engine.TrackCount > 0) AutosaveRequested?.Invoke();
    }

    // Surface any new audio dropouts (M7-8): log to stderr + a non-fatal status
    // message. Playback is never interrupted — the counter is passive.
    private void ReportDropouts()
    {
        int count = Engine.XrunCount;
        if (count <= _lastXrunCount) return;
        _lastXrunCount = count;
        Console.Error.WriteLine($"[xrun] audio dropout #{count} at {DateTime.Now:o}");
        _log.Warn($"Audio dropout #{count}");
        StatusText = $"Audio dropout ({count})";
    }

    /// <summary>
    /// Applies staged audio-device settings (M7-1): stops the playhead clock
    /// touching the engine, restarts the backend on the new device/rate/buffer,
    /// then resumes. Returns the negotiated status text; sets StatusText too.
    /// </summary>
    public string ApplyAudioSettings()
    {
        // Don't restart the backend mid-playback.
        if (Engine.IsPlaying) Transport.PlayStopCommand.Execute(null);

        SuspendEnginePolling = true;
        try
        {
            Engine.ApplyAudio();
        }
        catch (KnoxEngineException ex)
        {
            _log.Error("Audio device apply failed", ex);
            StatusText = $"Audio device error: {ex.Message}";
            return StatusText;
        }
        finally
        {
            SuspendEnginePolling = false;
        }

        EngineInfo = $"v{_build.Version} · {Engine.SampleRate:0} Hz";
        int buf = Engine.NegotiatedBufferFrames;
        _log.Info($"Audio applied · {Engine.NegotiatedSampleRate:0} Hz · {buf} frames");
        bool fallback = Engine.AudioExclusiveFallback;
        if (fallback)
        {
            _log.Warn("WASAPI exclusive mode refused — fell back to shared");
            StatusText = "WASAPI exclusive unavailable — using shared mode";
            return StatusText;
        }
        StatusText = buf > 0
            ? $"Audio: {Engine.NegotiatedSampleRate:0} Hz · {buf} frames"
            : $"Audio: {Engine.NegotiatedSampleRate:0} Hz";
        return StatusText;
    }

    /// <summary>
    /// Applies staged MIDI-input settings (M7-2). Lighter than audio: only the
    /// CoreMIDI port is reconnected, the audio backend keeps running, so no
    /// polling suspension is needed.
    /// </summary>
    public void ApplyMidiSettings()
    {
        try
        {
            Engine.ApplyMidi();
            ApplyMidiVelocitySettings();
            _log.Info("MIDI inputs updated");
            StatusText = "MIDI inputs updated.";
        }
        catch (KnoxEngineException ex)
        {
            _log.Error("MIDI apply failed", ex);
            StatusText = $"MIDI error: {ex.Message}";
        }
    }

    /// <summary>
    /// Pushes key velocity sensitivity, fixed velocity value, and response curve to the native audio engine.
    /// </summary>
    public void ApplyMidiVelocitySettings()
    {
        try
        {
            var s = Settings.Current;
            bool sens = s.MidiVelocitySensitivity;
            float fixedVel = Math.Clamp(s.MidiFixedVelocityValue / 127.0f, 0.01f, 1.0f);
            int curve = s.MidiVelocityCurve switch
            {
                "Soft" => 1,
                "Hard" => 2,
                "Compressed" => 3,
                "Fixed" => 4,
                _ => 0 // Linear
            };
            Engine.SetMidiVelocitySettings(sens, fixedVel, curve);
        }
        catch (Exception ex)
        {
            _log.Error("MIDI velocity settings apply failed", ex);
        }
    }

    public void Dispose()
    {
        _clock.Stop();
        _autosave.Stop();
        Engine.Dispose();
    }
}

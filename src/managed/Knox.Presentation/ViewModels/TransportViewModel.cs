// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// M4.1-A: transport as the first MVVM slice. Wraps the engine's transport +
// master behind observable properties and commands; the clock calls Tick().

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Knox.Application;

namespace Knox.Presentation;

public partial class TransportViewModel(IAudioEngine engine) : ObservableObject
{
    private readonly IAudioEngine _engine = engine;

    private const int BeatsPerBar = 4;

    [ObservableProperty] private string _positionText = "1.1.00";
    [ObservableProperty] private string _playLabel = "▶ Play";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private decimal _bpm = 120; // decimal for NumericUpDown
    [ObservableProperty] private int _timeSigNumerator = 4;
    [ObservableProperty] private int _timeSigDenominator = 4;
    [ObservableProperty] private double _masterVolume = 1.0;
    [ObservableProperty] private bool _metronomeOn;
    [ObservableProperty] private bool _loopOn;
    [ObservableProperty] private string _loopRangeText = "1.1 – 5.1";
    [ObservableProperty] private bool _recordOn;
    [ObservableProperty] private bool _countInOn = true;
    [ObservableProperty] private bool _countInOnRecord = true;
    [ObservableProperty] private bool _countInOnPlayback = false;
    [ObservableProperty] private int _countInBars = 1;
    [ObservableProperty] private bool _isCountingIn;
    [ObservableProperty] private int _countInCurrentBeat;
    [ObservableProperty] private string _countInText = "";

    private CancellationTokenSource? _countInCts;
    private bool _revertingRec;

    /// <summary>Status text for the record button (armed? / input failed? / recording).</summary>
    public event Action<string>? Message;

    /// <summary>Supplies the track to auto-arm when Record is pressed with nothing
    /// armed (0 = none). Lets "hit Record" work without a separate arm click.</summary>
    public Func<int>? RecordArmTarget { get; set; }

    /// <summary>Fired after auto-arming a track so the track headers can refresh.</summary>
    public event Action? TracksChanged;

    private bool _syncingLoop;

    partial void OnBpmChanged(decimal value) => _engine.SetBpm((double)value);
    partial void OnTimeSigNumeratorChanged(int value) => _engine.SetTimeSignature(value, TimeSigDenominator);
    partial void OnTimeSigDenominatorChanged(int value) => _engine.SetTimeSignature(TimeSigNumerator, value);
    partial void OnMasterVolumeChanged(double value) => _engine.SetMasterVolume((float)value);
    partial void OnMetronomeOnChanged(bool value) => _engine.SetMetronome(value);

    public void CancelCountIn()
    {
        if (_countInCts != null)
        {
            try { _countInCts.Cancel(); _countInCts.Dispose(); } catch { }
            _countInCts = null;
        }
        IsCountingIn = false;
        CountInCurrentBeat = 0;
        CountInText = "";
    }

    public void StartCountIn(Action onComplete, bool isRecord)
    {
        CancelCountIn();
        _countInCts = new CancellationTokenSource();
        var token = _countInCts.Token;
        IsCountingIn = true;

        Task.Run(async () =>
        {
            try
            {
                double bpm = (double)Bpm > 0 ? (double)Bpm : 120.0;
                int beatsPerBar = TimeSigNumerator > 0 ? TimeSigNumerator : 4;
                int totalBars = Math.Max(1, CountInBars);
                int totalBeats = totalBars * beatsPerBar;
                int intervalMs = (int)Math.Round(60000.0 / bpm);

                for (int b = 1; b <= totalBeats; b++)
                {
                    if (token.IsCancellationRequested) break;
                    int beatInBar = ((b - 1) % beatsPerBar) + 1;
                    bool downbeat = (beatInBar == 1);
                    _engine.PlayMetronomeClick(downbeat);

                    CountInCurrentBeat = beatInBar;
                    CountInText = $"{beatInBar}";
                    string msg = $"Count-in: {beatInBar} / {beatsPerBar}";
                    Message?.Invoke(msg);

                    await Task.Delay(intervalMs, token);
                }

                if (!token.IsCancellationRequested)
                {
                    IsCountingIn = false;
                    CountInCurrentBeat = 0;
                    CountInText = "";
                    onComplete();
                }
            }
            catch (OperationCanceledException)
            {
                // cancelled
            }
            catch (Exception ex)
            {
                IsCountingIn = false;
                CountInCurrentBeat = 0;
                CountInText = "";
                Message?.Invoke($"Count-in error: {ex.Message}");
            }
        }, token);
    }

    partial void OnLoopOnChanged(bool value)
    {
        if (_syncingLoop) return;   // reflecting engine state, not a user toggle
        // Toggle over the current region (set from the arrangement ruler / "Loop
        // selection"); fall back to the first four bars if none has been set.
        double s = _engine.LoopStart, e = _engine.LoopEnd;
        if (e <= s) { s = 0; e = 4 * BeatsPerBar; }
        _engine.SetLoop(value, s, e);
        LoopRangeText = FormatRange(s, e);
    }

    /// <summary>Reflect the engine's loop region in the transport bar (called after
    /// the arrangement authors a region via the ruler or "Loop selection").</summary>
    public void SyncLoop()
    {
        _syncingLoop = true;
        LoopOn = _engine.LoopEnabled;
        _syncingLoop = false;
        LoopRangeText = FormatRange(_engine.LoopStart, _engine.LoopEnd);
    }

    private static string FormatRange(double startBeat, double endBeat) => $"{BarBeat(startBeat)} – {BarBeat(endBeat)}";
    private static string BarBeat(double beat)
    {
        int bar = (int)(beat / BeatsPerBar) + 1;
        int b = (int)(beat % BeatsPerBar) + 1;
        return $"{bar}.{b}";
    }

    partial void OnRecordOnChanged(bool value)
    {
        if (_revertingRec) return;               // ignore the re-entry from a revert below
        if (!value)
        {
            CancelCountIn();
            _engine.SetRecording(false);
            TracksChanged?.Invoke();
            Message?.Invoke("");
            return;
        }

        if (CountInOn && CountInOnRecord && !_engine.IsPlaying && !_engine.IsRecording)
        {
            StartCountIn(() => ExecuteStartRecording(), isRecord: true);
            return;
        }

        ExecuteStartRecording();
    }

    private void ExecuteStartRecording()
    {
        _engine.SetRecording(true);
        if (_engine.IsRecording) { Message?.Invoke("● Recording…"); return; }

        // Failed only because nothing is armed? Auto-arm the record target and
        // retry, so pressing Record just works without a separate arm click.
        if (_engine.RecordStartStatus == 1 && RecordArmTarget?.Invoke() is int tid && tid > 0)
        {
            _engine.SetTrackArmed(tid, true);
            TracksChanged?.Invoke();
            _engine.SetRecording(true);
            if (_engine.IsRecording) { Message?.Invoke("● Recording…"); return; }
        }

        // Couldn't start — pop the button back up and explain why.
        _revertingRec = true; RecordOn = false; _revertingRec = false;
        Message?.Invoke(_engine.RecordStartStatus switch
        {
            1 => "Can't record: arm a track first (click Arm on a track header).",
            2 => "Can't record: no audio input — grant microphone access (needs a bundled app).",
            _ => "Recording didn't start.",
        });
    }

    [RelayCommand]
    private void PlayStop()
    {
        if (IsCountingIn)
        {
            CancelCountIn();
            StopAll();
            return;
        }
        if (_engine.IsPlaying || RecordOn) StopAll();
        else TriggerPlayWithCountInIfNeeded();
    }

    [RelayCommand]
    private void Play()
    {
        if (IsCountingIn)
        {
            CancelCountIn();
            return;
        }
        TriggerPlayWithCountInIfNeeded();
    }

    private void TriggerPlayWithCountInIfNeeded()
    {
        if (CountInOn && CountInOnPlayback && !_engine.IsPlaying)
        {
            StartCountIn(() => _engine.Play(), isRecord: false);
        }
        else
        {
            _engine.Play();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        CancelCountIn();
        if (!_engine.IsPlaying && !RecordOn) { _engine.Seek(0); return; }
        StopAll();
    }

    private void StopAll()
    {
        CancelCountIn();
        if (RecordOn) RecordOn = false;   // -> OnRecordOnChanged(false) -> SetRecording(false)
        _engine.StopTransport();
    }

    /// <summary>When true the position readout shows elapsed time (m:ss.mmm) instead of bars.beats.
    /// Toggled by clicking the readout.</summary>
    [ObservableProperty] private bool _showTimeDisplay;

    private double _lastBeats;

    /// <summary>Flip the position readout between bars.beats and minutes:seconds.</summary>
    public void ToggleTimeDisplay()
    {
        ShowTimeDisplay = !ShowTimeDisplay;
        UpdatePositionText(_lastBeats);   // reflect immediately, don't wait for the next tick
    }

    /// <summary>Pulled by the playhead clock (~30 Hz) to refresh the readout.</summary>
    public void Tick()
    {
        double beats = _engine.PositionBeats;
        _lastBeats = beats;
        UpdatePositionText(beats);
        IsPlaying = _engine.IsPlaying;
        PlayLabel = IsPlaying ? "■ Stop" : "▶ Play";
    }

    private void UpdatePositionText(double beats)
    {
        if (ShowTimeDisplay)
        {
            double sec = Bpm > 0 ? beats * 60.0 / (double)Bpm : 0.0;
            int m = (int)(sec / 60.0);
            double s = sec - m * 60.0;
            // Invariant so the readout keeps a "." separator regardless of locale.
            PositionText = m + ":" + s.ToString("00.000", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            int bar = (int)(beats / BeatsPerBar) + 1;
            int beatInBar = (int)(beats % BeatsPerBar) + 1;
            int ticks = (int)((beats - Math.Floor(beats)) * 100);
            PositionText = $"{bar}.{beatInBar}.{ticks:00}";
        }
    }
}

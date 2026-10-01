// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The MIDI-learn brain: owns the mapping table + the learn state machine, and on
// every UI tick drains native CC/note events and applies them. When armed and a
// control is selected (pending), the next incoming event binds it; otherwise each
// event drives every mapping whose source matches. Apply goes straight to the
// engine via the same setters the on-screen controls use, so mapped controls work
// even when their card is closed. Buttons (mute/solo/transport) trigger/toggle;
// everything else is an absolute value scaled through the mapping's range.

using System;
using System.Collections.Generic;
using System.Linq;
using Knox.Application;

namespace Knox.App;

public sealed class MidiLearnService
{
    private const float VolumeMax = 1.5f;   // track/master fader travel (matches VFader.Max)
    private const float RackGainMax = 2.0f; // rack out / chain gain travel (matches the rack faders)

    private readonly IAudioEngine _engine;
    private readonly ILogSink? _log;
    private readonly List<MidiMapping> _mappings = new();
    private readonly int[] _buf = new int[256 * 4];   // reused each tick — up to 256 events
    // Distinct control sources seen recently, keyed by (isNote, channel, number) → last value
    // + hit count. Lets a headless client (MCP) discover what a connected controller emits.
    private readonly Dictionary<(bool isNote, int channel, int number), (int value, int count)> _seen = new();

    private bool _armed;
    private MidiBinding? _pending;
    // Also echo MIDI to the console for live watching under `dotnet run` (opt-in, to
    // avoid flooding the terminal with a knob's CC stream during normal use).
    private readonly bool _echo = Environment.GetEnvironmentVariable("NOTA_MIDI_LOG") is not null;

    public MidiLearnService(IAudioEngine engine, ILogSink? log = null) { _engine = engine; _log = log; }

    private void LogMidi(string msg) { _log?.Info(msg); if (_echo) Console.WriteLine(msg); }

    public event Action? ArmedChanged;
    public event Action? PendingChanged;
    public event Action? MappingsChanged;
    public event Action<MidiTargetKind>? CustomActionTriggered;

    public static readonly (MidiTargetKind Kind, string NameKey, string DefaultName, string Category)[] StandardActions = new[]
    {
        (MidiTargetKind.TransportTogglePlay, "MidiAction.TogglePlay", "Play / Pause (Toggle)", "Transport"),
        (MidiTargetKind.TransportPlay, "MidiAction.Play", "Play", "Transport"),
        (MidiTargetKind.TransportStop, "MidiAction.Stop", "Stop", "Transport"),
        (MidiTargetKind.TransportRecord, "MidiAction.Record", "Record (Toggle)", "Transport"),
        (MidiTargetKind.TransportLoop, "MidiAction.Loop", "Loop / Cycle (Toggle)", "Transport"),
        (MidiTargetKind.TransportMetronome, "MidiAction.Metronome", "Metronome (Toggle)", "Transport"),
        (MidiTargetKind.TransportRewind, "MidiAction.Rewind", "Rewind to Start (Home)", "Transport"),
        (MidiTargetKind.TransportFastForward, "MidiAction.FastForward", "Fast Forward (+4 bars)", "Transport"),
        (MidiTargetKind.TransportTapTempo, "MidiAction.TapTempo", "Tap Tempo", "Transport"),
        (MidiTargetKind.TransportOverdub, "MidiAction.Overdub", "MIDI Overdub", "Transport"),

        (MidiTargetKind.ActionUndo, "MidiAction.Undo", "Undo (Cmd/Ctrl+Z)", "Edit & History"),
        (MidiTargetKind.ActionRedo, "MidiAction.Redo", "Redo (Cmd/Ctrl+Shift+Z)", "Edit & History"),
        (MidiTargetKind.ActionDuplicate, "MidiAction.Duplicate", "Duplicate Clip / Track (Cmd+D)", "Edit & History"),
        (MidiTargetKind.ActionSplit, "MidiAction.Split", "Split at Playhead (Cmd+E)", "Edit & History"),
        (MidiTargetKind.ActionQuantize, "MidiAction.Quantize", "Quantize Notes (Cmd+U)", "Edit & History"),

        (MidiTargetKind.ActionPrevTrack, "MidiAction.PrevTrack", "Select Previous Track (Up)", "Track Navigation"),
        (MidiTargetKind.ActionNextTrack, "MidiAction.NextTrack", "Select Next Track (Down)", "Track Navigation"),
        (MidiTargetKind.ActionArmTrack, "MidiAction.ArmTrack", "Arm Selected Track", "Track Navigation"),
        (MidiTargetKind.ActionMuteTrack, "MidiAction.MuteTrack", "Mute Selected Track", "Track Navigation"),
        (MidiTargetKind.ActionSoloTrack, "MidiAction.SoloTrack", "Solo Selected Track", "Track Navigation"),
        (MidiTargetKind.MasterVolume, "MidiAction.MasterVolume", "Master Volume (Fader/Knob)", "Mixer"),
    };

    private string? _pendingDeviceUid;
    private string? _pendingDeviceName;

    public string? ActiveDeviceUid { get; set; }
    public string? ActiveDeviceName { get; set; }

    public void BindAction(MidiTargetKind kind, string name, MidiSourceKind sourceKind, int channel, int number, string? deviceUid = null, string? deviceName = null)
    {
        var target = new MidiTarget(kind, -1, -1, -1);
        _mappings.RemoveAll(m => m.Target.Equals(target) && (string.IsNullOrEmpty(deviceUid) ? string.IsNullOrEmpty(m.DeviceUid) : m.DeviceUid == deviceUid));
        _mappings.Add(new MidiMapping
        {
            Target = target,
            DisplayName = name,
            SourceKind = sourceKind,
            Channel = channel,
            Number = number,
            DeviceUid = deviceUid,
            DeviceName = deviceName,
        });
        ClearPending();
        MappingsChanged?.Invoke();
    }

    public void RemoveActionMapping(MidiTargetKind kind, string? deviceUid = null)
    {
        var target = new MidiTarget(kind, -1, -1, -1);
        if (_mappings.RemoveAll(m => m.Target.Equals(target) && (string.IsNullOrEmpty(deviceUid) || m.DeviceUid == deviceUid)) > 0)
            MappingsChanged?.Invoke();
    }

    public MidiMapping? GetActionMapping(MidiTargetKind kind, string? deviceUid = null)
    {
        var target = new MidiTarget(kind, -1, -1, -1);
        if (!string.IsNullOrEmpty(deviceUid) && deviceUid != "all")
        {
            var match = _mappings.FirstOrDefault(m => m.Target.Equals(target) && m.DeviceUid == deviceUid);
            if (match != null) return match;
        }
        return _mappings.FirstOrDefault(m => m.Target.Equals(target) && string.IsNullOrEmpty(m.DeviceUid));
    }

    public void LoadPreset(string presetName, string? deviceUid = null, string? deviceName = null)
    {
        switch (presetName)
        {
            case "MMC Standard":
                // Standard MMC / CC transport
                BindAction(MidiTargetKind.TransportStop, "Stop", MidiSourceKind.Cc, 0, 114, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportPlay, "Play", MidiSourceKind.Cc, 0, 115, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportRecord, "Record", MidiSourceKind.Cc, 0, 116, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportRewind, "Rewind", MidiSourceKind.Cc, 0, 117, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportFastForward, "Fast Forward", MidiSourceKind.Cc, 0, 118, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportLoop, "Loop", MidiSourceKind.Cc, 0, 119, deviceUid, deviceName);
                break;
            case "Mackie Control":
                BindAction(MidiTargetKind.TransportRewind, "Rewind", MidiSourceKind.Note, 0, 91, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportFastForward, "Fast Forward", MidiSourceKind.Note, 0, 92, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportStop, "Stop", MidiSourceKind.Note, 0, 93, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportPlay, "Play", MidiSourceKind.Note, 0, 94, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportRecord, "Record", MidiSourceKind.Note, 0, 95, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportLoop, "Loop", MidiSourceKind.Note, 0, 86, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportMetronome, "Metronome", MidiSourceKind.Note, 0, 89, deviceUid, deviceName);
                BindAction(MidiTargetKind.ActionUndo, "Undo", MidiSourceKind.Note, 0, 76, deviceUid, deviceName);
                BindAction(MidiTargetKind.ActionRedo, "Redo", MidiSourceKind.Note, 0, 79, deviceUid, deviceName);
                break;
            case "Akai MPK Mini":
                BindAction(MidiTargetKind.TransportStop, "Stop", MidiSourceKind.Cc, 0, 114, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportPlay, "Play", MidiSourceKind.Cc, 0, 115, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportRecord, "Record", MidiSourceKind.Cc, 0, 116, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportLoop, "Loop", MidiSourceKind.Cc, 0, 117, deviceUid, deviceName);
                break;
            case "Novation Launchkey":
                BindAction(MidiTargetKind.TransportRewind, "Rewind", MidiSourceKind.Cc, 15, 115, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportPlay, "Play", MidiSourceKind.Cc, 15, 116, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportStop, "Stop", MidiSourceKind.Cc, 15, 117, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportRecord, "Record", MidiSourceKind.Cc, 15, 118, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportLoop, "Loop", MidiSourceKind.Cc, 15, 119, deviceUid, deviceName);
                BindAction(MidiTargetKind.ActionUndo, "Undo", MidiSourceKind.Cc, 15, 120, deviceUid, deviceName);
                break;
            case "Arturia KeyLab":
                BindAction(MidiTargetKind.TransportRewind, "Rewind", MidiSourceKind.Cc, 0, 91, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportFastForward, "Fast Forward", MidiSourceKind.Cc, 0, 92, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportStop, "Stop", MidiSourceKind.Cc, 0, 93, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportPlay, "Play", MidiSourceKind.Cc, 0, 94, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportRecord, "Record", MidiSourceKind.Cc, 0, 95, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportLoop, "Loop", MidiSourceKind.Cc, 0, 86, deviceUid, deviceName);
                BindAction(MidiTargetKind.TransportMetronome, "Metronome", MidiSourceKind.Cc, 0, 89, deviceUid, deviceName);
                break;
        }
    }

    /// <summary>Learn mode: controls highlight and a click selects a target to bind.</summary>
    public bool Armed
    {
        get => _armed;
        set
        {
            if (_armed == value) return;
            _armed = value;
            if (!_armed) ClearPending();
            ArmedChanged?.Invoke();
        }
    }

    /// <summary>The control awaiting a MIDI message (null = none selected yet).</summary>
    public MidiBinding? Pending => _pending;
    public string? PendingDeviceUid => _pendingDeviceUid;
    public string? PendingDeviceName => _pendingDeviceName;

    public void SelectForLearn(MidiBinding binding, string? deviceUid = null, string? deviceName = null)
    {
        _pending = binding;
        _pendingDeviceUid = deviceUid;
        _pendingDeviceName = deviceName;
        PendingChanged?.Invoke();
    }

    public void ClearPending()
    {
        if (_pending is null && _pendingDeviceUid is null && _pendingDeviceName is null) return;
        _pending = null;
        _pendingDeviceUid = null;
        _pendingDeviceName = null;
        PendingChanged?.Invoke();
    }

    public IReadOnlyList<MidiMapping> Mappings => _mappings;

    /// <summary>The mapping bound to <paramref name="target"/>, if any (for the "mapped" badge).</summary>
    public MidiMapping? MappingFor(MidiTarget target, string? deviceUid = null)
    {
        if (!string.IsNullOrEmpty(deviceUid) && deviceUid != "all")
        {
            var match = _mappings.FirstOrDefault(m => m.Target.Equals(target) && m.DeviceUid == deviceUid);
            if (match != null) return match;
        }
        return _mappings.FirstOrDefault(m => m.Target.Equals(target) && (string.IsNullOrEmpty(m.DeviceUid) || m.DeviceUid == deviceUid));
    }

    public void ClearDevice(string? deviceUid)
    {
        if (string.IsNullOrEmpty(deviceUid) || deviceUid == "all")
        {
            Clear();
            return;
        }
        if (_mappings.RemoveAll(m => m.DeviceUid == deviceUid) > 0)
            MappingsChanged?.Invoke();
    }

    public void RemoveMapping(MidiMapping m)
    {
        if (_mappings.Remove(m)) MappingsChanged?.Invoke();
    }

    public void Clear()
    {
        if (_mappings.Count == 0) return;
        _mappings.Clear();
        MappingsChanged?.Invoke();
    }

    /// <summary>Remove the mapping at <paramref name="index"/> (into <see cref="Mappings"/>).</summary>
    public bool RemoveMappingAt(int index)
    {
        if (index < 0 || index >= _mappings.Count) return false;
        _mappings.RemoveAt(index);
        MappingsChanged?.Invoke();
        return true;
    }

    /// <summary>Edit a mapping's output window / inversion by index.</summary>
    public bool SetMappingRange(int index, double min, double max, bool invert)
    {
        if (index < 0 || index >= _mappings.Count) return false;
        var m = _mappings[index];
        m.RangeMin = min; m.RangeMax = max; m.Invert = invert;
        MappingsChanged?.Invoke();
        return true;
    }

    // ---- recent controller activity (for headless discovery) --------------

    private void RecordSeen(bool isNote, int channel, int number, int value)
    {
        var key = (isNote, channel, number);
        int count = _seen.TryGetValue(key, out var e) ? e.count + 1 : 1;
        _seen[key] = (value, count);
        if (_seen.Count > 256) _seen.Clear();   // safety cap; distinct controls rarely near this
    }

    /// <summary>Distinct MIDI controls seen since the last clear: (isNote, channel, number,
    /// last value, hit count). Lets a client see what a connected controller is sending.</summary>
    public IReadOnlyList<(bool isNote, int channel, int number, int lastValue, int count)> RecentControls()
    {
        var list = new List<(bool, int, int, int, int)>(_seen.Count);
        foreach (var kv in _seen) list.Add((kv.Key.isNote, kv.Key.channel, kv.Key.number, kv.Value.value, kv.Value.count));
        return list;
    }

    /// <summary>Forget the recorded controller activity (start a fresh discovery window).</summary>
    public void ClearRecentControls() => _seen.Clear();

    // ---- per-tick drain + apply -------------------------------------------

    public void Tick()
    {
        int n = _engine.PollMidiControlEvents(_buf);
        for (int i = 0; i < n; i++)
        {
            var kind = _buf[i * 4] == 0 ? MidiSourceKind.Cc : MidiSourceKind.Note;
            int channel = _buf[i * 4 + 1];
            int number = _buf[i * 4 + 2];
            int value = _buf[i * 4 + 3];
            string src = $"{(kind == MidiSourceKind.Cc ? "CC" : "Note")} {number} ch{channel + 1} v{value}";
            RecordSeen(kind == MidiSourceKind.Note, channel, number, value);

            if (_pending is { } p)
            {
                Bind(p, kind, channel, number);
                LogMidi($"MIDI learn: {src} → bound to '{p.Name}'");
                continue;
            }
            int matched = Dispatch(kind, channel, number, value);
            LogMidi(matched > 0 ? $"MIDI in: {src} → {matched} mapping(s)" : $"MIDI in: {src} (unmapped)");
        }
    }

    private void Bind(MidiBinding binding, MidiSourceKind kind, int channel, int number)
    {
        // Rebinding a control replaces its old mapping; a source may still drive
        // several targets (fan-out), so we only dedupe on the target.
        _mappings.RemoveAll(m => m.Target.Equals(binding.Target) && (string.IsNullOrEmpty(_pendingDeviceUid) ? string.IsNullOrEmpty(m.DeviceUid) : m.DeviceUid == _pendingDeviceUid));
        _mappings.Add(new MidiMapping
        {
            Target = binding.Target,
            DisplayName = binding.Name,
            SourceKind = kind,
            Channel = channel,
            Number = number,
            DeviceUid = _pendingDeviceUid,
            DeviceName = _pendingDeviceName,
        });
        ClearPending();
        MappingsChanged?.Invoke();
    }

    private int Dispatch(MidiSourceKind kind, int channel, int number, int value)
    {
        int matched = 0;
        foreach (var m in _mappings)
            if (m.SourceKind == kind && m.Channel == channel && m.Number == number)
            {
                Apply(m, value);
                matched++;
            }
        return matched;
    }

    private void Apply(MidiMapping m, int value127)
    {
        var t = m.Target;
        if (t.IsButton)
        {
            // Trigger on a note-on or a CC crossing the half-way point.
            bool trigger = m.SourceKind == MidiSourceKind.Note ? value127 > 0 : value127 >= 64;
            if (!m.Invert ? trigger : !trigger) Trigger(t);
            return;
        }

        double norm = Math.Clamp(value127 / 127.0, 0, 1);
        if (m.Invert) norm = 1.0 - norm;
        double outv = m.RangeMin + norm * (m.RangeMax - m.RangeMin);   // 0..1 within the mapped window

        switch (t.Kind)
        {
            case MidiTargetKind.DeviceParam:
            {
                float min = _engine.DeviceParamMin(t.TrackId, t.DeviceIndex, t.ParamIndex);
                float max = _engine.DeviceParamMax(t.TrackId, t.DeviceIndex, t.ParamIndex);
                _engine.DeviceSetParam(t.TrackId, t.DeviceIndex, t.ParamIndex, (float)(min + outv * (max - min)));
                break;
            }
            case MidiTargetKind.PluginParam:
                _engine.PluginParamSet(t.TrackId, t.DeviceIndex, t.ParamIndex, (float)outv);
                break;
            case MidiTargetKind.MidiDeviceParam:
            {
                float min = _engine.MidiEffectParamMin(t.TrackId, t.DeviceIndex, t.ParamIndex);
                float max = _engine.MidiEffectParamMax(t.TrackId, t.DeviceIndex, t.ParamIndex);
                _engine.MidiEffectSetParam(t.TrackId, t.DeviceIndex, t.ParamIndex, (float)(min + outv * (max - min)));
                break;
            }
            case MidiTargetKind.TrackVolume: _engine.SetTrackVolume(t.TrackId, (float)(outv * VolumeMax)); break;
            case MidiTargetKind.MasterVolume: _engine.SetMasterVolume((float)(outv * VolumeMax)); break;
            case MidiTargetKind.TrackPan: _engine.SetTrackPan(t.TrackId, (float)(outv * 2.0 - 1.0)); break;
            case MidiTargetKind.RackVolume:
                if (t.DeviceIndex < 0) _engine.RackSetVolume(t.TrackId, (float)(outv * RackGainMax));
                else _engine.RackDevSetVolume(t.TrackId, t.DeviceIndex, (float)(outv * RackGainMax));
                break;
            case MidiTargetKind.RackChainGain:
                if (t.DeviceIndex < 0) _engine.RackSetChainGain(t.TrackId, t.ParamIndex, (float)(outv * RackGainMax));
                else _engine.RackDevSetChainGain(t.TrackId, t.DeviceIndex, t.ParamIndex, (float)(outv * RackGainMax));
                break;
        }
    }

    private void Trigger(MidiTarget t)
    {
        switch (t.Kind)
        {
            case MidiTargetKind.TransportPlay: _engine.Play(); break;
            case MidiTargetKind.TransportStop: _engine.StopTransport(); break;
            case MidiTargetKind.TransportTogglePlay:
                if (_engine.IsPlaying) _engine.StopTransport();
                else _engine.Play();
                break;
            case MidiTargetKind.TransportRecord: _engine.SetRecording(!_engine.IsRecording); break;
            case MidiTargetKind.TransportLoop:
                _engine.SetLoop(!_engine.LoopEnabled, _engine.LoopStart, _engine.LoopEnd);
                CustomActionTriggered?.Invoke(t.Kind);
                break;
            case MidiTargetKind.TransportMetronome:
            case MidiTargetKind.TransportRewind:
            case MidiTargetKind.TransportFastForward:
            case MidiTargetKind.TransportTapTempo:
            case MidiTargetKind.TransportOverdub:
            case MidiTargetKind.ActionUndo:
            case MidiTargetKind.ActionRedo:
            case MidiTargetKind.ActionPrevTrack:
            case MidiTargetKind.ActionNextTrack:
            case MidiTargetKind.ActionArmTrack:
            case MidiTargetKind.ActionSoloTrack:
            case MidiTargetKind.ActionMuteTrack:
            case MidiTargetKind.ActionDuplicate:
            case MidiTargetKind.ActionSplit:
            case MidiTargetKind.ActionQuantize:
                CustomActionTriggered?.Invoke(t.Kind);
                break;
            case MidiTargetKind.TrackMute: _engine.SetTrackMute(t.TrackId, !TrackFlag(t.TrackId, mute: true)); break;
            case MidiTargetKind.TrackSolo: _engine.SetTrackSolo(t.TrackId, !TrackFlag(t.TrackId, mute: false)); break;
            case MidiTargetKind.RackChainMute:
                if (t.DeviceIndex < 0) _engine.RackSetChainMute(t.TrackId, t.ParamIndex, !_engine.RackChainMute(t.TrackId, t.ParamIndex));
                else _engine.RackDevSetChainMute(t.TrackId, t.DeviceIndex, t.ParamIndex, !_engine.RackDevChainMute(t.TrackId, t.DeviceIndex, t.ParamIndex));
                break;
            case MidiTargetKind.RackChainSolo:
                if (t.DeviceIndex < 0) _engine.RackSetChainSolo(t.TrackId, t.ParamIndex, !_engine.RackChainSolo(t.TrackId, t.ParamIndex));
                else _engine.RackDevSetChainSolo(t.TrackId, t.DeviceIndex, t.ParamIndex, !_engine.RackDevChainSolo(t.TrackId, t.DeviceIndex, t.ParamIndex));
                break;
        }
    }

    private bool TrackFlag(int trackId, bool mute)
    {
        int n = _engine.TrackCount;
        for (int i = 0; i < n; i++)
            if (_engine.TryGetTrackInfo(i, out var ti) && ti.Id == trackId)
                return (mute ? ti.Muted : ti.Soloed) != 0;
        return false;
    }

    // ---- persistence seam (see project DTO mapping) -----------------------

    // ---- project persistence (sidecar in the .nota bundle) ----------------
    //
    // Mappings travel with the project as midimap.json. Runtime TrackIds aren't
    // stable across sessions, so we store each track-scoped mapping by the track's
    // position (its TryGetTrackInfo index) and re-resolve on load; globals use -1.

    private const string SidecarName = "midimap.json";

    private sealed class Dto
    {
        public int TrackIndex { get; set; } = -1;
        public int Kind { get; set; }
        public int DeviceIndex { get; set; } = -1;
        public int ParamIndex { get; set; } = -1;
        public int SourceKind { get; set; }
        public int Channel { get; set; }
        public int Number { get; set; }
        public double RangeMin { get; set; }
        public double RangeMax { get; set; } = 1;
        public bool Invert { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public void SaveMappings(string bundleDir)
    {
        var list = new List<Dto>(_mappings.Count);
        foreach (var m in _mappings)
        {
            var t = m.Target;
            list.Add(new Dto
            {
                TrackIndex = IndexOfTrackId(t.TrackId),
                Kind = (int)t.Kind,
                DeviceIndex = t.DeviceIndex,
                ParamIndex = t.ParamIndex,
                SourceKind = (int)m.SourceKind,
                Channel = m.Channel,
                Number = m.Number,
                RangeMin = m.RangeMin,
                RangeMax = m.RangeMax,
                Invert = m.Invert,
                DisplayName = m.DisplayName,
            });
        }
        var json = System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(System.IO.Path.Combine(bundleDir, SidecarName), json);
    }

    public void LoadMappings(string bundleDir)
    {
        _mappings.Clear();
        var path = System.IO.Path.Combine(bundleDir, SidecarName);
        if (System.IO.File.Exists(path))
        {
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<Dto>>(System.IO.File.ReadAllText(path)) ?? new();
                foreach (var d in list)
                {
                    var kind = (MidiTargetKind)d.Kind;
                    int trackId = TrackScoped(kind) ? TrackIdAtIndex(d.TrackIndex) : -1;
                    if (TrackScoped(kind) && trackId < 0) continue;   // track since deleted
                    _mappings.Add(new MidiMapping
                    {
                        Target = new MidiTarget(kind, trackId, d.DeviceIndex, d.ParamIndex),
                        DisplayName = d.DisplayName,
                        SourceKind = (MidiSourceKind)d.SourceKind,
                        Channel = d.Channel,
                        Number = d.Number,
                        RangeMin = d.RangeMin,
                        RangeMax = d.RangeMax,
                        Invert = d.Invert,
                    });
                }
            }
            catch { /* a corrupt sidecar just means no mappings — never block the load */ }
        }
        MappingsChanged?.Invoke();
    }

    private static bool TrackScoped(MidiTargetKind k) => k
        is MidiTargetKind.DeviceParam or MidiTargetKind.PluginParam or MidiTargetKind.MidiDeviceParam
        or MidiTargetKind.TrackVolume or MidiTargetKind.TrackPan or MidiTargetKind.TrackMute or MidiTargetKind.TrackSolo
        or MidiTargetKind.RackVolume or MidiTargetKind.RackChainGain
        or MidiTargetKind.RackChainMute or MidiTargetKind.RackChainSolo;

    private int IndexOfTrackId(int trackId)
    {
        if (trackId < 0) return -1;
        int n = _engine.TrackCount;
        for (int i = 0; i < n; i++)
            if (_engine.TryGetTrackInfo(i, out var ti) && ti.Id == trackId) return i;
        return -1;
    }

    private int TrackIdAtIndex(int index)
        => index >= 0 && _engine.TryGetTrackInfo(index, out var ti) ? ti.Id : -1;
}

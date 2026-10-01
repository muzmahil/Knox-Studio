// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Knox.Application;
using Knox.Presentation;

namespace Knox.App;

public sealed partial class ArrangementView
{
    // --- parameter automation (M9-A3) --------------------------------------
    internal bool AutomationModeActive => _automationMode;

    private string AutoLabelFor(TrackVM t) => t.AutoTarget switch
    {
        AutomationTarget.Volume => "Vol",
        AutomationTarget.Pan => "Pan",
        AutomationTarget.PluginParam => _engine is { } e
            ? Short(e.PluginParamName(t.Id, t.AutoDeviceIndex, ResolvePluginParamIndex(t))) : "Param",
        AutomationTarget.MidiDeviceParam => _engine is { } e
            ? Short(e.MidiEffectParamName(t.Id, t.AutoDeviceIndex, t.AutoParamIndex)) : "Param",
        _ => _engine is { } e ? Short(e.DeviceParamName(t.Id, t.AutoDeviceIndex, t.AutoParamIndex)) : "Param",
    };

    private string AutoLabelFor(AutoSubLaneVM sl) => sl.Target switch
    {
        AutomationTarget.Volume => "Vol",
        AutomationTarget.Pan => "Pan",
        AutomationTarget.PluginParam => _engine is { } e && sl.DeviceIndex >= 0
            ? Short(PluginParamNameById(e, _tracks.FirstOrDefault(x => x.Id == sl.TrackId) ?? new TrackVM { Id = sl.TrackId }, sl.DeviceIndex, sl.ParamId))
            : "Plugin",
        AutomationTarget.MidiDeviceParam => _engine is { } e
            ? Short(e.MidiEffectParamName(sl.TrackId, sl.DeviceIndex, sl.ParamIndex)) : "Param",
        _ => _engine is { } e ? Short(e.DeviceParamName(sl.TrackId, sl.DeviceIndex, sl.ParamIndex)) : "Param",
    };
    private static string Short(string s) => s.Length <= 10 ? s : s.Substring(0, 10);

    /// <summary>Value range of a track's current automation target (plugin params are 0..1).</summary>
    internal (float min, float max) AutoRange(TrackVM t) => AutoRange(t.AutoTarget, t.AutoDeviceIndex, t.AutoParamIndex, t.AutoParamId, t.Id);

    internal (float min, float max) AutoRange(AutoSubLaneVM sl) => AutoRange(sl.Target, sl.DeviceIndex, sl.ParamIndex, sl.ParamId, sl.TrackId);

    internal (float min, float max) AutoRange(AutomationTarget target, int dev, int param, string paramId, int trackId) => target switch
    {
        AutomationTarget.Volume => (0f, 2f),
        AutomationTarget.Pan => (-1f, 1f),
        AutomationTarget.PluginParam => (0f, 1f),   // normalized
        AutomationTarget.MidiDeviceParam => _engine is { } e
            ? (e.MidiEffectParamMin(trackId, dev, param),
               e.MidiEffectParamMax(trackId, dev, param))
            : (0f, 1f),
        _ => _engine is { } e
            ? (e.DeviceParamMin(trackId, dev, param),
               e.DeviceParamMax(trackId, dev, param))
            : (0f, 1f),
    };

    /// <summary>Current live value of the target (baseline when the lane has no points).</summary>
    internal float AutoCurrent(TrackVM t) => AutoCurrent(t.AutoTarget, t.AutoDeviceIndex, t.AutoParamIndex, t.AutoParamId, t.Id);

    internal float AutoCurrent(AutoSubLaneVM sl) => AutoCurrent(sl.Target, sl.DeviceIndex, sl.ParamIndex, sl.ParamId, sl.TrackId);

    internal float AutoCurrent(AutomationTarget target, int dev, int param, string paramId, int trackId) => target switch
    {
        AutomationTarget.Volume => (_tracks.FirstOrDefault(x => x.Id == trackId) ?? _returns.FirstOrDefault(x => x.Id == trackId))?.Volume ?? 1.0f,
        AutomationTarget.Pan => (_tracks.FirstOrDefault(x => x.Id == trackId) ?? _returns.FirstOrDefault(x => x.Id == trackId))?.Pan ?? 0.0f,
        AutomationTarget.PluginParam => _engine is { } e ? e.PluginParamGet(trackId, dev, ResolvePluginParamIndex(trackId, dev, paramId)) : 0f,
        AutomationTarget.MidiDeviceParam => _engine is { } e ? e.MidiEffectGetParam(trackId, dev, param) : 0f,
        _ => _engine is { } e ? e.DeviceGetParam(trackId, dev, param) : 0f,
    };

    /// <summary>Format automation value for display (dB for volume, %/L/R for pan, st for pitch, etc.).</summary>
    internal string FormatAutoValue(TrackVM t, float v) => FormatAutoValue(t.AutoTarget, v, t.AutoLabel);

    internal string FormatAutoValue(AutoSubLaneVM sl, float v) => FormatAutoValue(sl.Target, v, sl.Label);

    internal static string FormatAutoValue(AutomationTarget target, float v, string label = "")
    {
        if (target == AutomationTarget.Volume)
            return v <= 0.0011f ? "-∞ dB" : $"{AudioMath.LinToDb(v):+0.0;-0.0;0.0} dB";
        if (target == AutomationTarget.Pan)
            return Math.Abs(v) < 0.01f ? "Center" : v < 0 ? $"{(int)Math.Round(Math.Abs(v) * 100)}% L" : $"{(int)Math.Round(v * 100)}% R";

        string lblLower = label.ToLowerInvariant();
        if (lblLower.Contains("shift") || lblLower.Contains("pitch") || lblLower.Contains("semi") || lblLower.Contains("tune") || lblLower.Contains("transp"))
        {
            float st = (v - 0.5f) * 24f;
            return Math.Abs(st) < 0.05f ? "0 st" : $"{st:+0.0;-0.0;0.0} st";
        }

        return $"{(int)Math.Round(Math.Clamp(v, 0f, 1f) * 100)}%";
    }

    /// <summary>Format beat as musical time (Bar.Beat.Subbeat).</summary>
    internal static string FormatBeat(double beat)
    {
        int bar = (int)(beat / 4.0) + 1;
        double rem = beat % 4.0;
        int b = (int)rem + 1;
        int sub = (int)((rem - (int)rem) * 4) + 1;
        return $"{bar}.{b}.{sub}";
    }

    /// <summary>Interpolate automation curve at any timeline beat.</summary>
    internal static float InterpolateAuto(IReadOnlyList<AutoPt> pts, double beat)
    {
        if (pts.Count == 0) return 0f;
        if (beat <= pts[0].Beat) return pts[0].Value;
        if (beat >= pts[^1].Beat) return pts[^1].Value;
        for (int i = 1; i < pts.Count; i++)
        {
            if (beat <= pts[i].Beat)
            {
                double b0 = pts[i - 1].Beat, b1 = pts[i].Beat;
                float v0 = pts[i - 1].Value, v1 = pts[i].Value;
                double span = b1 - b0;
                if (span <= 0) return v1;
                double t = (beat - b0) / span;
                return (float)(v0 + (v1 - v0) * Shape(t, pts[i - 1].Curve));
            }
        }
        return pts[^1].Value;
    }

    // Plugin params are addressed by stable id; resolve the current index on demand.
    private int ResolvePluginParamIndex(TrackVM t) => ResolvePluginParamIndex(t.Id, t.AutoDeviceIndex, t.AutoParamId);

    private int ResolvePluginParamIndex(int trackId, int dev, string paramId)
    {
        if (_engine is not { } e || string.IsNullOrEmpty(paramId)) return -1;
        int n = e.PluginParamCount(trackId, dev);
        for (int i = 0; i < n; i++)
            if (e.PluginParamId(trackId, dev, i) == paramId) return i;
        return -1;
    }

    private int FindAutoLane(TrackVM t)
    {
        if (_engine is not { } e) return -1;
        int n = e.AutomationLaneCount(t.Id);
        for (int i = 0; i < n; i++)
        {
            var info = e.AutomationLaneInfo(t.Id, i);
            if (info.Target != t.AutoTarget) continue;
            bool match = t.AutoTarget switch
            {
                AutomationTarget.DeviceParam or AutomationTarget.MidiDeviceParam =>
                    info.DeviceIndex == t.AutoDeviceIndex && info.ParamIndex == t.AutoParamIndex,
                AutomationTarget.PluginParam => info.DeviceIndex == t.AutoDeviceIndex
                                                && e.AutomationLaneParamId(t.Id, i) == t.AutoParamId,
                _ => true,
            };
            if (match) return i;
        }
        return -1;
    }

    private void LoadAutoPoints(TrackVM t)
    {
        t.AutoPoints.Clear();
        if (_engine is not { } e) return;
        int lane = FindAutoLane(t);
        if (lane < 0) return;
        foreach (var p in e.GetAutomationPoints(t.Id, lane))
            t.AutoPoints.Add(new AutoPt { Beat = p.Beat, Value = p.Value, Curve = p.Curve });
    }

    // Any control's gesture-begin (knob/fader/plugin control) — while in automation mode,
    // follow the touched param so its envelope is what the track's lane shows.
    private void OnAutomationTouched(int trackId, AutomationTarget target, int dev, int param, string paramId)
    {
        if (!_automationMode) return;
        var t = _tracks.FirstOrDefault(x => x.Id == trackId) ?? _returns.FirstOrDefault(x => x.Id == trackId);
        if (t is null) return;
        // Already showing this exact target? leave it be (don't reload/redraw mid-gesture).
        bool same = t.AutoTarget == target && target switch
        {
            AutomationTarget.DeviceParam or AutomationTarget.MidiDeviceParam => t.AutoDeviceIndex == dev && t.AutoParamIndex == param,
            AutomationTarget.PluginParam => t.AutoDeviceIndex == dev && t.AutoParamId == (paramId ?? ""),
            _ => true,
        };
        if (same) return;
        if (target == AutomationTarget.PluginParam) SetAutoPluginTarget(t, dev, paramId ?? "");
        else SetAutoTarget(t, target, dev, param);
    }

    internal void SetAutoTarget(TrackVM t, AutomationTarget target, int dev, int param)
    {
        t.AutoTarget = target;
        t.AutoDeviceIndex = target is AutomationTarget.DeviceParam or AutomationTarget.PluginParam or AutomationTarget.MidiDeviceParam ? dev : -1;
        t.AutoParamIndex = target is AutomationTarget.DeviceParam or AutomationTarget.MidiDeviceParam ? param : -1;
        t.AutoParamId = "";
        _autoTargets[t.Id] = (t.AutoTarget, t.AutoDeviceIndex, t.AutoParamIndex, t.AutoParamId);
        t.AutoLabel = AutoLabelFor(t);
        if (ReferenceEquals(_autoSelTrack, t)) _autoSelTrack = null;   // selection was for the old lane
        LoadAutoPoints(t);
        Redraw();
    }

    /// <summary>Select a hosted-plugin parameter (by stable id) as the lane target (M9-B3).</summary>
    internal void SetAutoPluginTarget(TrackVM t, int deviceIndex, string paramId)
    {
        t.AutoTarget = AutomationTarget.PluginParam;
        t.AutoDeviceIndex = deviceIndex;
        t.AutoParamIndex = -1;
        t.AutoParamId = paramId;
        _autoTargets[t.Id] = (t.AutoTarget, t.AutoDeviceIndex, t.AutoParamIndex, t.AutoParamId);
        t.AutoLabel = AutoLabelFor(t);
        if (ReferenceEquals(_autoSelTrack, t)) _autoSelTrack = null;   // selection was for the old lane
        LoadAutoPoints(t);
        Redraw();
    }

    // --- automation range selection + clipboard (Phase 2) ------------------
    // A selection is one track's current lane between two beats; the range clipboard
    // holds that lane's points made relative to the range start, plus its length. All
    // point-level (no engine round-trip): edits go through the live AutoPoints + CommitAuto.
    internal TrackVM? _autoSelTrack;
    internal double _autoSelStart, _autoSelEnd;   // beats, kept normalized (start <= end)
    private readonly List<AutoPt> _autoClip = new();
    private double _autoClipLen;
    internal bool HasAutoSelection => _autoSelTrack is not null && _autoSelEnd - _autoSelStart > 1e-6;
    internal bool HasAutoClip => _autoClip.Count > 0 && _autoClipLen > 1e-6;

    /// <summary>Marks (or extends) the time-range selection on a track's lane.</summary>
    internal void SetAutoSelection(TrackVM t, double a, double b)
    {
        _autoSelTrack = t;
        _autoSelStart = Math.Max(0, Math.Min(a, b));
        _autoSelEnd = Math.Max(a, b);
        Redraw();
    }

    internal void ClearAutoSelection()
    {
        if (_autoSelTrack is null) return;
        _autoSelTrack = null;
        Redraw();
    }

    // Drop every live point of a track's lane in [s,e] (Cut/Delete, and the pre-clear
    // before a paste re-lands its points). Caller commits.
    private static void ClearAutoRange(TrackVM t, double s, double e)
        => t.AutoPoints.RemoveAll(p => p.Beat >= s - 1e-6 && p.Beat <= e + 1e-6);

    /// <summary>Copies the selected lane's points in range into the range clipboard
    /// (beats relative to the range start). False when there is no range selected.</summary>
    public bool CopyAutoSelection()
    {
        if (!HasAutoSelection) return false;
        var t = _autoSelTrack!;
        double s = _autoSelStart, e = _autoSelEnd;
        _autoClip.Clear();
        foreach (var p in t.AutoPoints.Where(p => p.Beat >= s - 1e-6 && p.Beat <= e + 1e-6).OrderBy(p => p.Beat))
            _autoClip.Add(new AutoPt { Beat = p.Beat - s, Value = p.Value, Curve = p.Curve });
        _autoClipLen = e - s;
        return true;
    }

    /// <summary>Copies the selection, then clears it from the lane.</summary>
    public bool CutAutoSelection()
    {
        if (!CopyAutoSelection()) return false;
        ClearAutoRange(_autoSelTrack!, _autoSelStart, _autoSelEnd);
        CommitAuto(_autoSelTrack!);
        Redraw();
        return true;
    }

    /// <summary>Clears the selected range from the lane (no clipboard change).</summary>
    public bool DeleteAutoSelection()
    {
        if (!HasAutoSelection) return false;
        ClearAutoRange(_autoSelTrack!, _autoSelStart, _autoSelEnd);
        CommitAuto(_autoSelTrack!);
        Redraw();
        return true;
    }

    /// <summary>Pastes the range clipboard onto a track's current lane at <paramref name="atBeat"/>
    /// (clearing that span first), then selects what was pasted. False if the clipboard is empty.</summary>
    internal bool PasteAutoAt(TrackVM t, double atBeat)
    {
        if (!HasAutoClip) return false;
        double s = Math.Max(0, atBeat), e = s + _autoClipLen;
        PasteRange(t, s, _autoClipLen, _autoClip);
        CommitAuto(t);
        SetAutoSelection(t, s, e);   // land the selection on the paste so it's visible/re-pasteable
        return true;
    }

    // Lays a set of relative points (beats from 0) onto the lane starting at `at`, spanning
    // `len`. Clears that span but keeps a point sitting exactly at `at` — the boundary the
    // incoming automation continues from — and skips a source point at relative 0 so it never
    // overwrites that boundary. That avoids the redundant flat point you'd otherwise get when
    // pasting/duplicating right after an existing point.
    private static void PasteRange(TrackVM t, double at, double len, IReadOnlyList<AutoPt> rel)
    {
        t.AutoPoints.RemoveAll(p => p.Beat > at + 1e-6 && p.Beat <= at + len + 1e-6);
        bool boundary = t.AutoPoints.Any(p => Math.Abs(p.Beat - at) <= 1e-6);
        foreach (var p in rel)
        {
            if (boundary && p.Beat <= 1e-6) continue;   // don't clobber the existing boundary point
            t.AutoPoints.Add(new AutoPt { Beat = at + p.Beat, Value = p.Value, Curve = p.Curve });
        }
    }

    /// <summary>Keyboard paste: onto the selected track's lane (fallback: first track) at the playhead.</summary>
    public bool PasteAuto()
    {
        var t = _autoSelTrack ?? (_tracks.Count > 0 ? _tracks[0] : null);
        return t is not null && PasteAutoAt(t, Snap(Math.Max(0.0, _playheadBeats)));
    }

    /// <summary>Cmd/Ctrl+D: duplicate the selected automation range immediately after itself
    /// (independent of the clipboard), then move the selection onto the copy so repeats chain.
    /// False when there's no range selected. </summary>
    public bool DuplicateAutoSelection()
    {
        if (!HasAutoSelection) return false;
        var t = _autoSelTrack!;
        double s = _autoSelStart, e = _autoSelEnd, len = e - s;
        var src = t.AutoPoints
            .Where(p => p.Beat >= s - 1e-6 && p.Beat <= e + 1e-6)
            .OrderBy(p => p.Beat)
            .Select(p => new AutoPt { Beat = p.Beat - s, Value = p.Value, Curve = p.Curve })
            .ToList();
        if (src.Count == 0) return false;
        PasteRange(t, e, len, src);
        CommitAuto(t);
        SetAutoSelection(t, e, e + len);
        Redraw();
        return true;
    }

    // --- master-volume automation (graph-level, M9 follow-up) --------------
    // Edited in the pinned footer master row. Value axis 0..2 (like track volume).
    internal const float MasterAutoMin = 0f, MasterAutoMax = 2f;
    internal readonly List<AutoPt> _masterAuto = new();

    internal void LoadMasterAuto()
    {
        _masterAuto.Clear();
        if (_engine is not { } e) return;
        foreach (var p in e.GetMasterVolumeAutomation())
            _masterAuto.Add(new AutoPt { Beat = p.Beat, Value = p.Value, Curve = p.Curve });
    }

    internal void CommitMasterAuto()
    {
        if (_engine is not { } e) return;
        e.SetMasterVolumeAutomation(_masterAuto
            .OrderBy(p => p.Beat)
            .Select(p => new AutomationPoint(p.Beat, p.Value, p.Curve))
            .ToArray());
    }

    /// <summary>Push a track's edited envelope back to the engine (creates/reuses the lane).</summary>
    internal void CommitAuto(TrackVM t) => CommitAutoImpl(t, live: false);

    /// <summary>Live commit while dragging a point (no undo checkpoint): the engine lane — and
    /// thus the device, via read-mode automation at the playhead — follows in real time. The
    /// drag checkpoints once at its start via CommitAuto; the release does a final live commit.</summary>
    internal void CommitAutoLive(TrackVM t) => CommitAutoImpl(t, live: true);

    private void CommitAutoImpl(TrackVM t, bool live)
    {
        if (_engine is not { } e) return;
        int lane = t.AutoTarget == AutomationTarget.PluginParam
            ? e.AddPluginAutomationLane(t.Id, t.AutoDeviceIndex, t.AutoParamId)
            : e.AddAutomationLane(t.Id, t.AutoTarget, t.AutoDeviceIndex, t.AutoParamIndex);
        if (lane < 0) return;
        var pts = t.AutoPoints
            .OrderBy(p => p.Beat)
            .Select(p => new AutomationPoint(p.Beat, p.Value, p.Curve))
            .ToArray();
        if (live) e.SetAutomationPointsLive(t.Id, lane, pts);
        else      e.SetAutomationPoints(t.Id, lane, pts);
    }

    internal float GetAutoDefaultValue(TrackVM t) => t.AutoTarget switch
    {
        AutomationTarget.Volume => 1.0f,
        AutomationTarget.Pan => 0.0f,
        _ => 0.5f,
    };

    internal float GetAutoDefaultValue(AutomationTarget target) => target switch
    {
        AutomationTarget.Volume => 1.0f,
        AutomationTarget.Pan => 0.0f,
        _ => 0.5f,
    };

    internal static bool TryParseAutoValue(AutomationTarget target, string input, out float val, string label = "")
    {
        val = 0f;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim().ToLowerInvariant();

        if (target == AutomationTarget.Volume)
        {
            if (s.StartsWith("-inf") || s == "-∞" || s == "-∞ db" || s == "inf") { val = 0.0f; return true; }
            string dbClean = s.Replace("db", "").Replace("+", "").Trim();
            if (double.TryParse(dbClean, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbVal)
                || double.TryParse(dbClean, NumberStyles.Float, CultureInfo.CurrentCulture, out dbVal))
            {
                if (s.Contains("db") || dbVal < 0 || s == "0" || s == "0.0" || s == "0,0")
                {
                    val = (float)Math.Clamp(AudioMath.DbToLin(dbVal), 0.0, 2.0);
                    return true;
                }
                val = (float)Math.Clamp(dbVal, 0.0, 2.0);
                return true;
            }
        }
        else if (target == AutomationTarget.Pan)
        {
            if (s == "center" || s == "c" || s == "0" || s == "0%") { val = 0f; return true; }
            if (s.Contains('l'))
            {
                string clean = s.Replace("l", "").Replace("%", "").Replace("-", "").Trim();
                if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                {
                    val = (float)-Math.Clamp(p > 1.0 ? p / 100.0 : p, 0.0, 1.0);
                    return true;
                }
            }
            if (s.Contains('r'))
            {
                string clean = s.Replace("r", "").Replace("%", "").Replace("+", "").Trim();
                if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                {
                    val = (float)Math.Clamp(p > 1.0 ? p / 100.0 : p, 0.0, 1.0);
                    return true;
                }
            }
        }

        if (s.Contains("st") || s.Contains("semi"))
        {
            string clean = s.Replace("st", "").Replace("semi", "").Replace("+", "").Trim();
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double stVal)
                || double.TryParse(clean, NumberStyles.Float, CultureInfo.CurrentCulture, out stVal))
            {
                val = (float)Math.Clamp((stVal / 24.0) + 0.5, 0.0, 1.0);
                return true;
            }
        }

        if (s.EndsWith('%'))
        {
            string pStr = s.TrimEnd('%').Trim();
            if (double.TryParse(pStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            {
                val = (float)Math.Clamp(pct / 100.0, 0.0, 1.0);
                return true;
            }
        }

        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
            || float.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out f))
        {
            val = f;
            return true;
        }

        return false;
    }

    // --- automation sub-lanes (stacked below track) -----------------------
    internal void AddAutoSubLane(TrackVM t)
    {
        var usedTargets = new HashSet<string> { $"{t.AutoTarget}:{t.AutoDeviceIndex}:{t.AutoParamIndex}:{t.AutoParamId}" };
        foreach (var sl in t.SubLanes)
            usedTargets.Add($"{sl.Target}:{sl.DeviceIndex}:{sl.ParamIndex}:{sl.ParamId}");

        AutomationTarget nextTarget = AutomationTarget.Pan;
        int nextDev = -1, nextParam = -1;
        string nextParamId = "", nextLabel = "Pan";

        if (!usedTargets.Contains("1:-1:-1:"))
        {
            nextTarget = AutomationTarget.Pan;
            nextLabel = "Pan";
        }
        else if (!usedTargets.Contains("0:-1:-1:"))
        {
            nextTarget = AutomationTarget.Volume;
            nextLabel = "Vol";
        }
        else if (_engine is { } e && e.TrackDeviceCount(t.Id) > 0)
        {
            for (int d = 0; d < e.TrackDeviceCount(t.Id); d++)
            {
                int pc = e.DeviceParamCount(t.Id, d);
                for (int p = 0; p < pc; p++)
                {
                    string k = $"2:{d}:{p}:";
                    if (!usedTargets.Contains(k))
                    {
                        nextTarget = AutomationTarget.DeviceParam;
                        nextDev = d; nextParam = p;
                        nextLabel = Short(e.DeviceParamName(t.Id, d, p));
                        break;
                    }
                }
                if (nextDev >= 0) break;
            }
        }

        var newSl = new AutoSubLaneVM
        {
            TrackId = t.Id,
            Target = nextTarget,
            DeviceIndex = nextDev,
            ParamIndex = nextParam,
            ParamId = nextParamId,
            Label = nextLabel,
            Height = 56,
        };
        LoadSubLanePoints(t, newSl);
        if (!_subLaneState.TryGetValue(t.Id, out var list))
        {
            list = new List<AutoSubLaneVM>();
            _subLaneState[t.Id] = list;
        }
        list.Add(newSl);
        t.SubLanes.Add(newSl);
        t.AutoExpanded = true;
        Refresh();
    }

    internal void RemoveAutoSubLane(TrackVM t, AutoSubLaneVM sl)
    {
        if (_subLaneState.TryGetValue(t.Id, out var list))
        {
            list.Remove(sl);
            if (list.Count == 0) _subLaneState.Remove(t.Id);
        }
        t.SubLanes.Remove(sl);
        Refresh();
    }

    internal void SetSubLaneTarget(TrackVM t, AutoSubLaneVM sl, AutomationTarget target, int dev, int param, string paramId = "")
    {
        sl.Target = target;
        sl.DeviceIndex = dev;
        sl.ParamIndex = param;
        sl.ParamId = paramId;
        sl.Label = target switch
        {
            AutomationTarget.Volume => "Vol",
            AutomationTarget.Pan => "Pan",
            AutomationTarget.PluginParam => _engine is { } e && dev >= 0 ? Short(PluginParamNameById(e, t, dev, paramId)) : "Plugin",
            AutomationTarget.MidiDeviceParam => _engine is { } e ? Short(e.MidiEffectParamName(t.Id, dev, param)) : "M-Param",
            _ => _engine is { } e ? Short(e.DeviceParamName(t.Id, dev, param)) : "Param",
        };
        LoadSubLanePoints(t, sl);
        Redraw();
    }

    internal void LoadSubLanePoints(TrackVM t, AutoSubLaneVM sl)
    {
        sl.Points.Clear();
        if (_engine is not { } e) return;
        int laneN = e.AutomationLaneCount(t.Id);
        int matchLane = -1;
        for (int i = 0; i < laneN; i++)
        {
            var info = e.AutomationLaneInfo(t.Id, i);
            if (info.Target != sl.Target) continue;
            bool match = sl.Target switch
            {
                AutomationTarget.DeviceParam or AutomationTarget.MidiDeviceParam =>
                    info.DeviceIndex == sl.DeviceIndex && info.ParamIndex == sl.ParamIndex,
                AutomationTarget.PluginParam => info.DeviceIndex == sl.DeviceIndex
                                                && e.AutomationLaneParamId(t.Id, i) == sl.ParamId,
                _ => true,
            };
            if (match) { matchLane = i; break; }
        }
        if (matchLane < 0) return;
        foreach (var p in e.GetAutomationPoints(t.Id, matchLane))
            sl.Points.Add(new AutoPt { Beat = p.Beat, Value = p.Value, Curve = p.Curve });
    }

    internal void CommitSubLane(TrackVM t, AutoSubLaneVM sl, bool live = false)
    {
        if (_engine is not { } e) return;
        int lane = sl.Target == AutomationTarget.PluginParam
            ? e.AddPluginAutomationLane(t.Id, sl.DeviceIndex, sl.ParamId)
            : e.AddAutomationLane(t.Id, sl.Target, sl.DeviceIndex, sl.ParamIndex);
        if (lane < 0) return;
        var pts = sl.Points
            .OrderBy(p => p.Beat)
            .Select(p => new AutomationPoint(p.Beat, p.Value, p.Curve))
            .ToArray();
        if (live) e.SetAutomationPointsLive(t.Id, lane, pts);
        else      e.SetAutomationPoints(t.Id, lane, pts);
    }

    // Arm the current target for Write recording (M9-C).
    private int ArmParamIndex(TrackVM t) => t.AutoTarget is AutomationTarget.DeviceParam or AutomationTarget.MidiDeviceParam ? t.AutoParamIndex : -1;
    internal bool AutoArmed(TrackVM t)
        => _engine is { } e && e.IsAutomationArmed(t.Id, t.AutoTarget, t.AutoDeviceIndex, ArmParamIndex(t), t.AutoParamId);
    internal bool AutoArmed(AutoSubLaneVM sl)
        => _engine is { } e && e.IsAutomationArmed(sl.TrackId, sl.Target, sl.DeviceIndex, sl.Target is AutomationTarget.DeviceParam or AutomationTarget.MidiDeviceParam ? sl.ParamIndex : -1, sl.ParamId);

    internal void ToggleAutoArm(TrackVM t)
    {
        if (_engine is not { } e) return;
        e.SetAutomationArm(t.Id, t.AutoTarget, t.AutoDeviceIndex, ArmParamIndex(t), t.AutoParamId, !AutoArmed(t));
        Redraw();
    }

    internal void ToggleAutoArm(int trackId, AutomationTarget target, int dev, int param, string paramId)
    {
        if (_engine is not { } e) return;
        int p = target is AutomationTarget.DeviceParam or AutomationTarget.MidiDeviceParam ? param : -1;
        bool cur = e.IsAutomationArmed(trackId, target, dev, p, paramId);
        e.SetAutomationArm(trackId, target, dev, p, paramId, !cur);
        Redraw();
    }

    /// <summary>Target picker: Volume / Pan / built-in device params, plus a
    /// filterable param picker + "Learn" for each hosted plugin (M9-B3).</summary>
    internal void ShowAutoTargetMenu(Control anchor, TrackVM t)
        => ShowTargetMenuInternal(anchor, t, (tgt, dev, param, pid) =>
        {
            if (tgt == AutomationTarget.PluginParam) SetAutoPluginTarget(t, dev, pid);
            else SetAutoTarget(t, tgt, dev, param);
        });

    internal void ShowSubLaneTargetMenu(Control anchor, TrackVM t, AutoSubLaneVM sl)
        => ShowTargetMenuInternal(anchor, t, (tgt, dev, param, pid) =>
        {
            SetSubLaneTarget(t, sl, tgt, dev, param, pid);
        });

    private void ShowTargetMenuInternal(Control anchor, TrackVM t, Action<AutomationTarget, int, int, string> onSelect)
    {
        var flyout = new MenuFlyout();

        // Params that already carry automation: pinned as a brass-dotted quick-access section at the top
        var automated = new HashSet<string>();
        if (_engine is { } eA)
        {
            var top = new List<(string label, Action apply)>();
            int laneN = eA.AutomationLaneCount(t.Id);
            for (int i = 0; i < laneN; i++)
            {
                var info = eA.AutomationLaneInfo(t.Id, i);
                if (info.PointCount <= 0) continue;
                string key; Action apply;
                switch (info.Target)
                {
                    case AutomationTarget.Volume:
                        key = "V"; apply = () => onSelect(AutomationTarget.Volume, -1, -1, ""); break;
                    case AutomationTarget.Pan:
                        key = "PAN"; apply = () => onSelect(AutomationTarget.Pan, -1, -1, ""); break;
                    case AutomationTarget.PluginParam:
                    { string pid = eA.AutomationLaneParamId(t.Id, i); int dv = info.DeviceIndex;
                      key = $"P:{dv}:{pid}"; apply = () => onSelect(AutomationTarget.PluginParam, dv, -1, pid); break; }
                    case AutomationTarget.MidiDeviceParam:
                    { int dv = info.DeviceIndex, pr = info.ParamIndex;
                      key = $"M:{dv}:{pr}"; apply = () => onSelect(AutomationTarget.MidiDeviceParam, dv, pr, ""); break; }
                    default:   // DeviceParam
                    { int dv = info.DeviceIndex, pr = info.ParamIndex;
                      key = $"D:{dv}:{pr}"; apply = () => onSelect(AutomationTarget.DeviceParam, dv, pr, ""); break; }
                }
                if (!automated.Add(key)) continue;
                top.Add((AutoLaneLabel(eA, t, info, i), apply));
            }
            foreach (var (label, apply) in top)
            {
                var it = new MenuItem { Header = label, Icon = AutoDot(), Foreground = DeviceCardKit.AccentBright };
                it.Click += (_, _) => apply();
                flyout.Items.Add(it);
            }
            if (top.Count > 0) flyout.Items.Add(new Separator());
        }

        // 1. Mixer section
        var mixerMenu = new MenuItem { Header = "Mixer" };
        mixerMenu.Items.Add(Leaf("Volume", "V", () => onSelect(AutomationTarget.Volume, -1, -1, ""), automated));
        mixerMenu.Items.Add(Leaf("Pan", "PAN", () => onSelect(AutomationTarget.Pan, -1, -1, ""), automated));
        flyout.Items.Add(mixerMenu);

        // 2. Dedicated Pitch & Tuning section
        var pitchMenu = new MenuItem { Header = "Pitch & Tuning" };
        bool hasAutoShift = false;
        if (_engine is { } ePitch)
        {
            int devCount = ePitch.TrackDeviceCount(t.Id);
            for (int d = 0; d < devCount; d++)
            {
                string dn = ePitch.DeviceName(t.Id, d);
                if (dn.Contains("Auto Shift", StringComparison.OrdinalIgnoreCase) || dn.Contains("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    hasAutoShift = true;
                    int pc = ePitch.DeviceParamCount(t.Id, d);
                    for (int p = 0; p < pc; p++)
                    {
                        int dd = d, pp = p;
                        pitchMenu.Items.Add(Leaf($"Auto Shift · {ePitch.DeviceParamName(t.Id, d, p)}", $"D:{dd}:{pp}",
                            () => onSelect(AutomationTarget.DeviceParam, dd, pp, ""), automated));
                    }
                }
            }
        }

        if (!hasAutoShift && _engine is { } eInsert)
        {
            var insertShiftItem = new MenuItem { Header = "Insert Knox Auto Shift (Pitch Shift ±12 st)" };
            insertShiftItem.Click += (_, _) =>
            {
                int newDev = eInsert.AddBuiltinDevice(t.Id, 10); // 10 = Knox Auto Shift
                if (newDev >= 0)
                {
                    // AutoShift.Shift is index 4
                    onSelect(AutomationTarget.DeviceParam, newDev, 4, "");
                }
            };
            pitchMenu.Items.Add(insertShiftItem);
        }

        // Instrument Pitch & Tune params
        if (t.IsInstrument && _engine is { } eInst)
        {
            int n = eInst.PluginParamCount(t.Id, -1);
            for (int i = 0; i < n; i++)
            {
                string id = eInst.PluginParamId(t.Id, -1, i);
                string nm = eInst.PluginParamName(t.Id, -1, i);
                if (id.Length == 0) continue;
                string nmLower = nm.ToLowerInvariant();
                if (nmLower.Contains("pitch") || nmLower.Contains("tune") || nmLower.Contains("detune")
                    || nmLower.Contains("glide") || nmLower.Contains("porta") || nmLower.Contains("semi")
                    || nmLower.Contains("cent") || nmLower.Contains("coarse") || nmLower.Contains("fine")
                    || nmLower.Contains("octave") || nmLower.Contains("freq") || nmLower.Contains("vibrato")
                    || nmLower.Contains("transp"))
                {
                    string pid = id;
                    pitchMenu.Items.Add(Leaf($"Synth · {nm}", $"P:-1:{pid}", () => onSelect(AutomationTarget.PluginParam, -1, -1, pid), automated));
                }
            }
        }

        // Beat Repeat Pitch Drop & Tape Flutter
        if (_engine is { } eOtherFx)
        {
            int devCount = eOtherFx.TrackDeviceCount(t.Id);
            bool hasBeatRepeat = false;
            bool hasVintageTape = false;
            for (int d = 0; d < devCount; d++)
            {
                string dn = eOtherFx.DeviceName(t.Id, d);
                if (dn.Contains("Beat Repeat", StringComparison.OrdinalIgnoreCase))
                {
                    hasBeatRepeat = true;
                    int pc = eOtherFx.DeviceParamCount(t.Id, d);
                    for (int p = 0; p < pc; p++)
                    {
                        string pn = eOtherFx.DeviceParamName(t.Id, d, p);
                        if (pn.Contains("Pitch", StringComparison.OrdinalIgnoreCase) || pn.Contains("Decay", StringComparison.OrdinalIgnoreCase))
                        {
                            int dd = d, pp = p;
                            pitchMenu.Items.Add(Leaf($"Beat Repeat · {pn}", $"D:{dd}:{pp}",
                                () => onSelect(AutomationTarget.DeviceParam, dd, pp, ""), automated));
                        }
                    }
                }
                else if (dn.Contains("Vintage Tape", StringComparison.OrdinalIgnoreCase) || dn.Contains("Tape", StringComparison.OrdinalIgnoreCase))
                {
                    hasVintageTape = true;
                    int pc = eOtherFx.DeviceParamCount(t.Id, d);
                    for (int p = 0; p < pc; p++)
                    {
                        string pn = eOtherFx.DeviceParamName(t.Id, d, p);
                        if (pn.Contains("Flutter", StringComparison.OrdinalIgnoreCase) || pn.Contains("Wow", StringComparison.OrdinalIgnoreCase) || pn.Contains("Speed", StringComparison.OrdinalIgnoreCase))
                        {
                            int dd = d, pp = p;
                            pitchMenu.Items.Add(Leaf($"Vintage Tape · {pn}", $"D:{dd}:{pp}",
                                () => onSelect(AutomationTarget.DeviceParam, dd, pp, ""), automated));
                        }
                    }
                }
            }

            if (!hasBeatRepeat)
            {
                var brItem = new MenuItem { Header = "Insert Knox Beat Repeat (Pitch Drop)" };
                brItem.Click += (_, _) =>
                {
                    int nd = eOtherFx.AddBuiltinDevice(t.Id, 11); // 11 = Beat Repeat
                    if (nd >= 0) onSelect(AutomationTarget.DeviceParam, nd, 0, "");
                };
                pitchMenu.Items.Add(brItem);
            }
            if (!hasVintageTape)
            {
                var tapeItem = new MenuItem { Header = "Insert Knox Vintage Tape (Flutter / Wow)" };
                tapeItem.Click += (_, _) =>
                {
                    int nd = eOtherFx.AddBuiltinDevice(t.Id, 8); // 8 = Vintage Tape
                    if (nd >= 0) onSelect(AutomationTarget.DeviceParam, nd, 0, "");
                };
                pitchMenu.Items.Add(tapeItem);
            }
        }
        flyout.Items.Add(pitchMenu);

        // Quick top-level Volume / Pan items
        flyout.Items.Add(Leaf("Volume", "V", () => onSelect(AutomationTarget.Volume, -1, -1, ""), automated));
        flyout.Items.Add(Leaf("Pan", "PAN", () => onSelect(AutomationTarget.Pan, -1, -1, ""), automated));

        if (_engine is { } e)
        {
            // Instrument
            if (t.IsInstrument && e.PluginParamCount(t.Id, -1) > 0)
            {
                flyout.Items.Add(new Separator());
                flyout.Items.Add(e.TrackInstrumentKind(t.Id) >= 0
                    ? BuildBuiltinInstrumentAutoMenu(e, t, automated, onSelect)
                    : BuildPluginTargetMenu(e, t, -1, "Instrument", onSelect));
            }

            // Devices / Audio Effects
            int devCount = e.TrackDeviceCount(t.Id);
            if (devCount > 0)
            {
                flyout.Items.Add(new Separator());
                for (int d = 0; d < devCount; d++)
                {
                    string dn = e.DeviceName(t.Id, d);
                    int builtinPc = e.DeviceParamCount(t.Id, d);
                    if (builtinPc > 0)
                    {
                        var devMenu = new MenuItem { Header = dn };
                        for (int p = 0; p < builtinPc; p++)
                        {
                            int dd = d, pp = p;
                            devMenu.Items.Add(Leaf(e.DeviceParamName(t.Id, d, p), $"D:{dd}:{pp}",
                                () => onSelect(AutomationTarget.DeviceParam, dd, pp, ""), automated));
                        }
                        flyout.Items.Add(devMenu);
                    }
                    else if (e.PluginParamCount(t.Id, d) > 0)
                    {
                        flyout.Items.Add(BuildPluginTargetMenu(e, t, d, dn, onSelect));
                    }
                }
            }

            // MIDI Effects
            int midiCount = e.TrackMidiEffectCount(t.Id);
            if (midiCount > 0)
            {
                flyout.Items.Add(new Separator());
                for (int m = 0; m < midiCount; m++)
                {
                    int mpc = e.MidiEffectParamCount(t.Id, m);
                    if (mpc <= 0) continue;
                    var mMenu = new MenuItem { Header = e.MidiEffectName(t.Id, m) };
                    int shown = Math.Min(mpc, 16);
                    for (int p = 0; p < shown; p++)
                    {
                        int mm = m, pp = p;
                        mMenu.Items.Add(Leaf(e.MidiEffectParamName(t.Id, m, p), $"M:{mm}:{pp}",
                            () => onSelect(AutomationTarget.MidiDeviceParam, mm, pp, ""), automated));
                    }
                    flyout.Items.Add(mMenu);
                }
            }

            // Quick Add Device & Automate
            flyout.Items.Add(new Separator());
            flyout.Items.Add(BuildQuickAddDeviceMenu(e, t, (tgt, dev, param) => onSelect(tgt, dev, param, "")));
        }

        // Sub-lane management: Add Automation Lane
        flyout.Items.Add(new Separator());
        var addLaneItem = new MenuItem { Header = "Add Automation Sub-Lane" };
        addLaneItem.Click += (_, _) => AddAutoSubLane(t);
        flyout.Items.Add(addLaneItem);

        flyout.ShowAt(anchor, showAtPointer: true);
    }

    private MenuItem BuildQuickAddDeviceMenu(IAudioEngine e, TrackVM t, Action<AutomationTarget, int, int> onTargetSelected)
    {
        var root = new MenuItem { Header = "Add Device & Automate…" };
        var quickDevices = new (string name, int kind, string defaultParamName, int defaultParamIdx)[]
        {
            ("Knox Auto Shift", 10, "Shift (Pitch)", 4),
            ("Knox Auto Filter", 7, "Cutoff", 0),
            ("Knox EQ-8", 0, "Band 1 Freq", 0),
            ("Knox Delay", 3, "Dry/Wet", 2),
            ("Knox Reverb", 2, "Dry/Wet", 2),
            ("Knox Compressor", 1, "Threshold", 0),
            ("Knox Orbit (Auto-Pan)", 9, "Rate", 0),
            ("Knox Vintage Tape", 8, "Flutter", 0),
            ("Knox Beat Repeat", 11, "Pitch Drop", 0),
            ("Knox Utility", 4, "Gain", 0),
        };

        foreach (var (name, kind, paramName, paramIdx) in quickDevices)
        {
            var item = new MenuItem { Header = $"{name} ({paramName})" };
            int k = kind; int pIdx = paramIdx;
            item.Click += (_, _) =>
            {
                int newDevIdx = e.AddBuiltinDevice(t.Id, k);
                if (newDevIdx >= 0)
                {
                    onTargetSelected(AutomationTarget.DeviceParam, newDevIdx, pIdx);
                }
            };
            root.Items.Add(item);
        }
        return root;
    }

    private MenuItem BuildBuiltinInstrumentAutoMenu(IAudioEngine e, TrackVM t, HashSet<string> automated, Action<AutomationTarget, int, int, string> onSelect)
    {
        var root = new MenuItem { Header = e.DeviceName(t.Id, -1) };
        var groups = new System.Collections.Generic.Dictionary<string, MenuItem>();
        int n = e.PluginParamCount(t.Id, -1);
        for (int i = 0; i < n; i++)
        {
            string id = e.PluginParamId(t.Id, -1, i);
            string nm = e.PluginParamName(t.Id, -1, i);
            if (id.Length == 0) continue;
            int sp = nm.LastIndexOf(' ');
            string leaf = sp >= 0 ? nm[(sp + 1)..] : nm;
            string grp = sp >= 0 ? nm[..sp] : "";
            string pid = id;
            var item = Leaf(leaf, $"P:-1:{pid}", () => onSelect(AutomationTarget.PluginParam, -1, -1, pid), automated);
            if (grp.Length == 0) root.Items.Add(item);
            else
            {
                if (!groups.TryGetValue(grp, out var g)) { g = new MenuItem { Header = grp }; groups[grp] = g; root.Items.Add(g); }
                g.Items.Add(item);
            }
        }
        return root;
    }

    private MenuItem BuildPluginTargetMenu(IAudioEngine e, TrackVM t, int deviceIndex, string name, Action<AutomationTarget, int, int, string> onSelect)
    {
        var menu = new MenuItem { Header = name };
        var choose = new MenuItem { Header = "Choose parameter…" };
        choose.Click += (_, _) => ShowPluginParamPicker(t, deviceIndex, name);
        var learn = new MenuItem { Header = "Learn (touch a control in the plugin)" };
        learn.Click += (_, _) => StartLearn(t, deviceIndex);
        menu.Items.Add(choose);
        menu.Items.Add(learn);
        return menu;
    }

    // A small brass dot dropped into a MenuItem.Icon slot to flag an already-automated param.
    private static Control AutoDot() => new Avalonia.Controls.Shapes.Ellipse
    {
        Width = 7, Height = 7, Fill = DeviceCardKit.Brass,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    // A leaf target item, dot-flagged + accent-tinted when its param already carries automation.
    private MenuItem Leaf(string header, string key, Action apply, HashSet<string> automated)
    {
        var it = new MenuItem { Header = header };
        if (automated.Contains(key)) { it.Icon = AutoDot(); it.Foreground = DeviceCardKit.AccentBright; }
        it.Click += (_, _) => apply();
        return it;
    }

    // Human-readable "Device · Param" label for an existing lane (used in the quick-access section).
    private string AutoLaneLabel(IAudioEngine e, TrackVM t, AutomationLaneInfo info, int laneIndex) => info.Target switch
    {
        AutomationTarget.Volume => "Volume",
        AutomationTarget.Pan => "Pan",
        AutomationTarget.MidiDeviceParam => $"{e.MidiEffectName(t.Id, info.DeviceIndex)} · {e.MidiEffectParamName(t.Id, info.DeviceIndex, info.ParamIndex)}",
        AutomationTarget.PluginParam => $"{e.DeviceName(t.Id, info.DeviceIndex)} · {PluginParamNameById(e, t, info.DeviceIndex, e.AutomationLaneParamId(t.Id, laneIndex))}",
        _ => $"{e.DeviceName(t.Id, info.DeviceIndex)} · {e.DeviceParamName(t.Id, info.DeviceIndex, info.ParamIndex)}",
    };

    private static string PluginParamNameById(IAudioEngine e, TrackVM t, int dev, string paramId)
    {
        int n = e.PluginParamCount(t.Id, dev);
        for (int i = 0; i < n; i++)
            if (e.PluginParamId(t.Id, dev, i) == paramId) return e.PluginParamName(t.Id, dev, i);
        return paramId;
    }

    // A built-in instrument's params listed directly, grouped by name section (the part
    // before the last space, e.g. "Filter 1"); single-word params (Attack, Volume…) sit
    // at the top level. Picking one sets a valid PluginParam target (paramId).
    private MenuItem BuildBuiltinInstrumentAutoMenu(IAudioEngine e, TrackVM t, HashSet<string> automated)
    {
        var root = new MenuItem { Header = e.DeviceName(t.Id, -1) };
        var groups = new System.Collections.Generic.Dictionary<string, MenuItem>();
        int n = e.PluginParamCount(t.Id, -1);
        for (int i = 0; i < n; i++)
        {
            string id = e.PluginParamId(t.Id, -1, i);
            string nm = e.PluginParamName(t.Id, -1, i);
            if (id.Length == 0) continue;
            int sp = nm.LastIndexOf(' ');
            string leaf = sp >= 0 ? nm[(sp + 1)..] : nm;
            string grp = sp >= 0 ? nm[..sp] : "";
            string pid = id;
            var item = Leaf(leaf, $"P:-1:{pid}", () => SetAutoPluginTarget(t, -1, pid), automated);
            if (grp.Length == 0) root.Items.Add(item);
            else
            {
                if (!groups.TryGetValue(grp, out var g)) { g = new MenuItem { Header = grp }; groups[grp] = g; root.Items.Add(g); }
                g.Items.Add(item);
            }
        }
        return root;
    }

    // A hosted plugin's menu entry: "Choose parameter…" (filterable picker) + "Learn".
    private MenuItem BuildPluginTargetMenu(IAudioEngine e, TrackVM t, int deviceIndex, string name)
    {
        var menu = new MenuItem { Header = name };
        var choose = new MenuItem { Header = "Choose parameter…" };
        choose.Click += (_, _) => ShowPluginParamPicker(t, deviceIndex, name);
        var learn = new MenuItem { Header = "Learn (touch a control in the plugin)" };
        learn.Click += (_, _) => StartLearn(t, deviceIndex);
        menu.Items.Add(choose);
        menu.Items.Add(learn);
        return menu;
    }

    // Filterable popup listing every plugin parameter; picking one sets the target.
    private void ShowPluginParamPicker(TrackVM t, int deviceIndex, string name)
    {
        if (_engine is not { } e) return;
        int n = e.PluginParamCount(t.Id, deviceIndex);
        var all = new List<(string id, string label)>(n);
        for (int i = 0; i < n; i++)
            all.Add((e.PluginParamId(t.Id, deviceIndex, i), e.PluginParamName(t.Id, deviceIndex, i)));

        var filter = new TextBox { PlaceholderText = "Filter parameters…", Margin = new Thickness(0, 0, 0, 6) };
        var list = new ListBox { MaxHeight = 260, Width = 260 };
        void Rebuild(string q)
        {
            list.ItemsSource = all
                .Where(p => q.Length == 0 || p.label.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.label).ToList();
        }
        Rebuild("");
        filter.TextChanged += (_, _) => Rebuild(filter.Text ?? "");

        var panel = new StackPanel { Margin = new Thickness(8), MinWidth = 260 };
        panel.Children.Add(new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(filter);
        panel.Children.Add(list);
        var popup = new Flyout { Content = panel };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not string lbl) return;
            var hit = all.FirstOrDefault(p => p.label == lbl);
            if (hit.id is not null) SetAutoPluginTarget(t, deviceIndex, hit.id);
            popup.Hide();
        };
        popup.ShowAt(_lanes);
    }

    // Learn: poll the plugin for the parameter the user next moves in its GUI.
    private void StartLearn(TrackVM t, int deviceIndex)
    {
        if (_engine is not { } e) return;
        e.OpenPluginEditor(t.Id, deviceIndex);           // so there's a control to touch
        e.PluginLastTouchedParam(t.Id, deviceIndex);     // clear any stale gesture
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            int idx = e.PluginLastTouchedParam(t.Id, deviceIndex);
            if (idx >= 0)
            {
                timer.Stop();
                SetAutoPluginTarget(t, deviceIndex, e.PluginParamId(t.Id, deviceIndex, idx));
            }
            else if (++ticks > 250) timer.Stop();        // give up after ~30s
        };
        timer.Start();
    }
}

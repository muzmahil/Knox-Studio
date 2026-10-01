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
    // ---- lanes ------------------------------------------------------------
    private sealed class LaneControl : Control
    {
        private readonly ArrangementView _o;
        public LaneControl(ArrangementView o)
        {
            _o = o;
            ClipToBounds = true;
        }

        private enum Drag { None, Move, TrimL, TrimR, FadeIn, FadeOut }
        private Drag _drag;
        private int _dragTrackId, _dragClipIndex;
        private ClipVM? _dragClip;
        private double _grabBeat, _origStart, _origLen, _origFadeIn, _origFadeOut;

        // Live-resize waveform preview (point 2): full-material peaks + the committed play
        // window [S0,S1] in that material's domain, captured at drag start. Drawing maps
        // each timeline beat through the committed anchor to a material point, so a resize
        // reveals/hides the REAL audio instead of squashing the fixed window peaks.
        private float[]? _rpPeaks;
        private int _rpCount;
        private double _rpMTotal, _rpS0, _rpS1;   // material total + committed window (frames for unwarped, beats for warped)
        private const double EdgePx = 6;

        // Press-pending: a clip was pressed but the 4px drag threshold isn't crossed yet, so
        // it's still a potential click (select / narrow selection), not a drag (req 2.3/2.12).
        private bool _pending;
        private Point _pressPos;
        private Drag _pendingMode;             // Move / TrimL / TrimR chosen at press
        private ClipVM? _pendingClip;
        private int _pendingTrackId, _pendingClipIndex;
        private double _pendingGrabBeat;
        private bool _pendingWasSelected;      // grabbed clip already in the multi-selection
        private Point _lastPos;                // last pointer position (drag-tooltip anchor)

        // Clip-edge resize hover & fade handles hover
        private int _hoverEdgeTrack = -1, _hoverEdgeClip = -1;
        private Drag _hoverEdge = Drag.None;
        private int _hoverClipTrack = -1, _hoverClipIndex = -1;
        private bool _hoverFadeInHandle, _hoverFadeOutHandle;
        private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
        private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
        private static readonly Typeface TitleTypeface = new(KnoxPalette.UiFont, FontStyle.Normal, FontWeight.SemiBold);

        // Middle-mouse drag panning (FL Studio navigation)
        private bool _isMiddlePanning;
        private Point _middlePanStart;
        private double _middlePanStartScrollBeats;
        private Vector _middlePanStartOffset;

        // Group move (drag a clip that's part of the multi-selection → move all).
        private List<(int track, int clip, ClipVM vm, double origStart, int origRow)>? _groupMove;
        private double _grabOrigStart;   // grabbed clip's original start (delta reference)
        private int _grabRow;            // grabbed clip's row (vertical delta reference)
        private int _moveRowDelta;       // rows to shift the group (instrument→instrument only)
        private List<double> _snapTargets = new();   // neighbour clip edges + markers (magnetic snap, 2.4)

        // Rubber-band on empty lane space. Plain drag = clip marquee (select whole clips);
        // Shift+drag = time-range selection (req 1.2). Decided at press by _emptyTimeMode.
        private bool _marqueeArmed;      // pressed on empty space; may become a drag
        private Point _marqueePress;     // press point (control coords)
        private bool _rangeActive;       // the drag crossed the threshold → a band is live
        private bool _emptyTimeMode;     // Shift held at press → author a time range, not a marquee
        private Rect? _marquee;          // live clip-marquee rect (null unless a marquee drag)
        private int _shiftClipTrack = -1, _shiftClipIndex = -1;   // Shift-armed over a clip → click falls back to rectangular clip-select
        private const double DragThreshold = 4;

        // Automation editing (M9-A3).
        private AutoPt? _autoDrag;
        private TrackVM? _autoDragTrack;
        private AutoSubLaneVM? _autoDragSubLane;
        private AutoLaneHit _autoDragHit;
        private double _autoDragLo, _autoDragHi;   // neighbour beats — a point can't be dragged past them (req 8.2.3)
        private float _autoDragStartVal;           // value at grab (for Shift fine-mode, req 8.2.2)
        private AutoPt? _bendLeft, _bendRight;   // segment being bent (M9-D)
        private TrackVM? _bendTrack;
        private AutoSubLaneVM? _bendSubLane;
        private AutoLaneHit _bendHit;
        private AutoPt? _segDragLeft, _segDragRight; // segment vertical dragging
        private TrackVM? _segDragTrack;
        private AutoSubLaneVM? _segDragSubLane;
        private AutoLaneHit _segDragHit;
        private double _segDragStartPosY;
        private float _segDragStartValL, _segDragStartValR;
        // While dragging/bending, the engine lane is updated live (no undo) so the device
        // follows in real time; the first update checkpoints undo once, the rest are raw.
        private bool _autoLiveStarted;
        private AutoPt? _hoverPoint;             // point / segment-left under the cursor (hover feedback)
        private AutoPt? _hoverSegLeft;
        private AutoPt? _hoverTensionLeft;
        private TrackVM? _hoverTrack;
        private AutoSubLaneVM? _hoverSubLane;
        private TrackVM? _rangeTrack;            // Shift-drag time-range selection in progress (Phase 2)
        private double _rangeAnchor;
        private Point _lastAutoPointerPos;       // cursor position for floating HUD tooltip
        public bool IsEditingPoint => _autoDrag != null || _bendLeft != null || _segDragLeft != null;
        private double RowTop(int i) => _o.GetTrackTop(i);
        private double RowHeight(int i) => _o.GetTrackHeightByIndex(i);
        private double MainRowHeight(int i)
        {
            if (i >= 0 && i < _o._tracks.Count) return _o.GetTrackMainHeight(_o._tracks[i].Id);
            return ArrangementView.RowHeight;
        }

        private struct AutoLaneHit
        {
            public int TrackIndex;
            public TrackVM Track;
            public AutoSubLaneVM? SubLane;
            public double Top;
            public double Height;
            public bool IsSubLane => SubLane != null;
        }

        private AutoLaneHit? GetAutoLaneHitAtY(double y)
        {
            if (y < 0) return null;
            for (int i = 0; i < _o._tracks.Count; i++)
            {
                var t = _o._tracks[i];
                double trackTop = RowTop(i);
                double mainH = MainRowHeight(i);
                if (y >= trackTop && y < trackTop + mainH)
                    return new AutoLaneHit { TrackIndex = i, Track = t, SubLane = null, Top = trackTop, Height = mainH };

                if (_o._automationMode && t.AutoExpanded && t.SubLanes.Count > 0)
                {
                    double slY = trackTop + mainH;
                    foreach (var sl in t.SubLanes)
                    {
                        if (y >= slY && y < slY + sl.Height)
                            return new AutoLaneHit { TrackIndex = i, Track = t, SubLane = sl, Top = slY, Height = sl.Height };
                        slY += sl.Height;
                    }
                }
            }
            return null;
        }

        private double LaneValueToY(AutoLaneHit hit, float v)
        {
            var (min, max) = hit.IsSubLane ? _o.AutoRange(hit.SubLane!) : _o.AutoRange(hit.Track);
            double pad = hit.IsSubLane ? 6 : 8;
            double top = hit.Top + pad, bot = hit.Top + hit.Height - pad;
            double frac = max > min ? Math.Clamp((v - min) / (max - min), 0, 1) : 0.5;
            return bot - frac * (bot - top);
        }

        private float LaneYToValue(AutoLaneHit hit, double py)
        {
            var (min, max) = hit.IsSubLane ? _o.AutoRange(hit.SubLane!) : _o.AutoRange(hit.Track);
            double pad = hit.IsSubLane ? 6 : 8;
            double top = hit.Top + pad, bot = hit.Top + hit.Height - pad;
            double frac = Math.Clamp((bot - py) / Math.Max(1.0, bot - top), 0, 1);
            return (float)(min + frac * (max - min));
        }


        private double ValueToY(TrackVM t, int row, float v)
        {
            var (min, max) = _o.AutoRange(t);
            double top = RowTop(row) + 8, bot = RowTop(row) + MainRowHeight(row) - 8;
            double frac = max > min ? Math.Clamp((v - min) / (max - min), 0, 1) : 0.5;
            return bot - frac * (bot - top);
        }
        private float YToValue(TrackVM t, int row, double py)
        {
            var (min, max) = _o.AutoRange(t);
            double top = RowTop(row) + 8, bot = RowTop(row) + MainRowHeight(row) - 8;
            double frac = Math.Clamp((bot - py) / Math.Max(1.0, bot - top), 0, 1);
            return (float)(min + frac * (max - min));
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            => _o.HandleLaneWheel(e, e.GetPosition(this).X);

        private (TrackVM track, ClipVM clip)? HitTest(Point p, out double beat)
        {
            beat = _o._scrollBeats + p.X / _o._pixelsPerBeat;
            if (p.Y < 0) return null;
            int ti = _o.GetTrackIndexAtY(p.Y);
            if (ti < 0 || ti >= _o._tracks.Count) return null;
            double top = RowTop(ti);
            double height = RowHeight(ti);
            if (p.Y < top || p.Y >= top + height) return null;

            var t = _o._tracks[ti];
            foreach (var c in t.Clips)
                if (beat >= c.StartBeat && beat <= c.StartBeat + c.LengthBeats)
                    return (t, c);
            return null;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            var pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsMiddleButtonPressed)
            {
                _isMiddlePanning = true;
                _middlePanStart = pt.Position;
                _middlePanStartScrollBeats = _o._scrollBeats;
                _middlePanStartOffset = _o._scroller?.Offset ?? default;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (_o._automationMode) { AutoPointerPressed(e); return; }
            var hit = HitTest(pt.Position, out double beat);

            if (pt.Properties.IsRightButtonPressed)
            {
                if (hit is { } h) ShowClipMenu(h.track.Id, h.clip, beat);
                else ShowLaneMenu(pt.Position, beat);
                return;
            }

            // Dedicated Tool mode actions (Pencil, Cut, Eraser)
            if (_o.CurrentTool == ArrangementTool.Eraser)
            {
                if (hit is { } eh)
                {
                    _o._engine?.DeleteClip(eh.track.Id, eh.clip.ClipIndex);
                    _o.Select(-1, -1);
                    _o.Refresh();
                }
                return;
            }
            if (_o.CurrentTool == ArrangementTool.Cut)
            {
                if (hit is { } ch)
                {
                    _o._engine?.SplitClip(ch.track.Id, ch.clip.ClipIndex, beat);
                    _o.Refresh();
                }
                return;
            }
            if (_o.CurrentTool == ArrangementTool.Pencil && hit is null)
            {
                int ti = _o.GetTrackIndexAtY(pt.Position.Y);
                if (ti >= 0 && ti < _o._tracks.Count)
                {
                    _o.AddMidiClipAt(_o._tracks[ti].Id, beat);
                    return;
                }
            }

            if (hit is not { } hh)
            {
                // Double-click empty space on any track lane → new MIDI clip there.
                if (e.ClickCount == 2)
                {
                    int ti = _o.GetTrackIndexAtY(pt.Position.Y);
                    if (ti >= 0 && ti < _o._tracks.Count && !_o._tracks[ti].IsGroup && !_o._tracks[ti].IsReturn)
                    {
                        _o.AddMidiClipAt(_o._tracks[ti].Id, beat);
                        return;
                    }
                }
                // Empty space: arm a rubber-band. Plain drag selects clips (marquee); Shift+drag
                // authors a time range; a plain click (no drag) moves the playhead (on release).
                _marqueeArmed = true;
                _marqueePress = pt.Position;
                _rangeActive = false;
                _emptyTimeMode = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                _marquee = null;
                _shiftClipTrack = -1; _shiftClipIndex = -1;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }

            if (e.ClickCount == 2)
            {
                // Keep the selection in sync with the opened editor (without firing
                // the select→Devices path), so the Clip tab targets this clip.
                _o.SelTrackId = hh.track.Id;
                _o.SelClipIndex = hh.clip.ClipIndex;
                _o.OnClipDoubleClicked(hh.track.Id, hh.clip.ClipIndex, hh.clip.IsMidi);
                return;
            }

            // Shift over a clip: arm a time-range rubber-band (like empty space) so a drag
            // authors a range even when it starts on top of a clip. A plain click (no drag)
            // falls back to the rectangular clip selection from the anchor (req 1.1.3).
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                _marqueeArmed = true;
                _marqueePress = pt.Position;
                _rangeActive = false;
                _emptyTimeMode = true;
                _marquee = null;
                _shiftClipTrack = hh.track.Id; _shiftClipIndex = hh.clip.ClipIndex;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }

            // Cmd/Ctrl+click toggles this clip in the selection — never starts a drag (req 1.1.2).
            if (ArrangementView.IsPrimaryDown(e.KeyModifiers))
            {
                _o.ToggleSelect(hh.track.Id, hh.clip.ClipIndex);
                return;
            }

            double x0 = _o.BeatToX(hh.clip.StartBeat);
            double x1 = _o.BeatToX(hh.clip.StartBeat + hh.clip.LengthBeats);
            double px = pt.Position.X;

            // Audio clips: check for fade-in and fade-out notch handle grab
            if (!hh.clip.IsMidi)
            {
                int row = _o._tracks.IndexOf(hh.track);
                double y = row >= 0 ? RowTop(row) : 0;
                double rh = row >= 0 ? RowHeight(row) : 64;
                double stripH = Math.Min(14, rh - 4);
                bool topZone = pt.Position.Y >= y + stripH - 2 && pt.Position.Y <= y + stripH + 22;
                double xin = x0 + hh.clip.FadeInBeats * _o._pixelsPerBeat;
                double xout = x1 - hh.clip.FadeOutBeats * _o._pixelsPerBeat;

                if (topZone)
                {
                    bool hitIn = (hh.clip.FadeInBeats <= 0.001 && px >= x0 - 4 && px <= x0 + 16)
                                 || (hh.clip.FadeInBeats > 0.001 && (Math.Abs(px - xin) <= 10 || (px >= x0 - 4 && px <= xin + 6)));
                    if (hitIn)
                    {
                        _pending = false;
                        _drag = Drag.FadeIn;
                        _dragTrackId = hh.track.Id;
                        _dragClipIndex = hh.clip.ClipIndex;
                        _dragClip = hh.clip;
                        _origFadeIn = hh.clip.FadeInBeats;
                        _grabBeat = beat;
                        e.Pointer.Capture(this);
                        InvalidateVisual();
                        return;
                    }

                    bool hitOut = (hh.clip.FadeOutBeats <= 0.001 && px >= x1 - 16 && px <= x1 + 4)
                                  || (hh.clip.FadeOutBeats > 0.001 && (Math.Abs(px - xout) <= 10 || (px >= xout - 6 && px <= x1 + 4)));
                    if (hitOut)
                    {
                        _pending = false;
                        _drag = Drag.FadeOut;
                        _dragTrackId = hh.track.Id;
                        _dragClipIndex = hh.clip.ClipIndex;
                        _dragClip = hh.clip;
                        _origFadeOut = hh.clip.FadeOutBeats;
                        _grabBeat = beat;
                        e.Pointer.Capture(this);
                        InvalidateVisual();
                        return;
                    }
                }
            }

            // Arm a press-pending: whether this becomes a drag (move/trim) or a click
            // (select / narrow selection) is decided once the 4px threshold is crossed.
            _pending = true;
            _pressPos = pt.Position;
            _pendingMode = (px - x0 <= EdgePx) ? Drag.TrimL : (x1 - px <= EdgePx) ? Drag.TrimR : Drag.Move;
            _pendingClip = hh.clip;
            _pendingTrackId = hh.track.Id;
            _pendingClipIndex = hh.clip.ClipIndex;
            _pendingGrabBeat = beat;
            _pendingWasSelected = _o.IsSelected(hh.track.Id, hh.clip.ClipIndex);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        // Snapshot start + row of every selected clip so the group moves rigidly.
        private void BeginGroupMove(int grabTrackId, double grabbedStart, double grabBeat)
        {
            _drag = Drag.Move;
            _grabBeat = grabBeat;
            _grabOrigStart = grabbedStart;
            _moveRowDelta = 0;
            _groupMove = new List<(int, int, ClipVM, double, int)>();
            for (int r = 0; r < _o._tracks.Count; r++)
            {
                var t = _o._tracks[r];
                if (t.Id == grabTrackId) _grabRow = r;
                foreach (var c in t.Clips)
                    if (_o.IsSelected(t.Id, c.ClipIndex))
                        _groupMove.Add((t.Id, c.ClipIndex, c, c.StartBeat, r));
            }
            // Magnetic-snap targets: every other clip's edges + markers (exclude the group).
            _snapTargets = _o.SnapTargets(new HashSet<(int, int)>(_groupMove.Select(m => (m.track, m.clip))));
        }

        // Vertical shift is allowed only when every dragged clip lands on a track of
        // the same type (instrument→instrument or audio→audio); else stay horizontal.
        private int ClampRowDelta(int delta)
        {
            if (delta == 0 || _groupMove is null) return 0;
            foreach (var m in _groupMove)
            {
                int tr = m.origRow + delta;
                if (tr < 0 || tr >= _o._tracks.Count) return 0;
                if (_o._tracks[tr].IsInstrument != _o._tracks[m.origRow].IsInstrument) return 0;
            }
            return delta;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var pos = e.GetPosition(this);
            if (_isMiddlePanning)
            {
                var delta = pos - _middlePanStart;
                double beatDelta = delta.X / _o._pixelsPerBeat;
                _o._scrollBeats = Math.Clamp(_middlePanStartScrollBeats - beatDelta, 0, _o._hScroll.Maximum);
                _o._hScroll.Value = _o._scrollBeats;
                if (_o._scroller is not null)
                {
                    _o._scroller.Offset = new Vector(_o._scroller.Offset.X, Math.Max(0, _middlePanStartOffset.Y - delta.Y));
                }
                _o.Redraw();
                e.Handled = true;
                return;
            }

            if (_o._automationMode) { AutoPointerMoved(e); return; }
            _lastPos = pos;
            _lastMods = e.KeyModifiers;

            if (_marqueeArmed)
            {
                if (_rangeActive || Math.Abs(pos.X - _marqueePress.X) >= DragThreshold
                                 || Math.Abs(pos.Y - _marqueePress.Y) >= DragThreshold)
                {
                    _rangeActive = true;
                    if (_emptyTimeMode)
                    {
                        // Shift+drag authors a time-range live: snap both edges (Alt bypasses),
                        // extend across rows vertically (req 1.2.1/1.2.2).
                        double a = _o.SnapMaybe(_o._scrollBeats + _marqueePress.X / _o._pixelsPerBeat, e.KeyModifiers);
                        double b = _o.SnapMaybe(_o._scrollBeats + pos.X / _o._pixelsPerBeat, e.KeyModifiers);
                        int rowA = Math.Clamp(_o.GetTrackIndexAtY(_marqueePress.Y), 0, Math.Max(0, _o._tracks.Count - 1));
                        int rowB = Math.Clamp(_o.GetTrackIndexAtY(pos.Y), 0, Math.Max(0, _o._tracks.Count - 1));
                        _o.SetTimeSelection(a, b, rowA, rowB);
                    }
                    else
                    {
                        // Plain drag = clip marquee (normalised; drag can go up/left).
                        _marquee = new Rect(
                            Math.Min(_marqueePress.X, pos.X), Math.Min(_marqueePress.Y, pos.Y),
                            Math.Abs(pos.X - _marqueePress.X), Math.Abs(pos.Y - _marqueePress.Y));
                    }
                }
                InvalidateVisual();
                return;
            }

            // Promote a press-pending to a real drag once the threshold is crossed (req 2.3);
            // below the threshold it stays a click and the model is never touched.
            if (_pending)
            {
                if (Math.Abs(pos.X - _pressPos.X) < DragThreshold && Math.Abs(pos.Y - _pressPos.Y) < DragThreshold)
                    return;
                _pending = false;
                if (_pendingMode == Drag.Move)
                {
                    // Grabbing an already-selected clip moves the whole group; otherwise the
                    // clip becomes the sole selection first (req 2.1/2.2).
                    if (!_pendingWasSelected) _o.Select(_pendingTrackId, _pendingClipIndex);
                    BeginGroupMove(_pendingTrackId, _pendingClip!.StartBeat, _pendingGrabBeat);
                }
                else
                {
                    _o.Select(_pendingTrackId, _pendingClipIndex);   // trims act on a single clip
                    _drag = _pendingMode;
                    _dragTrackId = _pendingTrackId; _dragClipIndex = _pendingClipIndex;
                    _dragClip = _pendingClip; _origStart = _pendingClip!.StartBeat; _origLen = _pendingClip.LengthBeats;
                    _grabBeat = _pendingGrabBeat;
                    _snapTargets = _o.SnapTargets(new HashSet<(int, int)> { (_pendingTrackId, _pendingClipIndex) });
                    if (_dragClip is not null && !_dragClip.IsMidi) CaptureResizePreview(_dragTrackId, _dragClipIndex);   // real-audio resize preview (point 2)
                }
            }

            if (_drag == Drag.None) { UpdateEdgeHover(pos); return; }
            EnsureAutoScroll();
            UpdateActiveDrag(pos, e.KeyModifiers);
        }

        private double _lastActiveDragDelta = double.NaN;
        private int _lastActiveDragRowDelta = int.MinValue;
        private double _lastActiveTrimLen = double.NaN;
        private double _lastActiveTrimStart = double.NaN;

        // Apply a move/resize drag to the preview at the given pointer position + modifiers.
        // Split out so the autoscroll timer can re-run it after nudging the view (req 2.10).
        private void UpdateActiveDrag(Point pos, KeyModifiers mods)
        {
            double beat = _o._scrollBeats + pos.X / _o._pixelsPerBeat;

            if (_drag == Drag.Move && _groupMove is not null)
            {
                // Snap by the grabbed clip (Alt bypasses; magnetic to neighbours), then shift
                // the whole group by the same delta.
                double snappedStart = Math.Max(0, _o.SnapMagnetic(_grabOrigStart + (beat - _grabBeat), mods, _snapTargets));
                double delta = snappedStart - _grabOrigStart;
                int targetRow = Math.Clamp(_o.GetTrackIndexAtY(pos.Y), 0, _o._tracks.Count - 1);
                int rowDelta = ClampRowDelta(targetRow - _grabRow);

                if (Math.Abs(delta - _lastActiveDragDelta) < 1e-6 && rowDelta == _lastActiveDragRowDelta)
                    return;

                _lastActiveDragDelta = delta;
                _lastActiveDragRowDelta = rowDelta;

                foreach (var m in _groupMove) m.vm.StartBeat = Math.Max(0, m.origStart + delta);
                // Vertical: how many rows to shift (instrument→instrument only).
                _moveRowDelta = rowDelta;
                InvalidateVisual();
                return;
            }

            if (_dragClip is null) return;
            double d = beat - _grabBeat;
            switch (_drag)
            {
                case Drag.FadeIn:
                    double maxFadeIn = Math.Max(0, _dragClip.LengthBeats - _dragClip.FadeOutBeats - 0.05);
                    double newFadeIn = Math.Clamp(beat - _dragClip.StartBeat, 0, maxFadeIn);
                    if (Math.Abs(newFadeIn - _dragClip.FadeInBeats) > 1e-4)
                    {
                        _dragClip.FadeInBeats = newFadeIn;
                        var pts = BuildFadeEnvelope(_dragClip.FadeInBeats, _dragClip.FadeOutBeats, _dragClip.LengthBeats);
                        _o._engine?.SetClipVolumeEnvelope(_dragTrackId, _dragClipIndex, pts);
                    }
                    break;
                case Drag.FadeOut:
                    double maxFadeOut = Math.Max(0, _dragClip.LengthBeats - _dragClip.FadeInBeats - 0.05);
                    double newFadeOut = Math.Clamp((_dragClip.StartBeat + _dragClip.LengthBeats) - beat, 0, maxFadeOut);
                    if (Math.Abs(newFadeOut - _dragClip.FadeOutBeats) > 1e-4)
                    {
                        _dragClip.FadeOutBeats = newFadeOut;
                        var pts = BuildFadeEnvelope(_dragClip.FadeInBeats, _dragClip.FadeOutBeats, _dragClip.LengthBeats);
                        _o._engine?.SetClipVolumeEnvelope(_dragTrackId, _dragClipIndex, pts);
                    }
                    break;
                case Drag.TrimR:
                    double newEnd = _o.SnapMagnetic(_origStart + _origLen + d, mods, _snapTargets);
                    double newLen = Math.Max(0.25, newEnd - _dragClip.StartBeat);
                    if (Math.Abs(newLen - _lastActiveTrimLen) < 1e-6) return;
                    _lastActiveTrimLen = newLen;
                    _dragClip.LengthBeats = newLen;
                    break;
                case Drag.TrimL:
                    double ns = Math.Clamp(_o.SnapMagnetic(_origStart + d, mods, _snapTargets), 0, _origStart + _origLen - 0.25);
                    double nl = (_origStart + _origLen) - ns;
                    if (Math.Abs(ns - _lastActiveTrimStart) < 1e-6 && Math.Abs(nl - _lastActiveTrimLen) < 1e-6) return;
                    _lastActiveTrimStart = ns;
                    _lastActiveTrimLen = nl;
                    _dragClip.StartBeat = ns;
                    _dragClip.LengthBeats = nl;
                    break;
            }
            InvalidateVisual();
        }

        // --- autoscroll near the viewport edges during a drag (req 2.10) --------
        private DispatcherTimer? _autoScroll;
        private KeyModifiers _lastMods;
        private void EnsureAutoScroll()
        {
            if (_autoScroll is not null) { if (!_autoScroll.IsEnabled) _autoScroll.Start(); return; }
            _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _autoScroll.Tick += (_, _) => AutoScrollTick();
            _autoScroll.Start();
        }
        private void AutoScrollTick()
        {
            // Only clip move/resize drags autoscroll; anything else stops the timer.
            if (_drag != Drag.Move && _drag != Drag.TrimL && _drag != Drag.TrimR) { _autoScroll?.Stop(); return; }
            const double zone = 40;                 // logical px from an edge where scrolling kicks in
            double w = Bounds.Width;
            double depth = _lastPos.X < zone ? -(zone - _lastPos.X)
                         : _lastPos.X > w - zone ? (_lastPos.X - (w - zone))
                         : 0;
            if (depth == 0) return;
            // Beats/tick scales with how deep into the edge the cursor is (0.3..~2 beats).
            double beats = Math.Sign(depth) * Math.Clamp(Math.Abs(depth) / zone, 0.15, 1.0) * 2.0;
            _o.ScrollByBeats(beats);
            UpdateActiveDrag(_lastPos, _lastMods);   // the clip follows the newly-scrolled view
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_isMiddlePanning)
            {
                _isMiddlePanning = false;
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }

            if (_o._automationMode) { AutoPointerReleased(e); return; }
            var eng = _o._engine;

            // A press that never crossed the drag threshold is a click on a clip: select it,
            // or narrow a multi-selection down to just it (req 2.2/2.12). No model change.
            if (_pending)
            {
                _pending = false;
                e.Pointer.Capture(null);
                if (!_pendingWasSelected || _o.SelectionCount > 1)
                    _o.Select(_pendingTrackId, _pendingClipIndex);
                // Also drop the playhead where the user clicked (same as clicking empty lane) —
                // but not during playback: scrubbing off the grid is disruptive, use the ruler.
                if (_o._engine is not { IsPlaying: true })
                    _o.SeekTo(_o._scrollBeats + _pressPos.X / _o._pixelsPerBeat);
                InvalidateVisual();
                return;
            }

            if (_marqueeArmed)
            {
                _marqueeArmed = false;
                e.Pointer.Capture(null);
                if (!_rangeActive)
                {
                    if (_shiftClipIndex >= 0)
                        _o.ShiftSelectTo(_shiftClipTrack, _shiftClipIndex);   // Shift-click on a clip: rectangular clip-select (req 1.1.3)
                    else
                    {
                        // Plain click on empty space: select the clicked track, clear time selection and move the playhead
                        // (except during playback — scrub from the ruler, not the grid).
                        int ti = _o.GetTrackIndexAtY(_marqueePress.Y);
                        if (ti >= 0 && ti < _o._tracks.Count)
                            _o.Select(_o._tracks[ti].Id, -1);
                        else
                            _o.Select(-1, -1);
                        _o.ClearTimeSelection();
                        if (_o._engine is not { IsPlaying: true })
                            _o.SeekTo(_o._scrollBeats + _marqueePress.X / _o._pixelsPerBeat);
                    }
                }
                else if (!_emptyTimeMode && _marquee is { } r)
                {
                    SelectInMarquee(r);   // plain drag selected whole clips
                }
                // Shift+drag already authored the live time range; nothing to commit.
                _rangeActive = false; _marquee = null;
                _shiftClipTrack = -1; _shiftClipIndex = -1;
                InvalidateVisual();
                return;
            }

            if (_drag == Drag.Move && _groupMove is not null)
            {
                int rowDelta = _moveRowDelta;
                // Nothing actually moved (snap kept every start, no row change) → don't touch
                // the model, so a jiggle-and-release doesn't create a no-op undo step.
                bool moved = rowDelta != 0 || _groupMove.Any(m => Math.Abs(m.vm.StartBeat - m.origStart) > 1e-9);
                bool keptDeviceAuto = false;
                if (eng is not null && moved)
                    // Descending clip index: cross-track moves erase from the source and
                    // would otherwise invalidate lower indices on the same track.
                    foreach (var m in _groupMove.OrderByDescending(m => m.clip))
                    {
                        int destRow = m.origRow + rowDelta;
                        int destTrack = (rowDelta != 0 && destRow >= 0 && destRow < _o._tracks.Count)
                            ? _o._tracks[destRow].Id : m.track;
                        if (destTrack != m.track)
                        {
                            eng.MoveClipToTrack(m.track, m.clip, destTrack, m.vm.StartBeat);
                            keptDeviceAuto |= eng.LastMoveKeptDeviceAutomation();
                        }
                        else eng.MoveClip(m.track, m.clip, m.vm.StartBeat);
                    }
                if (keptDeviceAuto)   // explicit hint: only Volume/Pan followed across tracks (req 8.3.4)
                    _o.StatusMessage?.Invoke("Moved across tracks — device automation stayed on the source");
                _groupMove = null; _drag = Drag.None; _moveRowDelta = 0;
                e.Pointer.Capture(null);
                if (moved && rowDelta != 0) _o.Select(-1, -1);   // indices changed → drop stale selection
                if (moved) { _o.Refresh(); _o.NotifyClipsChanged(); } else InvalidateVisual();
                return;
            }

            if (_drag == Drag.FadeIn || _drag == Drag.FadeOut)
            {
                if (_dragClip is not null && eng is not null)
                {
                    var pts = BuildFadeEnvelope(_dragClip.FadeInBeats, _dragClip.FadeOutBeats, _dragClip.LengthBeats);
                    eng.SetClipVolumeEnvelope(_dragTrackId, _dragClipIndex, pts);
                    _o.NotifyClipsChanged();
                }
                _drag = Drag.None;
                _dragClip = null;
                e.Pointer.Capture(null);
                InvalidateVisual();
                return;
            }

            if (_drag == Drag.None || _dragClip is null) { _drag = Drag.None; return; }
            // Skip a resize that didn't change anything (a click on the edge) — no undo step.
            bool changed = Math.Abs(_dragClip.StartBeat - _origStart) > 1e-9
                        || Math.Abs(_dragClip.LengthBeats - _origLen) > 1e-9;
            if (eng is not null && changed)
            {
                // Audio clips resize the grid-relative way: warped clips (or unwarped clips
                // dragged past their source) stretch, otherwise trim — the engine picks.
                // MIDI clips trim as before. When Stretch tool is active, time-stretch the clip.
                if (_dragClip.IsMidi)
                    eng.TrimClip(_dragTrackId, _dragClipIndex, _dragClip.StartBeat, _dragClip.LengthBeats);
                else if (_o.CurrentTool == ArrangementTool.Stretch)
                {
                    eng.SetClipWarp(_dragTrackId, _dragClipIndex, true, 3);
                    eng.SetClipWarpLength(_dragTrackId, _dragClipIndex, _dragClip.LengthBeats);
                    eng.MoveClip(_dragTrackId, _dragClipIndex, _dragClip.StartBeat);
                }
                else
                    eng.ResizeAudioClip(_dragTrackId, _dragClipIndex, _dragClip.StartBeat, _dragClip.LengthBeats);
            }
            _drag = Drag.None;
            _dragClip = null;
            _rpPeaks = null;   // drop the resize-preview peaks
            e.Pointer.Capture(null);
            if (changed) { _o.Refresh(); _o.NotifyClipsChanged(); } else InvalidateVisual();
        }

        // Esc / lost pointer capture / lost window focus: abandon any in-progress gesture with
        // NO model change (the model is only touched on release), restoring the live preview
        // from the engine snapshot (req 1.2.6 / 2.11 / 7.9).
        internal bool CancelGesture()
        {
            // Only a *committed* gesture (drag/resize/range/point edit) is cancellable. A mere
            // press-pending or armed rubber-band is a click-in-progress: leave it untouched so
            // that a PointerCaptureLost fired around button-up doesn't wipe the latch before
            // OnPointerReleased turns it into a selection.
            bool active = _drag != Drag.None || _groupMove is not null || _rangeActive
                          || _autoDrag is not null || _bendLeft is not null || _rangeTrack is not null;
            if (!active) return false;
            _pending = false; _drag = Drag.None; _groupMove = null; _moveRowDelta = 0; _dragClip = null; _rpPeaks = null;
            _marqueeArmed = false; _rangeActive = false; _marquee = null; _shiftClipTrack = -1; _shiftClipIndex = -1;
            _autoDrag = null; _autoDragTrack = null; _bendLeft = _bendRight = null; _bendTrack = null;
            _rangeTrack = null;
            _autoScroll?.Stop();
            SetResizeCursor(false);
            _o.Refresh();   // reload authoritative clip/automation positions, discarding the preview
            return true;
        }

        // NB: we deliberately do NOT cancel on OnPointerCaptureLost. On macOS that event fires
        // *before* OnPointerReleased on a normal button-up, so cancelling there would abort every
        // drag/resize/marquee right before it commits. External loss (alt-tab) is handled via the
        // window's Deactivated event instead (see MainWindow), and Esc cancels explicitly.

        // Select every clip whose rect intersects the marquee (across all tracks), replacing
        // the current selection.
        private void SelectInMarquee(Rect r)
        {
            var hits = new List<(int, int)>();
            for (int i = 0; i < _o._tracks.Count; i++)
            {
                double y = RowTop(i);
                double rh = RowHeight(i);
                if (!r.Intersects(new Rect(0, y, double.MaxValue, rh))) continue;
                foreach (var c in _o._tracks[i].Clips)
                {
                    double cx0 = _o.BeatToX(c.StartBeat);
                    double cx1 = _o.BeatToX(c.StartBeat + c.LengthBeats);
                    var clipRect = new Rect(cx0, y, Math.Max(1, cx1 - cx0), rh);
                    if (r.Intersects(clipRect)) hits.Add((_o._tracks[i].Id, c.ClipIndex));
                }
            }
            _o.SetSelection(hits);
        }

        // --- automation editing (M9-A3) ------------------------------------
        private void AutoPointerPressed(PointerPressedEventArgs e)
        {
            var pt = e.GetCurrentPoint(this);
            var pos = pt.Position;
            _lastAutoPointerPos = pos;
            var hit = GetAutoLaneHitAtY(pos.Y);
            if (hit == null) return;
            var t = hit.Value.Track;
            var sl = hit.Value.SubLane;
            int i = hit.Value.TrackIndex;

            double curBeat = Math.Max(0, _o.SnapMaybe(_o._scrollBeats + pos.X / _o._pixelsPerBeat, e.KeyModifiers));
            bool right = pt.Properties.IsRightButtonPressed;
            bool dbl = e.ClickCount == 2;

            // Shift + left-drag: time-range selection
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && !right)
            {
                _rangeTrack = t; _rangeAnchor = curBeat;
                _o.SetAutoSelection(t, curBeat, curBeat);
                e.Pointer.Capture(this);
                return;
            }

            var pts = sl != null ? sl.Points : t.AutoPoints;

            // Check hit on existing node (grab radius 7px)
            AutoPt? hitNode = null;
            foreach (var p in pts)
            {
                double dx = _o.BeatToX(p.Beat) - pos.X;
                double dy = LaneValueToY(hit.Value, p.Value) - pos.Y;
                if (dx * dx + dy * dy <= 49) { hitNode = p; break; }
            }

            if (hitNode is not null)
            {
                if (right)
                {
                    ShowAutoPointMenu(t, hitNode, pos, sl);
                    return;
                }
                if (dbl)
                {
                    pts.Remove(hitNode);
                    if (sl != null) _o.CommitSubLane(t, sl);
                    else _o.CommitAuto(t);
                    InvalidateVisual();
                    return;
                }
                StartAutoPointDrag(hit.Value, hitNode, e);
                return;
            }

            // Check hit on segment or midpoint tension handle
            var hitSeg = HitSegmentWithTension(hit.Value, pos);
            if (hitSeg is { } sg)
            {
                if (right)
                {
                    sg.left.Curve = 0f;
                    if (sl != null) _o.CommitSubLane(t, sl);
                    else _o.CommitAuto(t);
                    InvalidateVisual();
                    return;
                }

                if (sg.isTension || (e.KeyModifiers & KeyModifiers.Alt) != 0)
                {
                    _bendLeft = sg.left; _bendRight = sg.right; _bendTrack = t; _bendSubLane = sl; _bendHit = hit.Value;
                    e.Pointer.Capture(this);
                    return;
                }

                if (dbl)
                {
                    _o.ClearAutoSelection();
                    var newPt = new AutoPt { Beat = curBeat, Value = LaneYToValue(hit.Value, pos.Y) };
                    pts.Add(newPt);
                    StartAutoPointDrag(hit.Value, newPt, e);
                    return;
                }

                // Single-click on segment: start vertical segment drag
                _segDragLeft = sg.left;
                _segDragRight = sg.right;
                _segDragTrack = t;
                _segDragSubLane = sl;
                _segDragHit = hit.Value;
                _segDragStartPosY = pos.Y;
                _segDragStartValL = sg.left.Value;
                _segDragStartValR = sg.right.Value;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }

            if (right)
            {
                ShowAutoMenu(t, curBeat, sl);
                return;
            }

            if (dbl)
            {
                _o.ClearAutoSelection();
                var newPt = new AutoPt { Beat = curBeat, Value = LaneYToValue(hit.Value, pos.Y) };
                pts.Add(newPt);
                StartAutoPointDrag(hit.Value, newPt, e);
                return;
            }

            // Single click on empty lane: if clicking near baseline, create first point and drag
            if (pts.Count == 0)
            {
                float baseVal = sl != null ? _o.AutoCurrent(sl) : _o.AutoCurrent(t);
                double by = LaneValueToY(hit.Value, baseVal);
                if (Math.Abs(by - pos.Y) <= 10)
                {
                    var newPt = new AutoPt { Beat = curBeat, Value = baseVal };
                    pts.Add(newPt);
                    StartAutoPointDrag(hit.Value, newPt, e);
                    return;
                }
            }

            // Otherwise seek playhead
            _o.ClearAutoSelection();
            if (_o._engine is not { IsPlaying: true })
                _o.SeekTo(_o._scrollBeats + pos.X / _o._pixelsPerBeat);
        }

        private void StartAutoPointDrag(AutoLaneHit hit, AutoPt node, PointerPressedEventArgs e)
        {
            _autoDrag = node;
            _autoDragTrack = hit.Track;
            _autoDragSubLane = hit.SubLane;
            _autoDragHit = hit;
            _autoDragStartVal = node.Value;
            _autoDragLo = 0; _autoDragHi = double.MaxValue;
            var pts = hit.IsSubLane ? hit.SubLane!.Points : hit.Track.AutoPoints;
            foreach (var p in pts)
            {
                if (ReferenceEquals(p, node)) continue;
                if (p.Beat <= node.Beat) _autoDragLo = Math.Max(_autoDragLo, p.Beat);
                else _autoDragHi = Math.Min(_autoDragHi, p.Beat);
            }
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        // The segment under pos + whether the hit is specifically on the midpoint tension handle.
        private (AutoPt left, AutoPt right, bool isTension)? HitSegmentWithTension(AutoLaneHit hit, Point pos)
        {
            var pts = hit.IsSubLane ? hit.SubLane!.Points : hit.Track.AutoPoints;
            var ordered = pts.OrderBy(p => p.Beat).ToList();
            if (ordered.Count < 2) return null;
            double beat = _o._scrollBeats + pos.X / _o._pixelsPerBeat;

            for (int k = 1; k < ordered.Count; k++)
            {
                var a = ordered[k - 1]; var b = ordered[k];
                if (b.Beat < a.Beat) continue;
                double span = b.Beat - a.Beat;
                if (span <= 0) continue;

                // Check midpoint tension handle (7px grab radius)
                double midBeat = a.Beat + span * 0.5;
                double midVal = a.Value + (b.Value - a.Value) * Shape(0.5, a.Curve);
                double midX = _o.BeatToX(midBeat);
                double midY = LaneValueToY(hit, (float)midVal);
                double dx = pos.X - midX, dy = pos.Y - midY;
                if (dx * dx + dy * dy <= 49)
                    return (a, b, true);

                if (beat < a.Beat - 0.05 || beat > b.Beat + 0.05) continue;
                double tt = Math.Clamp((beat - a.Beat) / span, 0, 1);
                double vy = a.Value + (b.Value - a.Value) * Shape(tt, a.Curve);
                double cy = LaneValueToY(hit, (float)vy);
                if (Math.Abs(cy - pos.Y) <= 8)
                    return (a, b, false);
            }
            return null;
        }

        private void AutoBendMoved(Point pos)
        {
            if (_bendLeft is null || _bendRight is null || _bendTrack is null) return;
            float v0 = _bendLeft.Value, v1 = _bendRight.Value;
            if (Math.Abs(v1 - v0) < 1e-4f)
            {
                double laneH = _bendHit.Height - (_bendHit.IsSubLane ? 12 : 16);
                double lineY = LaneValueToY(_bendHit, v0);
                double deltaY = (lineY - pos.Y) / Math.Max(1.0, laneH * 0.5);
                _bendLeft.Curve = (float)Math.Clamp(deltaY, -1.0, 1.0);
            }
            else
            {
                double val = LaneYToValue(_bendHit, pos.Y);
                double frac = Math.Clamp((val - v0) / (v1 - v0), 0.02, 0.98);
                double ev = Math.Log(frac) / Math.Log(0.5);
                _bendLeft.Curve = (float)Math.Clamp(-Math.Log2(ev) / 4.0, -1.0, 1.0);
            }
            CommitAutoDuringDrag(_bendTrack, _bendSubLane);
            InvalidateVisual();
        }

        // Hover feedback (M9-D): highlight the point, tension handle or segment under cursor.
        private void UpdateHover(Point pos)
        {
            _lastAutoPointerPos = pos;
            AutoPt? pt = null, seg = null, tension = null;
            TrackVM? tr = null;
            AutoSubLaneVM? sl = null;
            var hit = GetAutoLaneHitAtY(pos.Y);
            if (hit is { } h)
            {
                var pts = h.IsSubLane ? h.SubLane!.Points : h.Track.AutoPoints;
                foreach (var p in pts)
                {
                    double dx = _o.BeatToX(p.Beat) - pos.X, dy = LaneValueToY(h, p.Value) - pos.Y;
                    if (dx * dx + dy * dy <= 49) { pt = p; break; }
                }
                if (pt is null)
                {
                    var hSeg = HitSegmentWithTension(h, pos);
                    if (hSeg is { } sg)
                    {
                        if (sg.isTension) tension = sg.left;
                        else seg = sg.left;
                    }
                }
                if (pt is not null || seg is not null || tension is not null)
                {
                    tr = h.Track;
                    sl = h.SubLane;
                }
            }

            if (!ReferenceEquals(pt, _hoverPoint) || !ReferenceEquals(seg, _hoverSegLeft) || !ReferenceEquals(tension, _hoverTensionLeft) || !ReferenceEquals(tr, _hoverTrack) || !ReferenceEquals(sl, _hoverSubLane))
            {
                _hoverPoint = pt; _hoverSegLeft = seg; _hoverTensionLeft = tension; _hoverTrack = tr; _hoverSubLane = sl;
                if (tension is not null) Cursor = HandCursor;
                else if (seg is not null) Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
                else if (pt is not null) Cursor = HandCursor;
                else Cursor = ArrowCursor;
                InvalidateVisual();
            }
        }

        private void ClearHover()
        {
            if (_hoverPoint is null && _hoverSegLeft is null && _hoverTensionLeft is null) return;
            _hoverPoint = null; _hoverSegLeft = null; _hoverTensionLeft = null; _hoverTrack = null; _hoverSubLane = null;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e) { ClearHover(); ClearEdgeHover(); base.OnPointerExited(e); }

        // Which edge or fade handle a gesture would grab at `pos`
        private void UpdateEdgeHover(Point pos)
        {
            var hit = HitTest(pos, out _);
            var edge = Drag.None; int et = -1, ec = -1;
            bool hitFadeIn = false, hitFadeOut = false;
            int hoverT = -1, hoverC = -1;

            if (hit is { } h)
            {
                double x0 = _o.BeatToX(h.clip.StartBeat);
                double x1 = _o.BeatToX(h.clip.StartBeat + h.clip.LengthBeats);
                int row = _o._tracks.IndexOf(h.track);
                double y = row >= 0 ? RowTop(row) : 0;
                double rh = row >= 0 ? RowHeight(row) : 64;

                if (pos.X >= x0 && pos.X <= x1 && pos.Y >= y && pos.Y <= y + rh)
                {
                    hoverT = h.track.Id;
                    hoverC = h.clip.ClipIndex;

                    double stripH = Math.Min(14, rh - 4);
                    bool topZone = pos.Y >= y + stripH - 2 && pos.Y <= y + stripH + 22;

                    if (!h.clip.IsMidi && topZone)
                    {
                        double xin = x0 + h.clip.FadeInBeats * _o._pixelsPerBeat;
                        double xout = x1 - h.clip.FadeOutBeats * _o._pixelsPerBeat;

                        hitFadeIn = (h.clip.FadeInBeats <= 0.001 && pos.X >= x0 - 4 && pos.X <= x0 + 16)
                                    || (h.clip.FadeInBeats > 0.001 && (Math.Abs(pos.X - xin) <= 10 || (pos.X >= x0 - 4 && pos.X <= xin + 6)));

                        if (!hitFadeIn)
                        {
                            hitFadeOut = (h.clip.FadeOutBeats <= 0.001 && pos.X >= x1 - 16 && pos.X <= x1 + 4)
                                         || (h.clip.FadeOutBeats > 0.001 && (Math.Abs(pos.X - xout) <= 10 || (pos.X >= xout - 6 && pos.X <= x1 + 4)));
                        }
                    }

                    if (hitFadeIn)
                    {
                        edge = Drag.FadeIn;
                        et = h.track.Id; ec = h.clip.ClipIndex;
                    }
                    else if (hitFadeOut)
                    {
                        edge = Drag.FadeOut;
                        et = h.track.Id; ec = h.clip.ClipIndex;
                    }
                    else
                    {
                        if (pos.X - x0 <= EdgePx) edge = Drag.TrimL;
                        else if (x1 - pos.X <= EdgePx) edge = Drag.TrimR;
                        if (edge != Drag.None) { et = h.track.Id; ec = h.clip.ClipIndex; }
                    }
                }
            }

            SetResizeCursor(edge != Drag.None, hitFadeIn || hitFadeOut);

            if (edge == _hoverEdge && et == _hoverEdgeTrack && ec == _hoverEdgeClip
                && hoverT == _hoverClipTrack && hoverC == _hoverClipIndex
                && hitFadeIn == _hoverFadeInHandle && hitFadeOut == _hoverFadeOutHandle)
                return;

            _hoverEdge = edge;
            _hoverEdgeTrack = et;
            _hoverEdgeClip = ec;
            _hoverClipTrack = hoverT;
            _hoverClipIndex = hoverC;
            _hoverFadeInHandle = hitFadeIn;
            _hoverFadeOutHandle = hitFadeOut;
            InvalidateVisual();
        }

        private void ClearEdgeHover()
        {
            SetResizeCursor(false);
            if (_hoverEdge == Drag.None && _hoverEdgeTrack == -1 && _hoverClipTrack == -1) return;
            _hoverEdge = Drag.None; _hoverEdgeTrack = -1; _hoverEdgeClip = -1;
            _hoverClipTrack = -1; _hoverClipIndex = -1;
            _hoverFadeInHandle = false; _hoverFadeOutHandle = false;
            InvalidateVisual();
        }

        private void SetResizeCursor(bool on, bool isHand = false)
        {
            if (on)
            {
                Cursor = isHand ? HandCursor : ResizeCursor;
            }
            else
            {
                Cursor = _o.CurrentTool switch
                {
                    ArrangementTool.Pencil => new Cursor(StandardCursorType.Cross),
                    ArrangementTool.Cut => new Cursor(StandardCursorType.Cross),
                    ArrangementTool.Eraser => new Cursor(StandardCursorType.No),
                    _ => ArrowCursor,
                };
            }
        }

        private void AutoPointerMoved(PointerEventArgs e)
        {
            var pos = e.GetPosition(this);
            _lastAutoPointerPos = pos;
            if (_rangeTrack is not null)
            {
                double beat = Math.Max(0, _o.Snap(_o._scrollBeats + pos.X / _o._pixelsPerBeat));
                _o.SetAutoSelection(_rangeTrack, _rangeAnchor, beat);
                return;
            }
            if (_bendLeft is not null && _bendTrack is not null) { AutoBendMoved(pos); return; }
            if (_segDragLeft is not null && _segDragRight is not null && _segDragTrack is not null)
            {
                var (min, max) = _segDragHit.IsSubLane ? _o.AutoRange(_segDragHit.SubLane!) : _o.AutoRange(_segDragHit.Track);
                double pad = _segDragHit.IsSubLane ? 6 : 8;
                double top = _segDragHit.Top + pad, bot = _segDragHit.Top + _segDragHit.Height - pad;
                double dy = _segDragStartPosY - pos.Y;
                float deltaVal = (float)(dy / Math.Max(1.0, bot - top) * (max - min));
                if ((e.KeyModifiers & KeyModifiers.Shift) != 0) deltaVal *= 0.25f;

                _segDragLeft.Value = Math.Clamp(_segDragStartValL + deltaVal, min, max);
                _segDragRight.Value = Math.Clamp(_segDragStartValR + deltaVal, min, max);
                CommitAutoDuringDrag(_segDragTrack, _segDragSubLane);
                InvalidateVisual();
                return;
            }
            if (_autoDrag is null || _autoDragTrack is null) { UpdateHover(pos); return; }

            // Time: snapped (Alt bypasses), clamped between neighbour points — no reordering
            double cand = Math.Max(0, _o.SnapMaybe(_o._scrollBeats + pos.X / _o._pixelsPerBeat, e.KeyModifiers));
            _autoDrag.Beat = Math.Clamp(cand, _autoDragLo, _autoDragHi);
            // Value: free, but Shift = fine-mode (quarter sensitivity)
            float full = LaneYToValue(_autoDragHit, pos.Y);
            _autoDrag.Value = (e.KeyModifiers & KeyModifiers.Shift) != 0
                ? _autoDragStartVal + (full - _autoDragStartVal) * 0.25f
                : full;
            CommitAutoDuringDrag(_autoDragTrack, _autoDragSubLane);
            InvalidateVisual();
        }

        // First push of a drag checkpoints undo; the rest are raw, so a whole drag is one undo step.
        private void CommitAutoDuringDrag(TrackVM t, AutoSubLaneVM? sl = null)
        {
            if (sl != null)
            {
                _o.CommitSubLane(t, sl, live: _autoLiveStarted);
                _autoLiveStarted = true;
            }
            else
            {
                if (!_autoLiveStarted) { _o.CommitAuto(t); _autoLiveStarted = true; }
                else _o.CommitAutoLive(t);
            }
        }

        private void AutoPointerReleased(PointerReleasedEventArgs e)
        {
            if (_rangeTrack is not null)
            {
                // A zero-width drag (Shift-click, no move) is a deselect, not a selection.
                if (_o._autoSelEnd - _o._autoSelStart <= 1e-6) _o.ClearAutoSelection();
                _rangeTrack = null;
                e.Pointer.Capture(null);
                return;
            }
            if (_bendLeft is not null && _bendTrack is not null)
            {
                if (_bendSubLane != null) _o.CommitSubLane(_bendTrack, _bendSubLane, live: false);
                else { if (_autoLiveStarted) _o.CommitAutoLive(_bendTrack); else _o.CommitAuto(_bendTrack); }
                _autoLiveStarted = false;
                _bendLeft = _bendRight = null; _bendTrack = null; _bendSubLane = null;
                e.Pointer.Capture(null);
                InvalidateVisual();
                return;
            }
            if (_segDragLeft is not null && _segDragTrack is not null)
            {
                if (_segDragSubLane != null) _o.CommitSubLane(_segDragTrack, _segDragSubLane, live: false);
                else { if (_autoLiveStarted) _o.CommitAutoLive(_segDragTrack); else _o.CommitAuto(_segDragTrack); }
                _autoLiveStarted = false;
                _segDragLeft = _segDragRight = null; _segDragTrack = null; _segDragSubLane = null;
                e.Pointer.Capture(null);
                InvalidateVisual();
                return;
            }
            if (_autoDrag is null || _autoDragTrack is null) return;
            if (_autoDragSubLane != null) _o.CommitSubLane(_autoDragTrack, _autoDragSubLane, live: false);
            else { if (_autoLiveStarted) _o.CommitAutoLive(_autoDragTrack); else _o.CommitAuto(_autoDragTrack); }
            _autoLiveStarted = false;
            _autoDrag = null;
            _autoDragTrack = null;
            _autoDragSubLane = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }

        private void ShowAutoPointMenu(TrackVM t, AutoPt pt, Point pos, AutoSubLaneVM? sl = null)
        {
            var target = sl?.Target ?? t.AutoTarget;
            float defVal = _o.GetAutoDefaultValue(target);
            string defStr = ArrangementView.FormatAutoValue(target, defVal);
            string curStr = ArrangementView.FormatAutoValue(target, pt.Value);

            var flyout = new MenuFlyout();

            var resetItem = new MenuItem { Header = $"Reset to Default ({defStr})" };
            resetItem.Click += (_, _) =>
            {
                pt.Value = defVal;
                if (sl != null) _o.CommitSubLane(t, sl);
                else _o.CommitAuto(t);
                InvalidateVisual();
            };
            flyout.Items.Add(resetItem);

            var editItem = new MenuItem { Header = $"Edit Value… ({curStr})" };
            editItem.Click += (_, _) =>
            {
                PromptEditAutoValue(target, pt.Value, pos, newVal =>
                {
                    pt.Value = newVal;
                    if (sl != null) _o.CommitSubLane(t, sl);
                    else _o.CommitAuto(t);
                    InvalidateVisual();
                });
            };
            flyout.Items.Add(editItem);

            flyout.Items.Add(new Separator());

            var delItem = new MenuItem { Header = "Delete Point" };
            delItem.Click += (_, _) =>
            {
                if (sl != null) { sl.Points.Remove(pt); _o.CommitSubLane(t, sl); }
                else { t.AutoPoints.Remove(pt); _o.CommitAuto(t); }
                InvalidateVisual();
            };
            flyout.Items.Add(delItem);

            flyout.ShowAt(this, showAtPointer: true);
        }

        private void PromptEditAutoValue(AutomationTarget target, float currentVal, Point pos, Action<float> commit)
        {
            var panel = new StackPanel { Spacing = 6, Margin = new Thickness(10), Width = 220 };
            var header = new TextBlock
            {
                Text = "Edit Automation Value",
                FontFamily = KnoxPalette.UiFont,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White
            };
            string rawStr = currentVal.ToString("0.####", CultureInfo.InvariantCulture);
            string fmtStr = ArrangementView.FormatAutoValue(target, currentVal);

            var box = new TextBox
            {
                Text = rawStr,
                PlaceholderText = fmtStr,
                FontFamily = KnoxPalette.UiFont,
                FontSize = 12,
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4)
            };

            var hint = new TextBlock
            {
                Text = target == AutomationTarget.Volume
                    ? "e.g. 0.0523, 0.0 dB, -6 dB, 1.0"
                    : target == AutomationTarget.Pan
                    ? "e.g. Center, 50% L, 75% R, -0.5, 0.25"
                    : "e.g. 0.75, 50%, 0.125",
                FontFamily = KnoxPalette.UiFont,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.Parse("#888888"))
            };

            panel.Children.Add(header);
            panel.Children.Add(box);
            panel.Children.Add(hint);

            var flyout = new Flyout
            {
                Content = panel,
                Placement = PlacementMode.Pointer
            };

            void Apply()
            {
                if (ArrangementView.TryParseAutoValue(target, box.Text ?? "", out float val))
                {
                    commit(val);
                    flyout.Hide();
                }
            }

            box.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter) { Apply(); ke.Handled = true; }
                else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
            };

            flyout.ShowAt(this);
            Dispatcher.UIThread.Post(() => { box.SelectAll(); box.Focus(); }, DispatcherPriority.Input);
        }

        // A small inline rename popup (Enter commits, Esc cancels) anchored at the pointer.
        private void PromptRename(string current, Action<string> commit)
        {
            var box = new TextBox { Text = current, Width = 170, FontSize = 12 };
            var flyout = new Flyout { Content = box, Placement = PlacementMode.Pointer };
            box.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter) { commit(box.Text ?? ""); flyout.Hide(); ke.Handled = true; }
                else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
            };
            flyout.ShowAt(this);
            Dispatcher.UIThread.Post(() => { box.SelectAll(); box.Focus(); }, DispatcherPriority.Input);
        }

        // Automation-mode right-click: copy/cut/delete the selected time range and paste the
        // range clipboard at the cursor (Phase 2).
        private void ShowAutoMenu(TrackVM t, double beat, AutoSubLaneVM? sl = null)
        {
            var flyout = new MenuFlyout();

            if (sl != null)
            {
                var changeTgt = new MenuItem { Header = "Change Target…" };
                changeTgt.Click += (_, _) => _o.ShowSubLaneTargetMenu(this, t, sl);
                flyout.Items.Add(changeTgt);

                var clearLane = new MenuItem { Header = "Clear Lane Points" };
                clearLane.Click += (_, _) => { sl.Points.Clear(); _o.CommitSubLane(t, sl); InvalidateVisual(); };
                flyout.Items.Add(clearLane);

                var removeLane = new MenuItem { Header = "Remove Sub-Lane" };
                removeLane.Click += (_, _) => _o.RemoveAutoSubLane(t, sl);
                flyout.Items.Add(removeLane);
                flyout.Items.Add(new Separator());
            }

            bool hasSel = _o.HasAutoSelection && ReferenceEquals(_o._autoSelTrack, t);
            var copy = new MenuItem { Header = "Copy Automation", IsEnabled = hasSel };
            copy.Click += (_, _) => _o.CopyAutoSelection();
            var cut = new MenuItem { Header = "Cut Automation", IsEnabled = hasSel };
            cut.Click += (_, _) => _o.CutAutoSelection();
            var del = new MenuItem { Header = "Delete Automation", IsEnabled = hasSel };
            del.Click += (_, _) => _o.DeleteAutoSelection();
            var paste = new MenuItem { Header = "Paste Automation", IsEnabled = _o.HasAutoClip };
            paste.Click += (_, _) => _o.PasteAutoAt(t, _o.Snap(beat));
            flyout.Items.Add(copy);
            flyout.Items.Add(cut);
            flyout.Items.Add(del);
            flyout.Items.Add(new Separator());
            flyout.Items.Add(paste);

            if (sl == null)
            {
                flyout.Items.Add(new Separator());
                var addLane = new MenuItem { Header = "Add Automation Sub-Lane" };
                addLane.Click += (_, _) => _o.AddAutoSubLane(t);
                flyout.Items.Add(addLane);
            }

            flyout.ShowAt(this, showAtPointer: true);
        }

        // Right-click on empty lane space: insert MIDI clip or paste from clipboard.
        private void ShowLaneMenu(Point pos, double beat)
        {
            int ti = _o.GetTrackIndexAtY(pos.Y);
            if (ti < 0 || ti >= _o._tracks.Count) return;
            var trk = _o._tracks[ti];
            if (trk.IsGroup || trk.IsReturn) return;
            int trackId = trk.Id;
            double at = _o.Snap(beat);
            var flyout = new MenuFlyout();

            var insertMidi = new MenuItem { Header = "Insert MIDI Clip" };
            insertMidi.Click += (_, _) => _o.AddMidiClipAt(trackId, at);
            flyout.Items.Add(insertMidi);

            if (_o.HasClipClipboard)
            {
                var paste = new MenuItem { Header = "Paste" };
                paste.Click += (_, _) => _o.PasteClipboardAt(trackId, at);
                flyout.Items.Add(paste);
            }
            flyout.ShowAt(this, showAtPointer: true);
        }

        private void ShowClipMenu(int trackId, ClipVM clip, double beat)
        {
            double at = _o.Snap(beat);
            int idx = clip.ClipIndex;
            // Copy/Cut/Duplicate/Delete act on the whole selection when the clicked clip is
            // part of a multi-selection; otherwise they collapse to just this clip.
            void EnsureSelected() { if (!_o.IsSelected(trackId, idx)) _o.Select(trackId, idx); }
            bool inGroup = _o.IsSelected(trackId, idx) && _o.Selection.Count > 1;

            var flyout = new MenuFlyout();
            var split = new MenuItem { Header = "Split here" };
            split.Click += (_, _) => { _o._engine?.SplitClip(trackId, idx, at); _o.Refresh(); };
            var dup = new MenuItem { Header = inGroup ? "Duplicate selection" : "Duplicate" };
            dup.Click += (_, _) => { EnsureSelected(); _o.DuplicateSelectedClip(); };
            var del = new MenuItem { Header = inGroup ? "Delete selection" : "Delete" };
            del.Click += (_, _) =>
            {
                if (inGroup) _o.DeleteSelectedClips();
                else { _o._engine?.DeleteClip(trackId, idx); _o.Select(-1, -1); _o.Refresh(); }
            };
            // Clip deactivate (key 0): header reflects the clicked clip's current state.
            var deact = new MenuItem
            {
                Header = clip.Active ? (inGroup ? "Deactivate selection" : "Deactivate clip")
                                     : (inGroup ? "Activate selection" : "Activate clip"),
                InputGesture = new KeyGesture(Key.D0),
            };
            deact.Click += (_, _) => { EnsureSelected(); _o.ToggleSelectedClipsActive(); };
            var copy = new MenuItem { Header = inGroup ? "Copy selection" : "Copy" };
            copy.Click += (_, _) => { EnsureSelected(); _o.CopySelectedClip(); };
            var cut = new MenuItem { Header = inGroup ? "Cut selection" : "Cut" };
            cut.Click += (_, _) => { EnsureSelected(); _o.CutSelectedClip(); };
            var paste = new MenuItem { Header = "Paste", IsEnabled = _o.HasClipClipboard };
            paste.Click += (_, _) => _o.PasteClipboardAt(trackId, at);
            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += (_, _) => PromptRename(clip.Name, s => { _o._engine?.SetClipName(trackId, idx, s); _o.Refresh(); });
            flyout.Items.Add(rename);
            flyout.Items.Add(copy);
            flyout.Items.Add(cut);
            flyout.Items.Add(paste);
            flyout.Items.Add(new Separator());
            flyout.Items.Add(split);
            flyout.Items.Add(dup);
            flyout.Items.Add(deact);
            flyout.Items.Add(del);

            // Loop the current selection (or just this clip if nothing is selected).
            var loop = new MenuItem { Header = "Loop selection" };
            loop.Click += (_, _) =>
            {
                if (!_o.LoopSelection())
                    _o.SetLoopRegion(clip.StartBeat, clip.StartBeat + clip.LengthBeats);
            };
            flyout.Items.Add(new Separator());
            flyout.Items.Add(loop);

            // Audio clip tools: Reverse, Normalize, Invert Phase, De-Click Fades & MIDI Conversion
            if (!clip.IsMidi)
            {
                var rev = new MenuItem { Header = "Reverse Clip" };
                rev.Click += (_, _) => { _o._engine?.ReverseAudioClip(trackId, idx); _o.Refresh(); };

                var norm = new MenuItem { Header = "Normalize" };
                void AddNorm(string title, double db)
                {
                    var nmi = new MenuItem { Header = title };
                    nmi.Click += (_, _) =>
                    {
                        var peaks = new float[1024 * 2];
                        int n = _o._engine?.GetClipPeaks(trackId, idx, peaks, 1024) ?? 0;
                        float maxPeak = 0.0001f;
                        for (int i = 0; i < n * 2; i++)
                        {
                            float abs = Math.Abs(peaks[i]);
                            if (abs > maxPeak) maxPeak = abs;
                        }
                        double targetLin = AudioMath.DbToLin(db);
                        float targetGain = (float)Math.Clamp(targetLin / maxPeak, 0.01f, 20.0f);
                        _o._engine?.SetClipGain(trackId, idx, targetGain);
                        _o.Refresh();
                    };
                    norm.Items.Add(nmi);
                }
                AddNorm("Peak Normalize (0.0 dBFS)", 0.0);
                AddNorm("True Peak Safe (-0.1 dB)", -0.1);
                AddNorm("Streaming Standard (-1.0 dB)", -1.0);
                AddNorm("Half Level (-6.0 dB)", -6.0);

                var invPhase = new MenuItem { Header = "Invert Phase (Polarity)" };
                invPhase.Click += (_, _) =>
                {
                    if (_o._engine != null && _o._engine.TryGetAudioClipInfo(trackId, idx, out var ai))
                    {
                        _o._engine.SetClipGain(trackId, idx, -ai.Gain);
                        _o.Refresh();
                    }
                };

                var declick = new MenuItem { Header = "Apply Quick De-Click Fades" };
                declick.Click += (_, _) =>
                {
                    if (clip.LengthBeats > 0)
                    {
                        double fadeLen = Math.Min(0.25, clip.LengthBeats * 0.1);
                        var pts = new[]
                        {
                            new AutomationPoint(0.0, 0.0f),
                            new AutomationPoint(fadeLen, 1.0f),
                            new AutomationPoint(Math.Max(fadeLen, clip.LengthBeats - fadeLen), 1.0f),
                            new AutomationPoint(clip.LengthBeats, 0.0f)
                        };
                        _o._engine?.SetClipVolumeEnvelope(trackId, idx, pts);
                        _o.Refresh();
                    }
                };

                flyout.Items.Add(new Separator());
                flyout.Items.Add(rev);
                flyout.Items.Add(norm);
                flyout.Items.Add(invPhase);
                flyout.Items.Add(declick);

                var convert = new MenuItem { Header = "Convert" };
                MenuItem ConvItem(string header, ClipConvertMode mode, bool enabled)
                {
                    var mi = new MenuItem { Header = header, IsEnabled = enabled };
                    if (enabled) mi.Click += (_, _) => _o.ConvertClipRequested?.Invoke(trackId, idx, mode);
                    return mi;
                }
                convert.Items.Add(ConvItem("Convert Melody to New MIDI Track", ClipConvertMode.Melody, true));
                convert.Items.Add(ConvItem("Convert Harmony to New MIDI Track", ClipConvertMode.Harmony, true));
                convert.Items.Add(ConvItem("Convert Drums to New MIDI Track", ClipConvertMode.Drums, true));
                convert.Items.Add(ConvItem("Slice to New MIDI Track", ClipConvertMode.Slice, true));
                flyout.Items.Add(convert);
            }

            flyout.ShowAt(this, showAtPointer: true);
        }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;

            // 1. Paint alternating lane stripes and horizontal dividers for existing tracks
            double curY = 0;
            for (int i = 0; i < _o._tracks.Count; i++)
            {
                double rh = RowHeight(i);
                ctx.FillRectangle((i & 1) == 0 ? LaneBgA : LaneBgB, new Rect(0, curY, w, rh));
                double divY = Math.Floor(curY + rh) + 0.5;
                ctx.DrawLine(TrackDividerPen, new Point(0, divY), new Point(w, divY));
                curY += rh;
            }

            // 2. Paint extra empty lane rows below tracks up to the full height of the arrangement view
            int extraRow = _o._tracks.Count;
            while (curY < h)
            {
                double rh = ArrangementView.RowHeight;
                ctx.FillRectangle((extraRow & 1) == 0 ? LaneBgA : LaneBgB, new Rect(0, curY, w, rh));
                double divY = Math.Floor(curY + rh) + 0.5;
                ctx.DrawLine(TrackDividerPen, new Point(0, divY), new Point(w, divY));
                curY += rh;
                extraRow++;
            }

            if (_bgBitmap != null && _bgOpacity > 0.001)
            {
                using (ctx.PushOpacity(_bgOpacity))
                {
                    double bw = _bgBitmap.PixelSize.Width, bh = _bgBitmap.PixelSize.Height;
                    switch (BgImageMode)
                    {
                        case "Center":
                            if (bw > 0 && bh > 0)
                            {
                                double dx = (w - bw) / 2.0;
                                double dy = (h - bh) / 2.0;
                                ctx.DrawImage(_bgBitmap, new Rect(dx, dy, bw, bh));
                            }
                            break;
                        case "Grid":
                        case "Tile":
                            if (bw > 0 && bh > 0)
                            {
                                using (ctx.PushClip(new Rect(0, 0, w, h)))
                                {
                                    for (double x = 0; x < w; x += bw)
                                    {
                                        for (double y = 0; y < h; y += bh)
                                        {
                                            double rw = Math.Min(bw, w - x);
                                            double rh = Math.Min(bh, h - y);
                                            ctx.DrawImage(_bgBitmap, new Rect(0, 0, rw, rh), new Rect(x, y, rw, rh));
                                        }
                                    }
                                }
                            }
                            break;
                        case "Fit":
                            if (bw > 0 && bh > 0)
                            {
                                double scale = Math.Min(w / bw, h / bh);
                                double dw = bw * scale, dh = bh * scale;
                                double dx = (w - dw) / 2.0, dy = (h - dh) / 2.0;
                                ctx.DrawImage(_bgBitmap, new Rect(dx, dy, dw, dh));
                            }
                            break;
                        case "Fill":
                        case "Cover":
                            if (bw > 0 && bh > 0)
                            {
                                double scale = Math.Max(w / bw, h / bh);
                                double dw = bw * scale, dh = bh * scale;
                                double dx = (w - dw) / 2.0, dy = (h - dh) / 2.0;
                                using (ctx.PushClip(new Rect(0, 0, w, h)))
                                {
                                    ctx.DrawImage(_bgBitmap, new Rect(dx, dy, dw, dh));
                                }
                            }
                            break;
                        case "Stretch":
                        default:
                            ctx.DrawImage(_bgBitmap, new Rect(0, 0, w, h));
                            break;
                    }
                }
            }

            var (rLo, rHi) = _o.VisibleRowRange();

            // Selected-track highlight: a soft brass wash + bright top/bottom edges
            // so the selection reads across the whole grid, not just the header.
            for (int i = rLo; i <= rHi; i++)
            {
                if (_o._tracks[i].Id != _o.SelTrackId) continue;
                double sy = RowTop(i);
                ctx.FillRectangle(SelWash, new Rect(0, sy, w, RowHeight(i)));
            }

            // Browser drag-over: glow the lane the drop would land on (future state).
            if (_o.DropTrackIndex >= 0 && _o.DropTrackIndex < _o._tracks.Count)
            {
                double dy = RowTop(_o.DropTrackIndex);
                var r = new Rect(0, dy, w, RowHeight(_o.DropTrackIndex));
                ctx.FillRectangle(DropWash, r);
                ctx.DrawRectangle(null, new Pen(DropEdge, 1.5), r);
            }

            // Dynamic vertical grid: Bars always, beats when zoomed, and sub-divisions matching active snap
            double ppb = _o._pixelsPerBeat;
            double snap = _o.EffectiveSnapBeats > 0 ? _o.EffectiveSnapBeats : 0.0;
            double step = snap;
            if (step <= 0)
            {
                // Adaptive step ladder down to 1/128 beat (0.0078125 beats)
                double[] ladder = { 0.0078125, 0.015625, 0.03125, 0.0625, 0.125, 0.25, 0.5, 1.0, 2.0, 4.0 };
                step = 4.0;
                foreach (var s in ladder)
                {
                    if (s * ppb >= 10) { step = s; break; }
                }
            }
            else
            {
                while (step * ppb < 10 && step < 1.0)
                {
                    step *= 2.0;
                }
            }
            if (step >= 1.0 && ppb < 12) step = 1.0;

            double firstStep = Math.Floor(_o._scrollBeats / step) * step;
            for (double beat = firstStep; ; beat += step)
            {
                double x = _o.BeatToX(beat);
                if (x > w) break;
                if (x < 0) continue;

                double barDiff = Math.Abs(beat % _o._beatsPerBar);
                bool isBar = barDiff < 1e-4 || Math.Abs(barDiff - _o._beatsPerBar) < 1e-4;

                double beatDiff = Math.Abs(beat % 1.0);
                bool isBeat = beatDiff < 1e-4 || Math.Abs(beatDiff - 1.0) < 1e-4;

                double snapX = Math.Floor(x) + 0.5;
                if (isBar)
                {
                    ctx.DrawLine(BarPen, new Point(snapX, 0), new Point(snapX, h));
                }
                else if (isBeat)
                {
                    if (_o._pixelsPerBeat >= 12 || snap >= 1.0)
                        ctx.DrawLine(BeatPen, new Point(snapX, 0), new Point(snapX, h));
                }
                else
                {
                    if (step * _o._pixelsPerBeat >= 10)
                        ctx.DrawLine(SnapPen, new Point(snapX, 0), new Point(snapX, h));
                }
            }

            // Clips (fill + border + 14px name strip, all in the track colour). A clip
            // being dragged to another track is hidden here and shown as a translucent
            // preview on the destination lane (below).
            for (int i = rLo; i <= rHi; i++)
            {
                double y = RowTop(i);
                double rh = RowHeight(i);
                int tid = _o._tracks[i].Id;
                // Group rows carry no clips of their own: preview their descendants instead
                // (stacked mini-clips collapsed, a thin colour strip expanded).
                if (_o._tracks[i].IsGroup)
                {
                    DrawGroupLane(ctx, _o._tracks[i], y, rh, w, _o.IsGroupCollapsed(tid));
                    continue;
                }
                foreach (var c in _o._tracks[i].Clips)
                {
                    if (IsDraggingCrossTrack(tid, c.ClipIndex)) continue;
                    // Highlight the edge a resize would grab (hover), or the edge actively being trimmed.
                    var edgeHi = Drag.None;
                    if (_hoverEdge != Drag.None && _hoverEdgeTrack == tid && _hoverEdgeClip == c.ClipIndex) edgeHi = _hoverEdge;
                    else if ((_drag == Drag.TrimL || _drag == Drag.TrimR) && _dragTrackId == tid && _dragClipIndex == c.ClipIndex) edgeHi = _drag;
                    DrawClipBody(ctx, _o._tracks[i].ColorIndex, _o._tracks[i].Name, c, y, rh, _o.IsSelected(tid, c.ClipIndex), edgeHi);
                }
            }

            // In-progress audio take (M-fix): audio clips only materialise on stop,
            // so draw a growing red region on the armed track from the take start to
            // the playhead — otherwise recording gives no feedback on the timeline.
            if (_o._engine is { } re)
            {
                int rt = re.AudioRecordTrackId;
                if (rt > 0)
                    for (int i = rLo; i <= rHi; i++)
                    {
                        if (_o._tracks[i].Id != rt) continue;
                        double ry = RowTop(i);
                        double rh = RowHeight(i);
                        // Loop recording punches the loop region → draw a stable box over it.
                        // Otherwise the box grows by the actual captured length (monotonic),
                        // not the playhead.
                        double rx0, rx1;
                        if (re.LoopEnabled)
                        {
                            rx0 = _o.BeatToX(re.LoopStart);
                            rx1 = _o.BeatToX(re.LoopEnd);
                        }
                        else
                        {
                            rx0 = _o.BeatToX(re.AudioRecordStartBeat);
                            rx1 = Math.Max(rx0, _o.BeatToX(re.AudioRecordStartBeat + re.AudioRecordLengthBeats));
                        }
                        var rr = new Rect(rx0, ry + 2, Math.Max(2, rx1 - rx0), rh - 4);
                        ctx.DrawRectangle(RecFill, RecBorder, rr, 4, 4);
                        // Live waveform of what's being captured, so the take shows real
                        // signal rather than an empty box (clips only materialise on stop).
                        DrawRecPeaks(ctx, re, rr);
                        if (rr.Width > 34)
                        {
                            var ft = new FormattedText("● REC", CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, new Typeface(KnoxPalette.UiFont), 9, RecText);
                            ctx.DrawText(ft, new Point(rr.X + 5, rr.Y + 3));
                        }
                        break;
                    }
            }

            // Automation overlay (M9-A3): scrim + envelope + target pill per row.
            if (_o._automationMode) DrawAutomation(ctx, w);

            // Cross-track drag: the dragged clips are hidden on their source lanes
            // (above) and previewed translucently on the destination lane here.
            if (_groupMove is not null && _moveRowDelta != 0)
                using (ctx.PushOpacity(0.5))
                    foreach (var m in _groupMove)
                    {
                        int destRow = m.origRow + _moveRowDelta;
                        if (destRow < 0 || destRow >= _o._tracks.Count) continue;
                        var dest = _o._tracks[destRow];
                        DrawClipBody(ctx, dest.ColorIndex, dest.Name, m.vm, RowTop(destRow), RowHeight(destRow), true);
                    }

            // Clip marquee (plain drag on empty space).
            if (_marquee is { } mq) ctx.DrawRectangle(MarqueeFill, MarqueePen, mq);

            // Time-range selection: a band across its covered rows (req 1.2).
            if (_o.HasTimeSelection)
            {
                double sx = _o.BeatToX(_o._timeSelStart), ex = _o.BeatToX(_o._timeSelEnd);
                double ty = RowTop(_o._timeSelRowLo);
                double th = RowTop(_o._timeSelRowHi) + RowHeight(_o._timeSelRowHi) - ty;
                double cx0 = Math.Max(0, sx), cx1 = Math.Min(w, ex);
                if (cx1 > cx0) ctx.FillRectangle(MarqueeFill, new Rect(cx0, ty, cx1 - cx0, th));
                if (sx >= 0 && sx <= w) ctx.DrawLine(MarqueePen, new Point(sx, ty), new Point(sx, ty + th));
            }

            // Drag position tooltip: bar.beat of the grabbed clip's start, edge, or fade duration
            if (_drag != Drag.None)
            {
                string text = _drag switch
                {
                    Drag.FadeIn => $"Fade In: {_dragClip?.FadeInBeats ?? 0:0.00} beats",
                    Drag.FadeOut => $"Fade Out: {_dragClip?.FadeOutBeats ?? 0:0.00} beats",
                    Drag.TrimR => _o.FormatBarBeat((_dragClip?.StartBeat ?? 0) + (_dragClip?.LengthBeats ?? 0)),
                    Drag.TrimL => _o.FormatBarBeat(_dragClip?.StartBeat ?? 0),
                    _ => _o.FormatBarBeat(_pendingClip?.StartBeat ?? 0),
                };
                var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(KnoxPalette.UiFont), 10, TooltipText);
                double tx = Math.Clamp(_lastPos.X + 12, 6, Math.Max(6, w - ft.Width - 10));
                double ty = Math.Max(4, _lastPos.Y - 24);
                ctx.DrawRectangle(TooltipBg, null, new Rect(tx - 5, ty - 3, ft.Width + 10, ft.Height + 6), 3, 3);
                ctx.DrawText(ft, new Point(tx, ty));
            }

            // NB: the loop band + playhead are drawn on LaneOverlayControl (a hit-transparent
            // layer above the lanes) so the 30 Hz transport tick doesn't repaint this whole
            // clip/waveform surface — only the overlay's few strokes.
        }

        // Adds one axis-aligned rectangle as its own closed figure. Batching a clip's many
        // columns/notes into a single StreamGeometry drawn once cuts thousands of per-rect
        // draw commands per clip down to one DrawGeometry call.
        private static void AddRect(StreamGeometryContext g, double x, double y, double w, double h)
        {
            g.BeginFigure(new Point(x, y), true);
            g.LineTo(new Point(x + w, y));
            g.LineTo(new Point(x + w, y + h));
            g.LineTo(new Point(x, y + h));
            g.EndFigure(true);
        }

        private void DrawNotes(DrawingContext ctx, ClipVM c, Rect r, IBrush brush)
        {
            if (c.Notes is null || c.Notes.Length == 0 || c.LengthBeats <= 0 || r.Height <= 0) return;
            // Auto-range to the pitches actually present: a fixed 5-octave range
            // needs far more height than one lane row, so mapping it with a min
            // bar height pushes high notes above the clip and into the row above.
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var n in c.Notes) { lo = Math.Min(lo, n.Pitch); hi = Math.Max(hi, n.Pitch); }
            int span = Math.Max(1, hi - lo + 1);
            double noteH = Math.Clamp(r.Height / span, 1, 4);
            using var _ = ctx.PushClip(r);   // never bleed into adjacent lanes
            var geo = new StreamGeometry();
            using (var g = geo.Open())
                foreach (var n in c.Notes)
                {
                    double nx = r.X + (n.StartBeat / c.LengthBeats) * r.Width;
                    double nw = Math.Max(2, (n.LengthBeats / c.LengthBeats) * r.Width);
                    double ny = r.Bottom - (n.Pitch - lo + 1) * noteH;
                    double ww = Math.Min(nw, r.Right - nx);
                    if (ww > 0) AddRect(g, nx, ny, ww, noteH);
                }
            ctx.DrawGeometry(brush, null, geo);
        }

        // Scratch buffer for live capture peaks — reused each frame (no per-render alloc).
        private readonly float[] _recPeaks = new float[512 * 2];
        private readonly float[] _viewportPeaks = new float[4096 * 2];

        // Live waveform for the in-progress take: fetch min/max peaks over the capture
        // buffer sized to the region width, and draw them like a normal clip waveform.
        private void DrawRecPeaks(DrawingContext ctx, IAudioEngine eng, Rect r)
        {
            if (r.Width < 2 || r.Height <= 4) return;
            int want = Math.Clamp((int)r.Width, 1, 512);
            int n = eng.AudioRecordPeaks(_recPeaks, want);
            if (n <= 0) return;
            double mid = Math.Floor(r.Y + r.Height / 2) + 0.5;
            double amp = Math.Max(1.0, (r.Height - 6) / 2.0);

            var zeroLinePen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
            ctx.DrawLine(zeroLinePen, new Point(r.X, mid), new Point(r.Right, mid));

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(r.X, mid), true);
                for (int i = 0; i < n; i++)
                {
                    double fx = r.X + (i / (double)Math.Max(1, n - 1)) * r.Width;
                    float max = _recPeaks[i * 2 + 1];
                    double yTop = Math.Min(mid - 0.5, mid - max * amp);
                    g.LineTo(new Point(fx, yTop));
                }
                for (int i = n - 1; i >= 0; i--)
                {
                    double fx = r.X + (i / (double)Math.Max(1, n - 1)) * r.Width;
                    float min = _recPeaks[i * 2];
                    double yBot = Math.Max(mid + 0.5, mid - min * amp);
                    g.LineTo(new Point(fx, yBot));
                }
                g.EndFigure(true);
            }
            var outlinePen = new Pen(RecWave, 1.0);
            ctx.DrawGeometry(RecWave, outlinePen, geo);
        }

        private bool IsDraggingCrossTrack(int track, int clip)
            => _groupMove is not null && _moveRowDelta != 0 && _groupMove.Any(m => m.track == track && m.clip == clip);

        // Draws one clip (fill + border + name strip + notes/waveform) at row-y with
        // the given track colour. Reused for the live cross-track drag preview.
        // Group lane preview: collapsed → each descendant clip as a mini-clip stacked into its
        // own sub-lane (child colour), so starts/ends read at a glance; expanded → a thin strip
        // in the group colour over each descendant clip's span, marking content boundaries.
        private void DrawGroupLane(DrawingContext ctx, ArrangementView.TrackVM g, double y, double rowH, double w, bool collapsed)
        {
            if (g.GroupMini.Count == 0) return;
            if (collapsed)
            {
                int slots = Math.Max(1, g.GroupSlotCount);
                const double padTop = 7, padBot = 7;
                double innerH = Math.Max(10, rowH - padTop - padBot);
                double laneH = innerH / slots;
                double barH = Math.Max(3, laneH - 2);
                foreach (var m in g.GroupMini)
                {
                    double x0 = _o.BeatToX(m.Start), x1 = _o.BeatToX(m.Start + m.Length);
                    if (x1 < 0 || x0 > w || m.Length <= 0) continue;
                    var (fill, border, _, _) = ClipColors(m.ColorIndex);
                    var r = new Rect(x0, y + padTop + m.Slot * laneH, Math.Max(2, x1 - x0), barH);
                    ctx.DrawRectangle(fill, border, r, 2, 2);
                }
            }
            else
            {
                var col = TrackColorForIndex(g.ColorIndex);
                var fill = Alpha(col, 0.55);
                var border = new Pen(Alpha(col, 0.85), 1);
                const double stripH = 6;
                double sy = y + (rowH - stripH) / 2;
                foreach (var m in g.GroupMini)
                {
                    double x0 = _o.BeatToX(m.Start), x1 = _o.BeatToX(m.Start + m.Length);
                    if (x1 < 0 || x0 > w || m.Length <= 0) continue;
                    var r = new Rect(x0, sy, Math.Max(2, x1 - x0), stripH);
                    ctx.DrawRectangle(fill, border, r, 3, 3);
                }
            }
        }

        private void DrawClipBody(DrawingContext ctx, int colorIndex, string label, ClipVM c, double y, double rowH, bool selected, Drag edgeHi = Drag.None)
        {
            double w = Bounds.Width;
            double x0 = Math.Floor(_o.BeatToX(c.StartBeat));
            double x1 = Math.Floor(_o.BeatToX(c.StartBeat + c.LengthBeats));
            if (x1 < 0 || x0 > w || c.LengthBeats <= 0) return;
            var (fill, border, strip, content) = ClipColors(colorIndex);
            double sy = Math.Floor(y) + 2;
            var rect = new Rect(x0, sy, Math.Max(2, x1 - x0), Math.Max(8, rowH - 4));
            double radius = ClipShape switch
            {
                "Rectangle" => 0,
                "Pill" => Math.Min(12, rect.Height / 2),
                _ => 4
            };
            ctx.DrawRectangle(fill, selected ? ClipSelBorder : border, rect, radius, radius);

            // Resize-edge affordance: a bright bar on the edge a drag would trim.
            if (edgeHi != Drag.None)
            {
                double ex = edgeHi == Drag.TrimL ? rect.X : rect.Right - 2.5;
                ctx.FillRectangle(EdgeHighlight, new Rect(ex, rect.Y, 2.5, rect.Height));
            }

            double stripH = Math.Min(14, rect.Height);
            if (rect.Width > 3)
            {
                var stripRect = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, stripH);
                ctx.FillRectangle(strip, stripRect);
                if (rect.Width > 16)
                {
                    // The clip's own name (set via its menu) wins; otherwise the track name.
                    var textColor = ClipDarkTextAndWaveform ? Brushes.Black : Brushes.White;
                    var ft = new FormattedText(string.IsNullOrEmpty(c.Name) ? label : c.Name, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, TitleTypeface, 10.5, textColor);
                    using (ctx.PushClip(stripRect))
                        ctx.DrawText(ft, new Point(rect.X + 5, rect.Y + 2));
                }
            }

            var contentRect = new Rect(rect.X, rect.Y + stripH, rect.Width, Math.Max(0, rect.Height - stripH));
            if (c.IsMidi) DrawNotes(ctx, c, contentRect, content);
            else DrawWaveform(ctx, c, contentRect, content);

            // Audio clip fade-in & fade-out shading, curves, and hover notch handles
            if (!c.IsMidi && rect.Width > 8)
            {
                DrawClipFades(ctx, c, rect, stripH);
            }

            // Deactivated clip (key 0): grey it out with a dark scrim so it reads as "off"
            // while still showing its content/geometry. Inset so the (selection) border stays.
            if (!c.Active)
                ctx.DrawRectangle(InactiveVeil, null, rect.Deflate(1), Math.Max(0, radius - 1), Math.Max(0, radius - 1));
        }

        private static StreamGeometry BuildDownwardNotchGeometry(double cx, double topY, double w, double h, double tipH)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                double halfW = w / 2.0;
                double rectBot = topY + h - tipH;
                g.BeginFigure(new Point(cx - halfW, topY), true);
                g.LineTo(new Point(cx + halfW, topY));
                g.LineTo(new Point(cx + halfW, rectBot));
                g.LineTo(new Point(cx, topY + h)); // Downward point tip
                g.LineTo(new Point(cx - halfW, rectBot));
                g.EndFigure(true);
            }
            return geo;
        }

        private void DrawClipFades(DrawingContext ctx, ClipVM c, Rect rect, double stripH)
        {
            double ppb = _o._pixelsPerBeat;
            bool isClipHovered = (_hoverClipTrack == c.TrackId && _hoverClipIndex == c.ClipIndex);
            bool isDraggingFade = (_dragClip == c && (_drag == Drag.FadeIn || _drag == Drag.FadeOut));
            bool showHandles = isClipHovered || isDraggingFade;

            double fadeInW = Math.Clamp(c.FadeInBeats * ppb, 0, rect.Width);
            double fadeOutW = Math.Clamp(c.FadeOutBeats * ppb, 0, rect.Width);

            double contentTop = rect.Y + stripH;
            var contentRect = new Rect(rect.X, contentTop, rect.Width, Math.Max(0, rect.Height - stripH));

            using (ctx.PushClip(contentRect))
            {
                // 1. Fade-in shaded area & curve in waveform area
                if (fadeInW > 0.5)
                {
                    double fx = rect.X + fadeInW;
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(new Point(rect.X, contentTop), true);
                        g.LineTo(new Point(fx, contentTop));
                        g.LineTo(new Point(rect.X, rect.Bottom));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(FadeAreaFill, null, geo);
                    ctx.DrawLine(FadeCurvePen, new Point(rect.X, rect.Bottom), new Point(fx, contentTop));
                }

                // 2. Fade-out shaded area & curve in waveform area
                if (fadeOutW > 0.5)
                {
                    double fx = rect.Right - fadeOutW;
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(new Point(fx, contentTop), true);
                        g.LineTo(new Point(rect.Right, contentTop));
                        g.LineTo(new Point(rect.Right, rect.Bottom));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(FadeAreaFill, null, geo);
                    ctx.DrawLine(FadeCurvePen, new Point(fx, contentTop), new Point(rect.Right, rect.Bottom));
                }
            }

            // 3. Fade notch handles (Downward pointing notched rectangle in waveform area)
            if (showHandles && contentRect.Height >= 10)
            {
                double notchY = contentTop + 0.5;

                // Fade In Notch Handle
                double hxIn = rect.X + fadeInW;
                bool isHoveredIn = (_hoverClipTrack == c.TrackId && _hoverClipIndex == c.ClipIndex && _hoverFadeInHandle)
                                 || (_drag == Drag.FadeIn && _dragClip == c);

                double wIn = isHoveredIn ? 7.0 : 5.5;
                double hIn = isHoveredIn ? 8.0 : 6.5;
                double tipHIn = isHoveredIn ? 2.5 : 2.0;
                double hxInClamped = Math.Clamp(hxIn, rect.X + wIn / 2.0, rect.Right - wIn / 2.0);

                var fillIn = isHoveredIn ? FadeHandleHoverFill : FadeHandleFill;
                var borderIn = isHoveredIn ? FadeHandleHoverBorder : FadeHandleBorder;

                var geoInShadow = BuildDownwardNotchGeometry(hxInClamped, notchY, wIn + 1.5, hIn + 1.0, tipHIn + 0.5);
                ctx.DrawGeometry(Brushes.Black, null, geoInShadow);

                var geoIn = BuildDownwardNotchGeometry(hxInClamped, notchY, wIn, hIn, tipHIn);
                ctx.DrawGeometry(fillIn, borderIn, geoIn);

                // Fade Out Notch Handle
                double hxOut = rect.Right - fadeOutW;
                bool isHoveredOut = (_hoverClipTrack == c.TrackId && _hoverClipIndex == c.ClipIndex && _hoverFadeOutHandle)
                                  || (_drag == Drag.FadeOut && _dragClip == c);

                double wOut = isHoveredOut ? 7.0 : 5.5;
                double hOut = isHoveredOut ? 8.0 : 6.5;
                double tipHOut = isHoveredOut ? 2.5 : 2.0;
                double hxOutClamped = Math.Clamp(hxOut, rect.X + wOut / 2.0, rect.Right - wOut / 2.0);

                var fillOut = isHoveredOut ? FadeHandleHoverFill : FadeHandleFill;
                var borderOut = isHoveredOut ? FadeHandleHoverBorder : FadeHandleBorder;

                var geoOutShadow = BuildDownwardNotchGeometry(hxOutClamped, notchY, wOut + 1.5, hOut + 1.0, tipHOut + 0.5);
                ctx.DrawGeometry(Brushes.Black, null, geoOutShadow);

                var geoOut = BuildDownwardNotchGeometry(hxOutClamped, notchY, wOut, hOut, tipHOut);
                ctx.DrawGeometry(fillOut, borderOut, geoOut);
            }
        }

        // Capture full-material peaks + the committed play window for a live resize preview
        // (point 2). Unwarped: peaks over the whole sample, window = source frames. Warped:
        // peaks over the whole warp, window = played beats. Cleared on release/cancel.
        private void CaptureResizePreview(int trackId, int clipIndex)
        {
            _rpPeaks = null; _rpCount = 0;
            var eng = _o._engine;
            if (eng is null || !eng.TryGetAudioClipInfo(trackId, clipIndex, out var ai)) return;
            const int buckets = 8192;
            var peaks = new float[buckets * 2];
            if (ai.WarpEnabled != 0 && ai.WarpBeats > 0)
            {
                int n = eng.GetClipWarpFullPeaks(trackId, clipIndex, peaks, buckets);
                if (n <= 0) return;
                _rpCount = n; _rpPeaks = peaks;
                _rpMTotal = ai.WarpBeats;
                _rpS0 = ai.WarpPlayStart;
                _rpS1 = ai.WarpPlayEnd > 0 ? ai.WarpPlayEnd : ai.WarpBeats;
            }
            else
            {
                if (ai.SampleId == 0 || !eng.TryGetSampleInfo(ai.SampleId, out var si) || si.Frames <= 0) return;
                int n = eng.GetClipSourcePeaks(trackId, clipIndex, peaks, buckets);
                if (n <= 0) return;
                _rpCount = n; _rpPeaks = peaks;
                _rpMTotal = si.Frames;
                _rpS0 = ai.SourceOffsetFrames;
                double len = ai.LengthFrames > 0 ? ai.LengthFrames : si.Frames - ai.SourceOffsetFrames;
                _rpS1 = ai.SourceOffsetFrames + len;
            }
        }

        // Draw the clip being resized from its full-material peaks, mapping each timeline
        // beat through the committed window so the SAME audio stays anchored and dragging
        // reveals/hides real content (point 2) rather than squashing the window peaks.
        private void DrawResizeWaveform(DrawingContext ctx, Rect r, IBrush brush)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var _ = ctx.PushClip(r);
            double mid = Math.Floor(r.Y + r.Height / 2) + 0.5;
            double amp = Math.Max(1.0, (r.Height - 6) / 2.0);
            double ppb = _o._pixelsPerBeat, scroll = _o._scrollBeats;
            double rate = (_rpS1 - _rpS0) / _origLen;   // material units per timeline beat (committed)
            double vx0 = Math.Max(r.X, 0), vx1 = Math.Min(r.Right, Bounds.Width);
            if (vx1 <= vx0) return;

            var zeroLinePen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1);
            ctx.DrawLine(zeroLinePen, new Point(r.X, mid), new Point(r.Right, mid));

            double pxPerBucket = r.Width / Math.Max(1.0, (double)_rpCount);

            // Layer 1: Semi-transparent envelope body fill
            var fillGeo = new StreamGeometry();
            using (var fg = fillGeo.Open())
            {
                fg.BeginFigure(new Point(vx0, mid), true);
                for (double px = vx0; px <= vx1; px += 1.0)
                {
                    double s0 = _rpS0 + (scroll + px / ppb - _origStart) * rate;
                    double s1 = _rpS0 + (scroll + (px + 1) / ppb - _origStart) * rate;
                    double f0 = Math.Clamp(s0 / _rpMTotal, 0, 1), f1 = Math.Clamp(s1 / _rpMTotal, 0, 1);
                    float max;
                    if (pxPerBucket > 1.0)
                    {
                        double u = f0 * (_rpCount - 1);
                        int idx = Math.Clamp((int)u, 0, _rpCount - 1);
                        int next = Math.Min(idx + 1, _rpCount - 1);
                        float frac = (float)(u - idx);
                        max = _rpPeaks![idx * 2 + 1] * (1 - frac) + _rpPeaks[next * 2 + 1] * frac;
                    }
                    else
                    {
                        int b0 = Math.Clamp((int)(f0 * _rpCount), 0, _rpCount - 1);
                        int b1 = Math.Clamp((int)Math.Ceiling(f1 * _rpCount), b0 + 1, _rpCount);
                        max = 0f;
                        for (int b = b0; b < b1; b++)
                            max = Math.Max(max, _rpPeaks![b * 2 + 1]);
                    }
                    double yTop = Math.Min(mid - 0.5, mid - max * amp);
                    fg.LineTo(new Point(px, yTop));
                }
                for (double px = vx1; px >= vx0; px -= 1.0)
                {
                    double s0 = _rpS0 + (scroll + px / ppb - _origStart) * rate;
                    double s1 = _rpS0 + (scroll + (px + 1) / ppb - _origStart) * rate;
                    double f0 = Math.Clamp(s0 / _rpMTotal, 0, 1), f1 = Math.Clamp(s1 / _rpMTotal, 0, 1);
                    float min;
                    if (pxPerBucket > 1.0)
                    {
                        double u = f0 * (_rpCount - 1);
                        int idx = Math.Clamp((int)u, 0, _rpCount - 1);
                        int next = Math.Min(idx + 1, _rpCount - 1);
                        float frac = (float)(u - idx);
                        min = _rpPeaks![idx * 2] * (1 - frac) + _rpPeaks[next * 2] * frac;
                    }
                    else
                    {
                        int b0 = Math.Clamp((int)(f0 * _rpCount), 0, _rpCount - 1);
                        int b1 = Math.Clamp((int)Math.Ceiling(f1 * _rpCount), b0 + 1, _rpCount);
                        min = 0f;
                        for (int b = b0; b < b1; b++)
                            min = Math.Min(min, _rpPeaks![b * 2]);
                    }
                    double yBot = Math.Max(mid + 0.5, mid - min * amp);
                    fg.LineTo(new Point(px, yBot));
                }
                fg.EndFigure(true);
            }
            if (brush is SolidColorBrush scbResize)
            {
                var bodyBrush = new SolidColorBrush(Color.FromArgb((byte)(scbResize.Color.A * 0.40), scbResize.Color.R, scbResize.Color.G, scbResize.Color.B));
                ctx.DrawGeometry(bodyBrush, null, fillGeo);
            }

            // Layer 2: 100% opaque, ultra-sharp vertical needle lines
            var linesGeo = new StreamGeometry();
            using (var lg = linesGeo.Open())
            {
                for (double px = vx0; px <= vx1; px += 1.0)
                {
                    double s0 = _rpS0 + (scroll + px / ppb - _origStart) * rate;
                    double s1 = _rpS0 + (scroll + (px + 1) / ppb - _origStart) * rate;
                    double f0 = Math.Clamp(s0 / _rpMTotal, 0, 1), f1 = Math.Clamp(s1 / _rpMTotal, 0, 1);
                    float min, max;
                    if (pxPerBucket > 1.0)
                    {
                        double u = f0 * (_rpCount - 1);
                        int idx = Math.Clamp((int)u, 0, _rpCount - 1);
                        int next = Math.Min(idx + 1, _rpCount - 1);
                        float frac = (float)(u - idx);
                        min = _rpPeaks![idx * 2] * (1 - frac) + _rpPeaks[next * 2] * frac;
                        max = _rpPeaks[idx * 2 + 1] * (1 - frac) + _rpPeaks[next * 2 + 1] * frac;
                    }
                    else
                    {
                        int b0 = Math.Clamp((int)(f0 * _rpCount), 0, _rpCount - 1);
                        int b1 = Math.Clamp((int)Math.Ceiling(f1 * _rpCount), b0 + 1, _rpCount);
                        min = 0f; max = 0f;
                        for (int b = b0; b < b1; b++)
                        {
                            min = Math.Min(min, _rpPeaks![b * 2]);
                            max = Math.Max(max, _rpPeaks[b * 2 + 1]);
                        }
                    }
                    double yTop = Math.Min(mid - 0.5, mid - max * amp);
                    double yBot = Math.Max(mid + 0.5, mid - min * amp);
                    double x = Math.Floor(px) + 0.5;
                    lg.BeginFigure(new Point(x, yTop), false);
                    lg.LineTo(new Point(x, yBot));
                    lg.EndFigure(false);
                }
            }
            var needlePen = new Pen(brush, 1.0);
            ctx.DrawGeometry(null, needlePen, linesGeo);
        }

        private void DrawWaveform(DrawingContext ctx, ClipVM c, Rect r, IBrush brush)
        {
            // While trimming (with normal tool), show the real audio revealed/hidden by the drag.
            // In Stretch mode, scale the current slice's waveform smoothly to the dragged width.
            if ((_drag == Drag.TrimL || _drag == Drag.TrimR) && ReferenceEquals(c, _dragClip)
                && _o.CurrentTool != ArrangementTool.Stretch
                && _rpPeaks is not null && _rpCount > 0 && _rpMTotal > 0 && _origLen > 0 && r.Height > 0 && r.Width > 0)
            {
                DrawResizeWaveform(ctx, r, brush);
                return;
            }
            if (r.Height <= 0 || r.Width <= 0) return;
            using var _ = ctx.PushClip(r);
            double mid = Math.Floor(r.Y + r.Height / 2) + 0.5;
            double amp = Math.Max(1.0, (r.Height - 6) / 2.0);

            // Subtle horizontal zero-centerline
            var zeroLinePen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1);
            ctx.DrawLine(zeroLinePen, new Point(r.X, mid), new Point(r.Right, mid));

            // Fast path for micro/sliced clips (< 4px) to drastically reduce GPU geometry overhead
            if (r.Width < 4)
            {
                ctx.DrawLine(new Pen(brush, 1.0), new Point(Math.Floor(r.X + r.Width / 2) + 0.5, mid - amp * 0.7), new Point(Math.Floor(r.X + r.Width / 2) + 0.5, mid + amp * 0.7));
                return;
            }

            double vx0 = Math.Max(r.X, 0), vx1 = Math.Min(r.Right, Bounds.Width);
            if (vx1 <= vx0) return;

            int colCount = Math.Min(4096, Math.Max(1, (int)Math.Ceiling(vx1 - vx0)));
            double bStart = _o.XToBeat(vx0) - c.StartBeat;
            double bEnd = _o.XToBeat(vx1) - c.StartBeat;

            int n = 0;
            if (_o._engine is not null && c.TrackId > 0)
            {
                n = _o._engine.GetClipViewportPeaks(c.TrackId, c.ClipIndex, bStart, bEnd, _viewportPeaks, colCount);
            }

            if (n > 0)
            {
                // Layer 1: Semi-transparent envelope body fill for solidity
                var fillGeo = new StreamGeometry();
                using (var fg = fillGeo.Open())
                {
                    fg.BeginFigure(new Point(vx0, mid), true);
                    for (int i = 0; i < n; i++)
                    {
                        double px = vx0 + i;
                        float max = _viewportPeaks[i * 2 + 1];
                        double yTop = Math.Min(mid - 0.5, mid - max * amp);
                        fg.LineTo(new Point(px, yTop));
                    }
                    for (int i = n - 1; i >= 0; i--)
                    {
                        double px = vx0 + i;
                        float min = _viewportPeaks[i * 2];
                        double yBot = Math.Max(mid + 0.5, mid - min * amp);
                        fg.LineTo(new Point(px, yBot));
                    }
                    fg.EndFigure(true);
                }
                if (brush is SolidColorBrush scb)
                {
                    var bodyBrush = new SolidColorBrush(Color.FromArgb((byte)(scb.Color.A * 0.40), scb.Color.R, scb.Color.G, scb.Color.B));
                    ctx.DrawGeometry(bodyBrush, null, fillGeo);
                }

                // Layer 2: 100% opaque, ultra-sharp vertical peak needle lines (FL Studio signature look)
                var linesGeo = new StreamGeometry();
                using (var lg = linesGeo.Open())
                {
                    for (int i = 0; i < n; i++)
                    {
                        float min = _viewportPeaks[i * 2];
                        float max = _viewportPeaks[i * 2 + 1];
                        double yTop = Math.Min(mid - 0.5, mid - max * amp);
                        double yBot = Math.Max(mid + 0.5, mid - min * amp);
                        double x = Math.Floor(vx0 + i) + 0.5;
                        lg.BeginFigure(new Point(x, yTop), false);
                        lg.LineTo(new Point(x, yBot));
                        lg.EndFigure(false);
                    }
                }
                var needlePen = new Pen(brush, 1.0);
                ctx.DrawGeometry(null, needlePen, linesGeo);
                return;
            }

            if (c.Peaks is null || c.PeakCount <= 0) return;

            double pxPerBucket = r.Width / Math.Max(1.0, (double)c.PeakCount);

            // Fallback Layer 1: Semi-transparent envelope body fill for solidity
            var fallbackFillGeo = new StreamGeometry();
            using (var fg = fallbackFillGeo.Open())
            {
                fg.BeginFigure(new Point(vx0, mid), true);
                for (double px = vx0; px <= vx1; px += 1.0)
                {
                    float max;
                    double u = (px - r.X) / r.Width * (c.PeakCount - 1);
                    int idx = Math.Clamp((int)u, 0, c.PeakCount - 1);
                    int next = Math.Min(idx + 1, c.PeakCount - 1);
                    float frac = (float)(u - idx);
                    if (pxPerBucket > 1.0 || idx == next)
                    {
                        max = c.Peaks[idx * 2 + 1] * (1 - frac) + c.Peaks[next * 2 + 1] * frac;
                    }
                    else
                    {
                        int b0 = Math.Clamp((int)((px - r.X) / r.Width * c.PeakCount), 0, c.PeakCount - 1);
                        int b1 = Math.Clamp((int)Math.Ceiling((px + 1 - r.X) / r.Width * c.PeakCount), b0 + 1, c.PeakCount);
                        max = 0f;
                        for (int b = b0; b < b1; b++)
                            max = Math.Max(max, c.Peaks[b * 2 + 1]);
                        float interp = c.Peaks[idx * 2 + 1] * (1 - frac) + c.Peaks[next * 2 + 1] * frac;
                        max = Math.Max(max, interp);
                    }
                    double yTop = Math.Min(mid - 0.5, mid - max * amp);
                    fg.LineTo(new Point(px, yTop));
                }
                for (double px = vx1; px >= vx0; px -= 1.0)
                {
                    float min;
                    double u = (px - r.X) / r.Width * (c.PeakCount - 1);
                    int idx = Math.Clamp((int)u, 0, c.PeakCount - 1);
                    int next = Math.Min(idx + 1, c.PeakCount - 1);
                    float frac = (float)(u - idx);
                    if (pxPerBucket > 1.0 || idx == next)
                    {
                        min = c.Peaks[idx * 2] * (1 - frac) + c.Peaks[next * 2] * frac;
                    }
                    else
                    {
                        int b0 = Math.Clamp((int)((px - r.X) / r.Width * c.PeakCount), 0, c.PeakCount - 1);
                        int b1 = Math.Clamp((int)Math.Ceiling((px + 1 - r.X) / r.Width * c.PeakCount), b0 + 1, c.PeakCount);
                        min = 0f;
                        for (int b = b0; b < b1; b++)
                            min = Math.Min(min, c.Peaks[b * 2]);
                        float interp = c.Peaks[idx * 2] * (1 - frac) + c.Peaks[next * 2] * frac;
                        min = Math.Min(min, interp);
                    }
                    double yBot = Math.Max(mid + 0.5, mid - min * amp);
                    fg.LineTo(new Point(px, yBot));
                }
                fg.EndFigure(true);
            }
            if (brush is SolidColorBrush scbFallback)
            {
                var bodyBrush = new SolidColorBrush(Color.FromArgb((byte)(scbFallback.Color.A * 0.40), scbFallback.Color.R, scbFallback.Color.G, scbFallback.Color.B));
                ctx.DrawGeometry(bodyBrush, null, fallbackFillGeo);
            }

            // Fallback Layer 2: 100% opaque, ultra-sharp vertical peak needle lines
            var fallbackLinesGeo = new StreamGeometry();
            using (var lg = fallbackLinesGeo.Open())
            {
                for (double px = vx0; px <= vx1; px += 1.0)
                {
                    float min, max;
                    double u = (px - r.X) / r.Width * (c.PeakCount - 1);
                    int idx = Math.Clamp((int)u, 0, c.PeakCount - 1);
                    int next = Math.Min(idx + 1, c.PeakCount - 1);
                    float frac = (float)(u - idx);
                    if (pxPerBucket > 1.0 || idx == next)
                    {
                        min = c.Peaks[idx * 2] * (1 - frac) + c.Peaks[next * 2] * frac;
                        max = c.Peaks[idx * 2 + 1] * (1 - frac) + c.Peaks[next * 2 + 1] * frac;
                    }
                    else
                    {
                        int b0 = Math.Clamp((int)((px - r.X) / r.Width * c.PeakCount), 0, c.PeakCount - 1);
                        int b1 = Math.Clamp((int)Math.Ceiling((px + 1 - r.X) / r.Width * c.PeakCount), b0 + 1, c.PeakCount);
                        min = 0f; max = 0f;
                        for (int b = b0; b < b1; b++)
                        {
                            min = Math.Min(min, c.Peaks[b * 2]);
                            max = Math.Max(max, c.Peaks[b * 2 + 1]);
                        }
                        float interpMin = c.Peaks[idx * 2] * (1 - frac) + c.Peaks[next * 2] * frac;
                        float interpMax = c.Peaks[idx * 2 + 1] * (1 - frac) + c.Peaks[next * 2 + 1] * frac;
                        min = Math.Min(min, interpMin);
                        max = Math.Max(max, interpMax);
                    }
                    double yTop = Math.Min(mid - 0.5, mid - max * amp);
                    double yBot = Math.Max(mid + 0.5, mid - min * amp);
                    double x = Math.Floor(px) + 0.5;
                    lg.BeginFigure(new Point(x, yTop), false);
                    lg.LineTo(new Point(x, yBot));
                    lg.EndFigure(false);
                }
            }
            var fallbackNeedlePen = new Pen(brush, 1.0);
            ctx.DrawGeometry(null, fallbackNeedlePen, fallbackLinesGeo);
        }

        private void DrawAutomation(DrawingContext ctx, double w)
        {
            var (rLo, rHi) = _o.VisibleRowRange();
            double scrollX = _o._scroller?.Offset.X ?? 0;
            double pinX = scrollX + 8;
            var pillBg = new SolidColorBrush(Color.FromArgb(0xEE, 0x1E, 0x1E, 0x1E));
            var pillBorder = new Pen(new SolidColorBrush(Color.Parse("#383838")), 1.0);
            var subLaneBg = new SolidColorBrush(Color.FromArgb(0xEE, 0x14, 0x14, 0x14));
            var subLaneDividerPen = new Pen(new SolidColorBrush(Color.Parse("#262626")), 1.0);

            for (int i = rLo; i <= rHi; i++)
            {
                var t = _o._tracks[i];
                double y = RowTop(i);
                double rh = MainRowHeight(i);
                ctx.FillRectangle(AutoScrim, new Rect(0, y, w, rh));

                var (fill, _, strip, content) = ClipColors(t.ColorIndex);
                var contentColor = content is SolidColorBrush scb ? scb.Color : Color.Parse("#D8A03D");
                var pen = new Pen(content, 2.2);
                var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(0x35, contentColor.R, contentColor.G, contentColor.B)), 5.0);
                var areaFillBrush = new SolidColorBrush(Color.FromArgb(0x20, contentColor.R, contentColor.G, contentColor.B));
                var tensionFill = new SolidColorBrush(Color.FromArgb(0xEE, contentColor.R, contentColor.G, contentColor.B));

                var hitMain = new AutoLaneHit { TrackIndex = i, Track = t, SubLane = null, Top = y, Height = rh };

                // --- Draw Main Track Automation Lane ---
                DrawSingleAutoLane(ctx, hitMain, t.AutoPoints, content, contentColor, pen, glowPen, areaFillBrush, tensionFill, w);

                // Range selection band (Phase 2)
                if (ReferenceEquals(_o._autoSelTrack, t) && _o._autoSelEnd - _o._autoSelStart > 1e-6)
                {
                    double sx = _o.BeatToX(_o._autoSelStart), ex = _o.BeatToX(_o._autoSelEnd);
                    double cx0 = Math.Max(0, sx), cx1 = Math.Min(w, ex);
                    if (cx1 > cx0)
                    {
                        double totalH = RowHeight(i);
                        ctx.FillRectangle(AutoSelBand, new Rect(cx0, y, cx1 - cx0, totalH));
                        if (sx >= 0) ctx.DrawLine(AutoSelEdge, new Point(sx, y), new Point(sx, y + totalH));
                        if (ex <= w) ctx.DrawLine(AutoSelEdge, new Point(ex, y), new Point(ex, y + totalH));
                    }
                }

                // --- Draw Automation Sub-Lanes ---
                if (t.AutoExpanded && t.SubLanes.Count > 0)
                {
                    double slY = y + rh;
                    foreach (var sl in t.SubLanes)
                    {
                        // Background and top divider
                        ctx.DrawLine(subLaneDividerPen, new Point(0, slY), new Point(w, slY));
                        ctx.FillRectangle(subLaneBg, new Rect(0, slY, w, sl.Height));

                        var hitSub = new AutoLaneHit { TrackIndex = i, Track = t, SubLane = sl, Top = slY, Height = sl.Height };
                        DrawSingleAutoLane(ctx, hitSub, sl.Points, content, contentColor, pen, glowPen, areaFillBrush, tensionFill, w);

                        slY += sl.Height;
                    }
                }
            }

            // Floating HUD Tooltip Badge
            string? hudText = null;
            if (_autoDrag is not null && _autoDragTrack is not null)
            {
                string trkName = _autoDragSubLane != null ? $"{_autoDragTrack.Name} ({_autoDragSubLane.Label})" : _autoDragTrack.Name;
                var tgt = _autoDragSubLane?.Target ?? _autoDragTrack.AutoTarget;
                hudText = $"{trkName}: {ArrangementView.FormatAutoValue(tgt, _autoDrag.Value)} · Bar {FormatBeat(_autoDrag.Beat)}";
            }
            else if (_bendLeft is not null)
            {
                int pct = (int)Math.Round(_bendLeft.Curve * 100);
                hudText = $"Curve: {(pct >= 0 ? "+" : "")}{pct}% {(pct > 0 ? "(Ease-Out)" : pct < 0 ? "(Ease-In)" : "(Linear)")}";
            }
            else if (_segDragLeft is not null && _segDragTrack is not null)
            {
                string trkName = _segDragSubLane != null ? $"{_segDragTrack.Name} ({_segDragSubLane.Label})" : _segDragTrack.Name;
                var tgt = _segDragSubLane?.Target ?? _segDragTrack.AutoTarget;
                hudText = $"{trkName}: {ArrangementView.FormatAutoValue(tgt, _segDragLeft.Value)}";
            }
            else if (_hoverPoint is not null && _hoverTrack is not null)
            {
                string trkName = _hoverSubLane != null ? $"{_hoverTrack.Name} ({_hoverSubLane.Label})" : _hoverTrack.Name;
                var tgt = _hoverSubLane?.Target ?? _hoverTrack.AutoTarget;
                hudText = $"{trkName}: {ArrangementView.FormatAutoValue(tgt, _hoverPoint.Value)} · Bar {FormatBeat(_hoverPoint.Beat)}";
            }

            if (!string.IsNullOrEmpty(hudText))
            {
                var ftHud = new FormattedText(hudText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(KnoxPalette.UiFont, FontStyle.Normal, FontWeight.SemiBold), 10, Brushes.White);
                double tipW = ftHud.Width + 16;
                double tipH = ftHud.Height + 8;
                double tipX = Math.Clamp(_lastAutoPointerPos.X - tipW / 2.0, 4, w - tipW - 4);
                double tipY = _lastAutoPointerPos.Y - tipH - 12;
                if (tipY < 4) tipY = _lastAutoPointerPos.Y + 16;

                var tipRect = new Rect(tipX, tipY, tipW, tipH);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x16, 0x16)),
                    new Pen(new SolidColorBrush(Color.Parse("#444444")), 1.0), tipRect, 4, 4);
                ctx.DrawText(ftHud, new Point(tipX + 8, tipY + 4));
            }
        }

        private void DrawSingleAutoLane(DrawingContext ctx, AutoLaneHit hit, List<AutoPt> rawPts, IBrush content, Color contentColor, Pen pen, Pen glowPen, IBrush areaFillBrush, IBrush tensionFill, double w)
        {
            var ordered = rawPts.OrderBy(p => p.Beat).ToList();
            if (ordered.Count == 0)
            {
                float baseVal = hit.IsSubLane ? _o.AutoCurrent(hit.SubLane!) : _o.AutoCurrent(hit.Track);
                double by = LaneValueToY(hit, baseVal);
                var tgt = hit.IsSubLane ? hit.SubLane!.Target : hit.Track.AutoTarget;
                string lbl = hit.IsSubLane ? hit.SubLane!.Label : hit.Track.AutoLabel;

                // 1. Shaded area below the baseline level
                var areaGeo = new StreamGeometry();
                using (var ag = areaGeo.Open())
                {
                    ag.BeginFigure(new Point(0, hit.Top + hit.Height), true);
                    ag.LineTo(new Point(0, by));
                    ag.LineTo(new Point(w, by));
                    ag.LineTo(new Point(w, hit.Top + hit.Height));
                    ag.EndFigure(true);
                }
                ctx.DrawGeometry(areaFillBrush, null, areaGeo);

                // 2. Full continuous reference line across the entire timeline
                bool isLaneHover = ReferenceEquals(hit.Track, _hoverTrack) && (hit.SubLane == null && _hoverSubLane == null || ReferenceEquals(hit.SubLane, _hoverSubLane));
                var linePen = isLaneHover ? AutoHoverPen : pen;
                var lineGlow = isLaneHover ? PlayheadGlow : glowPen;

                ctx.DrawLine(lineGlow, new Point(0, by), new Point(w, by));
                ctx.DrawLine(linePen, new Point(0, by), new Point(w, by));

                // 3. Crisp baseline value badge on the line
                string valStr = ArrangementView.FormatAutoValue(tgt, baseVal, lbl);
                var valFt = new FormattedText(valStr, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(KnoxPalette.UiFont, FontStyle.Normal, FontWeight.SemiBold), 9.0, isLaneHover ? PlayheadBrush : content);
                double badgeX = 8;
                double badgeY = Math.Clamp(by - valFt.Height - 3, hit.Top + 2, hit.Top + hit.Height - valFt.Height - 4);
                var badgeRect = new Rect(badgeX - 3, badgeY - 1, valFt.Width + 6, valFt.Height + 2);
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(0xDD, 0x14, 0x14, 0x18)), badgeRect, 3);
                ctx.DrawRectangle(null, new Pen(isLaneHover ? DeviceCardKit.AccentBright : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 0.8), badgeRect, 3);
                ctx.DrawText(valFt, new Point(badgeX, badgeY));
            }
            else
            {
                double firstX = _o.BeatToX(ordered[0].Beat), firstY = LaneValueToY(hit, ordered[0].Value);
                double lastX = _o.BeatToX(ordered[^1].Beat), lastY = LaneValueToY(hit, ordered[^1].Value);

                // 1. Shaded area below the automation curve
                var areaGeo = new StreamGeometry();
                using (var ag = areaGeo.Open())
                {
                    ag.BeginFigure(new Point(0, hit.Top + hit.Height), true);
                    ag.LineTo(new Point(0, firstY));
                    ag.LineTo(new Point(firstX, firstY));
                    for (int k = 1; k < ordered.Count; k++)
                    {
                        var a = ordered[k - 1]; var b = ordered[k];
                        double ax = _o.BeatToX(a.Beat), bx = _o.BeatToX(b.Beat);
                        if (a.Curve == 0f) { ag.LineTo(new Point(bx, LaneValueToY(hit, b.Value))); }
                        else
                        {
                            const int steps = 16;
                            for (int s = 1; s <= steps; s++)
                            {
                                double tt = s / (double)steps;
                                double vy = a.Value + (b.Value - a.Value) * Shape(tt, a.Curve);
                                ag.LineTo(new Point(ax + (bx - ax) * tt, LaneValueToY(hit, (float)vy)));
                            }
                        }
                    }
                    ag.LineTo(new Point(lastX, lastY));
                    ag.LineTo(new Point(w, lastY));
                    ag.LineTo(new Point(w, hit.Top + hit.Height));
                    ag.EndFigure(true);
                }
                ctx.DrawGeometry(areaFillBrush, null, areaGeo);

                // 2. Left and Right end-holds
                ctx.DrawLine(glowPen, new Point(0, firstY), new Point(firstX, firstY));
                ctx.DrawLine(pen, new Point(0, firstY), new Point(firstX, firstY));
                ctx.DrawLine(glowPen, new Point(lastX, lastY), new Point(w, lastY));
                ctx.DrawLine(pen, new Point(lastX, lastY), new Point(w, lastY));

                // 3. Segment curve lines + midpoint tension handles
                for (int k = 1; k < ordered.Count; k++)
                {
                    var a = ordered[k - 1]; var b = ordered[k];
                    bool isLaneHover = ReferenceEquals(hit.Track, _hoverTrack) && (hit.SubLane == null && _hoverSubLane == null || ReferenceEquals(hit.SubLane, _hoverSubLane));
                    bool isSegHover = isLaneHover && ReferenceEquals(a, _hoverSegLeft);
                    var segPen = isSegHover ? AutoHoverPen : pen;
                    var segGlow = isSegHover ? PlayheadGlow : glowPen;

                    double ax = _o.BeatToX(a.Beat), ay = LaneValueToY(hit, a.Value);
                    double bx = _o.BeatToX(b.Beat), by2 = LaneValueToY(hit, b.Value);

                    if (a.Curve == 0f)
                    {
                        ctx.DrawLine(segGlow, new Point(ax, ay), new Point(bx, by2));
                        ctx.DrawLine(segPen, new Point(ax, ay), new Point(bx, by2));
                    }
                    else
                    {
                        const int steps = 16;
                        var prev = new Point(ax, ay);
                        for (int s = 1; s <= steps; s++)
                        {
                            double tt = s / (double)steps;
                            double vy = a.Value + (b.Value - a.Value) * Shape(tt, a.Curve);
                            var cur = new Point(ax + (bx - ax) * tt, LaneValueToY(hit, (float)vy));
                            ctx.DrawLine(segGlow, prev, cur);
                            ctx.DrawLine(segPen, prev, cur);
                            prev = cur;
                        }
                    }

                    // Midpoint tension handle
                    double midBeat = (a.Beat + b.Beat) * 0.5;
                    double midVal = a.Value + (b.Value - a.Value) * Shape(0.5, a.Curve);
                    double midX = _o.BeatToX(midBeat);
                    double midY = LaneValueToY(hit, (float)midVal);
                    if (midX >= -10 && midX <= w + 10)
                    {
                        bool isTensionHover = isLaneHover && (ReferenceEquals(a, _hoverTensionLeft) || ReferenceEquals(a, _bendLeft));
                        ctx.DrawEllipse(Brushes.Black, null, new Point(midX, midY), isTensionHover ? 5.5 : 3.8, isTensionHover ? 5.5 : 3.8);
                        ctx.DrawEllipse(isTensionHover ? PlayheadBrush : tensionFill, null, new Point(midX, midY), isTensionHover ? 4.2 : 2.6, isTensionHover ? 4.2 : 2.6);
                        if (isTensionHover)
                            ctx.DrawEllipse(null, AutoHoverRing, new Point(midX, midY), 7.5, 7.5);
                    }
                }

                // 4. Breakpoint nodes
                for (int pIdx = 0; pIdx < ordered.Count; pIdx++)
                {
                    var p = ordered[pIdx];
                    double dx = _o.BeatToX(p.Beat), dy = LaneValueToY(hit, p.Value);
                    if (dx < -10 || dx > w + 10) continue;
                    bool isLaneHover = ReferenceEquals(hit.Track, _hoverTrack) && (hit.SubLane == null && _hoverSubLane == null || ReferenceEquals(hit.SubLane, _hoverSubLane));
                    bool ptHover = (isLaneHover && ReferenceEquals(p, _hoverPoint)) || ReferenceEquals(p, _autoDrag);
                    ctx.DrawEllipse(Brushes.Black, null, new Point(dx, dy), ptHover ? 6.5 : 4.8, ptHover ? 6.5 : 4.8);
                    ctx.DrawEllipse(ptHover ? PlayheadBrush : content, null, new Point(dx, dy), ptHover ? 5.0 : 3.6, ptHover ? 5.0 : 3.6);
                    if (ptHover)
                        ctx.DrawEllipse(null, AutoHoverRing, new Point(dx, dy), 8.5, 8.5);
                }

                // 5. Playhead tracking bead during playback
                if (_o._engine is { IsPlaying: true })
                {
                    double pBeat = _o._playheadBeats;
                    double px = _o.BeatToX(pBeat);
                    if (px >= 0 && px <= w)
                    {
                        float curV = InterpolateAuto(ordered, pBeat);
                        double py = LaneValueToY(hit, curV);
                        ctx.DrawEllipse(Brushes.Black, null, new Point(px, py), 5.5, 5.5);
                        ctx.DrawEllipse(PlayheadBrush, null, new Point(px, py), 4.0, 4.0);
                    }
                }
            }
        }
    }

    // ---- playhead + loop overlay -----------------------------------------
    // A hit-transparent layer stacked over LaneControl. Only the moving/transport-driven
    // decorations live here so the 30 Hz tick repaints these few strokes instead of the
    // whole clip/waveform surface. It shares the lanes' width/height and scroll/zoom mapping.
    private sealed class LaneOverlayControl : Control
    {
        private readonly ArrangementView _o;
        public LaneOverlayControl(ArrangementView o) { _o = o; }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;

            // Loop region: a faint wash + edge lines spanning all lanes.
            if (_o._loopActive && _o._loopE > _o._loopS)
            {
                double lx0 = _o.BeatToX(_o._loopS), lx1 = _o.BeatToX(_o._loopE);
                if (lx1 > 0 && lx0 < w)
                {
                    double cx0 = Math.Max(0, lx0), cx1 = Math.Min(w, lx1);
                    ctx.FillRectangle(LoopBand, new Rect(cx0, 0, cx1 - cx0, h));
                    if (lx0 >= 0) ctx.DrawLine(LoopEdge, new Point(lx0, 0), new Point(lx0, h));
                    if (lx1 <= w) ctx.DrawLine(LoopEdge, new Point(lx1, 0), new Point(lx1, h));
                }
            }

            // Playhead across all lanes: brass line with a soft glow.
            double px = _o.BeatToX(_o._playheadBeats);
            if (px >= 0 && px <= w)
            {
                ctx.DrawLine(PlayheadGlow, new Point(px, 0), new Point(px, h));
                ctx.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
            }
        }
    }
}

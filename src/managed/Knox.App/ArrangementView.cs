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
using Avalonia.VisualTree;
using Knox.Application;
using Knox.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace Knox.App;

/// <summary>Audio-clip → MIDI conversion modes (arrangement clip context menu).</summary>
public enum ClipConvertMode { Drums, Slice, Melody, Harmony }

/// <summary>Active tool mode for arrangement editing (Pointer, Pencil, Cut, Eraser, Stretch).</summary>
public enum ArrangementTool { Pointer, Pencil, Cut, Eraser, Stretch }

/// <summary>Follow playhead modes during playback.</summary>
public enum PlayheadFollowMode
{
    Paged = 0,             // 2- Parça parça atlama (Default)
    ContinuousLeft = 1,    // 1- Dinamik ilerleme (Çizgi sürekli en başta)
    ContinuousCenter = 2,  // 3- Sürekli çizgi takip (Merkezde / Sabit)
}

public sealed partial class ArrangementView : UserControl
{
    // --- coordinate model / shared state ---
    public ArrangementTool CurrentTool { get; set; } = ArrangementTool.Pointer;
    private double _pixelsPerBeat = 28;
    private double _scrollBeats;
    private double _playheadBeats;
    private double _totalBeats = 64;
    private int _beatsPerBar = 4;
    private double _snapBeats = 1.0;               // snap grid (1 beat) — M4-2
    internal int SelTrackId = -1, SelClipIndex = -1;   // "primary" selection (Clip tab, effect target, trim)
    // Multi-selection of clips across tracks (marquee / group move + delete). The
    // primary (SelTrackId/SelClipIndex) is always one of these when non-empty.
    private readonly HashSet<(int track, int clip)> _sel = new();
    // General time-range selection (arrangement, not automation): a beat range over a span
    // of visual rows. Mutually exclusive with the clip selection (req 1.2.5). Operations act
    // on the covered clip *parts*, splitting at the range edges.
    internal double _timeSelStart, _timeSelEnd;
    internal int _timeSelRowLo = -1, _timeSelRowHi = -1;
    internal bool HasTimeSelection => _timeSelRowLo >= 0 && _timeSelEnd - _timeSelStart > 1e-6;
    private bool _automationMode;                     // toolbar Automation toggle (M9-A3)
    private double _autoBaselineSig = double.NaN;      // signature of visible empty-lane baselines (redraw on change)
    // Loop region highlight: cached from the engine (message-thread mirror) and
    // drawn as a brace in the ruler + a wash across the lanes. During a ruler
    // drag we show a live preview before committing to the engine on release.
    private bool _loopActive;
    private double _loopS, _loopE;
    private bool _loopDragging;
    /// <summary>Raised after the arrangement authors a loop region (ruler drag /
    /// "Loop selection"), so the transport bar can reflect it.</summary>
    public event Action? LoopChanged;
    /// <summary>Raised whenever clips are resized, moved, split, or modified in the arrangement.</summary>
    public event Action? ClipsChanged;
    public void NotifyClipsChanged() => ClipsChanged?.Invoke();
    private readonly List<TrackVM> _tracks = new();   // audio + instrument (scrolling)
    private readonly List<TrackVM> _returns = new();  // return buses (pinned footer)
    private readonly Dictionary<int, MeterBar> _meters = new(); // per-track header meters (M6-2)
    // Per-track header vol/pan controls, refreshed each tick so automation moves them live.
    private readonly Dictionary<int, MiniFader> _volFaders = new();
    private readonly Dictionary<int, PanBar> _panBars = new();
    private readonly Dictionary<int, TextBlock> _volDb = new();
    // Per-track header card + its name label, so a selection change repaints just those two
    // properties instead of tearing down and rebuilding every header control (click latency).
    private readonly Dictionary<int, (Border card, TextBlock name, bool isGroup)> _headerCards = new();
    // Per-track chosen automation target, kept across Refresh (which rebuilds VMs).
    private readonly Dictionary<int, (AutomationTarget target, int dev, int param, string paramId)> _autoTargets = new();
    internal readonly Dictionary<int, List<AutoSubLaneVM>> _subLaneState = new();

    private const double HeaderW = 224;
    private static readonly double[] RowHeights = { 24, 36, 48, 74, 92, 120 };
    private int _rowHeightIdx = 3;
    public static double RowHeight = 74;
    private readonly Dictionary<int, double> _customTrackHeights = new();

    public double GetTrackHeight(int trackId)
    {
        double h = _customTrackHeights.TryGetValue(trackId, out var customH) ? customH : RowHeight;
        if (_automationMode)
        {
            var t = _tracks.FirstOrDefault(x => x.Id == trackId);
            if (t is not null && t.AutoExpanded && t.SubLanes.Count > 0)
            {
                foreach (var sl in t.SubLanes)
                    h += sl.Height;
            }
        }
        return h;
    }

    public double GetTrackMainHeight(int trackId)
    {
        if (_customTrackHeights.TryGetValue(trackId, out var h)) return h;
        return RowHeight;
    }

    public double GetTrackHeightByIndex(int index)
    {
        if (index >= 0 && index < _tracks.Count) return GetTrackHeight(_tracks[index].Id);
        return RowHeight;
    }

    public double GetTrackTop(int visualRowIndex)
    {
        double top = 0;
        int count = Math.Min(visualRowIndex, _tracks.Count);
        for (int i = 0; i < count; i++)
            top += GetTrackHeight(_tracks[i].Id);
        if (visualRowIndex > _tracks.Count)
            top += (visualRowIndex - _tracks.Count) * RowHeight;
        return top;
    }

    public double GetTotalTracksHeight()
    {
        double total = 0;
        for (int i = 0; i < _tracks.Count; i++)
            total += GetTrackHeight(_tracks[i].Id);
        return total;
    }

    public int GetTrackIndexAtY(double y)
    {
        if (y < 0) return -1;
        double acc = 0;
        for (int i = 0; i < _tracks.Count; i++)
        {
            double h = GetTrackHeight(_tracks[i].Id);
            if (y >= acc && y < acc + h) return i;
            acc += h;
        }
        return -1;
    }

    public void SetTrackHeight(int trackId, double height)
    {
        _customTrackHeights[trackId] = Math.Clamp(height, 24, 360);
        Refresh();
    }

    public void ResetTrackHeight(int trackId)
    {
        _customTrackHeights.Remove(trackId);
        Refresh();
    }

    public void ResetAllTrackHeights()
    {
        _customTrackHeights.Clear();
        Refresh();
    }

    public void CycleRowHeight()
    {
        _rowHeightIdx = (_rowHeightIdx + 1) % RowHeights.Length;
        RowHeight = RowHeights[_rowHeightIdx];
        _customTrackHeights.Clear();
        Refresh();
    }
    private const double RulerH = 24;
    private const double FooterRowH = 40;   // slim return/master rows

    private IAudioEngine? _engine;

    private readonly RulerControl _ruler;
    private readonly LaneControl _lanes;
    // Playhead + loop band live on a thin, hit-transparent overlay ABOVE the lanes so the
    // 30 Hz transport tick repaints only these few strokes instead of the whole clip/waveform
    // surface underneath (which is static during playback). See SetPlayhead / Redraw.
    private readonly LaneOverlayControl _overlay;
    private readonly Canvas _headers;
    private readonly ScrollBar _hScroll;
    // The vertical scroller wrapping [headers | lanes]; its offset/viewport drive row culling
    // in LaneControl.Render so off-screen tracks skip their clip/waveform work.
    private ScrollViewer _scroller = null!;

    // Track reorder: grab a header's title strip to drag it to a new slot. Manual
    // capture-drag (headers live in a Canvas); an accent insertion line marks the drop gap.
    private int _hdrDragId = -1, _hdrDragFrom = -1, _hdrDropGap = -1;
    private bool _hdrDragging;
    private Point _hdrDragStart;
    private readonly Border _trackDropLine = new()
    {
        IsVisible = false, IsHitTestVisible = false, Height = 2, Width = HeaderW,
        Background = KnoxPalette.AccentBright,
    };

    // Return/Master section pinned under the scrolling tracks (no scroll).
    private readonly Canvas _footerHeaders;
    private readonly FooterLaneControl _footerLanes;
    private readonly Grid _footer;

    /// <summary>Raised when a MIDI clip is double-clicked (trackId, clipIndex).</summary>
    public event Action<int, int>? MidiClipActivated;
    /// <summary>Raised when an audio clip is double-clicked (trackId, clipIndex).</summary>
    public event Action<int, int>? AudioClipActivated;
    /// <summary>Raised when a track becomes selected (clip or header click).</summary>
    public event Action<int>? TrackSelected;
    /// <summary>Live-freeze (v1.1): per-track role for the header badge — 0 none, 1 sleeping
    /// source, 2 linked frozen. Set by MainWindow; queried while rebuilding headers.</summary>
    public Func<int, int>? FreezeRole;
    /// <summary>A transient status line the arrangement wants shown (e.g. an automation-follow hint).</summary>
    public event Action<string>? StatusMessage;
    /// <summary>Raised after a clip is copied into a session slot (M5-6).</summary>
    public event Action? SessionChanged;
    /// <summary>Raised when a browser item is dropped on a lane (M7-5): item, target
    /// track id (-1 if past the last track), and the snapped drop beat.</summary>
    public event Action<BrowserItem, int, double>? ItemDropped;
    /// <summary>Audio-clip context-menu "Convert / Slice to New MIDI Track" (track id, clip index, mode).</summary>
    public event Action<int, int, ClipConvertMode>? ConvertClipRequested;

    public ArrangementView()
    {
        _ruler = new RulerControl(this) { Height = RulerH };
        _lanes = new LaneControl(this) { VerticalAlignment = VerticalAlignment.Top };
        // Sits in the same grid cell as _lanes, on top; hit-transparent so all pointer
        // gestures pass through to the lanes beneath.
        _overlay = new LaneOverlayControl(this)
        {
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        // Drop target is the whole scroller (below), not _lanes — _lanes is only as
        // tall as the tracks, so instruments dropped in the empty space beneath them
        // (the natural "make a new track" gesture) would otherwise miss.
        _headers = new Canvas { Width = HeaderW, VerticalAlignment = VerticalAlignment.Top };
        _hScroll = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Minimum = 0,
            AllowAutoHide = false,
        };
        _hScroll.Scroll += (_, _) => { _scrollBeats = _hScroll.Value; Redraw(); };
        // Keep the scroll range in sync with the visible width — outside the
        // render pass (mutating controls during Render() is illegal / crashes).
        _lanes.SizeChanged += (_, _) => SyncScroll(_lanes.Bounds.Width);

        // Top: zoom controls over the header spacer, ruler over the lanes.
        var zoomOut = ZoomChip("−");
        var zoomIn = ZoomChip("+");
        zoomOut.Click += (_, _) => Zoom(1 / 1.25);
        zoomIn.Click += (_, _) => Zoom(1.25);
        var trackHeightToggle = ZoomChip("↕");
        ToolTip.SetTip(trackHeightToggle, "Toggle track height (Compact / Normal / Large)");
        trackHeightToggle.Click += (_, _) => CycleRowHeight();
        var zoomBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5, Width = HeaderW,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Children =
            {
                new TextBlock { Text = "ZOOM", Classes = { "SectionLabel" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) },
                zoomOut, zoomIn, trackHeightToggle,
            },
        };
        var topLeft = new Border
        {
            Width = HeaderW,
            Background = RulerBg,
            BorderThickness = new Thickness(0, 0, 1, 1),
            BorderBrush = new SolidColorBrush(Color.Parse("#383838")),
            Child = zoomBar
        };
        var top = new Grid { Height = RulerH, ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        top.Children.Add(topLeft);
        Grid.SetColumn(_ruler, 1);
        top.Children.Add(_ruler);

        // Center: vertical scroll over [headers | lanes].
        var center = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        center.Children.Add(_headers);
        Grid.SetColumn(_lanes, 1);
        center.Children.Add(_lanes);
        Grid.SetColumn(_overlay, 1);
        center.Children.Add(_overlay);   // above _lanes (added later = on top)
        var scroller = _scroller = new ScrollViewer
        {
            Content = center,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        scroller.ScrollChanged += (_, _) =>
        {
            _lanes.InvalidateVisual();
            _overlay.InvalidateVisual();
        };
        scroller.SizeChanged += (_, _) =>
        {
            double minH = Math.Max(scroller.Bounds.Height, GetTotalTracksHeight());
            if (_lanes.Height < minH)
            {
                _lanes.Height = minH;
                _overlay.Height = minH;
            }
        };
        // Browser & file drag & drop covers the root, scroller, and lanes
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnLaneDragOver);
        DragDrop.AddDragLeaveHandler(this, (_, _) => { DropTrackIndex = -1; _lanes.InvalidateVisual(); });
        DragDrop.AddDropHandler(this, OnLaneDrop);

        DragDrop.SetAllowDrop(scroller, true);
        DragDrop.AddDragOverHandler(scroller, OnLaneDragOver);
        DragDrop.AddDragLeaveHandler(scroller, (_, _) => { DropTrackIndex = -1; _lanes.InvalidateVisual(); });
        DragDrop.AddDropHandler(scroller, OnLaneDrop);

        DragDrop.SetAllowDrop(_lanes, true);
        DragDrop.AddDragOverHandler(_lanes, OnLaneDragOver);
        DragDrop.AddDragLeaveHandler(_lanes, (_, _) => { DropTrackIndex = -1; _lanes.InvalidateVisual(); });
        DragDrop.AddDropHandler(_lanes, OnLaneDrop);
        // Right-click the empty area below the tracks → paste a copied track there (the
        // header cards / lanes are top-anchored, so clicks below them land on the scroller).
        scroller.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(scroller).Properties.IsRightButtonPressed) return;
            double y = e.GetPosition(_headers).Y;                     // shared vertical (scrolls with content)
            if (y >= 0 && y < GetTrackTop(_tracks.Count)) return;      // on a track → it shows its own menu
            ShowEmptyAreaMenu(scroller);
        };
        // Wheel over the empty area below the tracks (top-anchored lanes leave a gap when few
        // tracks) still zooms/scrolls the timeline. Events over the lanes are handled there
        // first (Handled), so this only picks up the gap.
        scroller.PointerWheelChanged += (_, e) => { if (!e.Handled) HandleLaneWheel(e, e.GetPosition(_lanes).X); };
        // Full-height header-column backdrop behind the scroller so the header
        // column reads as a distinct panel even with no tracks.
        var leftBackdrop = new Border
        {
            Width = HeaderW,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        leftBackdrop.BindResource(Border.BackgroundProperty, "Brush.SurfaceCard");
        leftBackdrop.BindResource(Border.BorderBrushProperty, "Brush.BorderDefault");
        var centerArea = new Grid();
        centerArea.Children.Add(leftBackdrop);
        centerArea.Children.Add(scroller);

        // Return / Master: a slim section pinned under the tracks (no scroll).
        _footerHeaders = new Canvas { Width = HeaderW };
        var footerHeaderPanel = new Border { Width = HeaderW, BorderThickness = new Thickness(0, 0, 1, 0), Child = _footerHeaders };
        footerHeaderPanel.BindResource(Border.BackgroundProperty, "Brush.SurfaceCard");
        footerHeaderPanel.BindResource(Border.BorderBrushProperty, "Brush.BorderDefault");
        _footerLanes = new FooterLaneControl(this);
        _footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(footerHeaderPanel, 0);
        Grid.SetColumn(_footerLanes, 1);
        _footer.Children.Add(footerHeaderPanel);
        _footer.Children.Add(_footerLanes);

        // Bottom: horizontal scrollbar under the lanes.
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        bottom.Children.Add(HeaderPanel(null));
        Grid.SetColumn(_hScroll, 1);
        bottom.Children.Add(_hScroll);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_footer);
        root.Children.Add(centerArea);
        Content = root;

        var settings = App.Services.GetService<ISettingsService>();
        if (settings != null)
        {
            ApplySettings(settings.Current);
            settings.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ApplySettings(settings.Current);
                _lanes.InvalidateVisual();
                Refresh();
            });
        }
    }

    public IAudioEngine? Engine
    {
        get => _engine;
        set
        {
            if (_engine is not null) _engine.AutomationTouched -= OnAutomationTouched;
            _engine = value;
            if (_engine is not null) _engine.AutomationTouched += OnAutomationTouched;
            Refresh();
        }
    }

    public double PixelsPerBeat => _pixelsPerBeat;
    public double ScrollBeats => _scrollBeats;
    public double PlayheadBeats => _playheadBeats;
    public int BeatsPerBar { get => _beatsPerBar; set { _beatsPerBar = value; Redraw(); } }
    /// <summary>Clip-drag snap grid in beats (toolbar Snap chip, 1b). Negative = Adaptive.</summary>
    public double SnapBeats { get => _snapBeats; set { _snapBeats = value; Redraw(); } }

    /// <summary>Effective snap beat size taking adaptive zoom into account.</summary>
    public double EffectiveSnapBeats
    {
        get
        {
            if (_snapBeats < 0)
            {
                double ppb = _pixelsPerBeat;
                if (ppb >= 480) return 0.03125; // 1/128
                if (ppb >= 240) return 0.0625;  // 1/64
                if (ppb >= 110) return 0.125;   // 1/32
                if (ppb >= 50)  return 0.25;    // 1/16
                if (ppb >= 22)  return 0.5;     // 1/8
                if (ppb >= 10)  return 1.0;     // 1/4 (1 beat)
                if (ppb >= 4)   return 4.0;     // 1 bar (4 beats)
                return 16.0;                    // 4 bars
            }
            return _snapBeats;
        }
    }
    /// <summary>Show/edit parameter-automation envelopes over the lanes (M9-A3).</summary>
    public bool AutomationMode
    {
        get => _automationMode;
        set { if (_automationMode == value) return; _automationMode = value; Refresh(); }
    }
    private AutomationWriteMode _autoWriteMode;
    /// <summary>Current record mode (M9-C) — drives the arm dot + live envelope refresh.</summary>
    public AutomationWriteMode AutomationWriteMode
    {
        get => _autoWriteMode;
        set { _autoWriteMode = value; if (_automationMode) Redraw(); }
    }
    /// <summary>While recording, reload the visible envelopes so writes appear live (M9-C).</summary>
    public void RefreshAutomationLive()
    {
        // Only reload while actually recording: a write mode + playing + no hand-drag
        // in progress. Otherwise this would clobber manual envelope editing.
        if (!_automationMode || _autoWriteMode == AutomationWriteMode.Read) return;
        if (_engine is not { IsPlaying: true }) return;
        if (_lanes.IsEditingPoint) return;
        foreach (var t in _tracks) LoadAutoPoints(t);
        _lanes.InvalidateVisual();
    }
    internal IReadOnlyList<TrackVM> Tracks => _tracks;
    internal double RowH => RowHeight;

    /// <summary>When on, the grid scrolls with the playhead during playback according to FollowMode.</summary>
    public bool FollowPlayhead { get; set; } = true;
    public PlayheadFollowMode FollowMode { get; set; } = PlayheadFollowMode.Paged;

    /// <summary>Snap the view to the playhead now (called when Follow is switched on so the
    /// grid jumps to the cursor immediately, without waiting for the next playback tick).</summary>
    public void RecenterOnPlayhead()
    {
        UpdateFollowScroll(forceRecenter: true);
        _ruler.InvalidateVisual();
        _lanes.InvalidateVisual();
        _overlay.InvalidateVisual();
        _footerLanes.InvalidateVisual();
    }

    private bool UpdateFollowScroll(bool forceRecenter = false)
    {
        double laneWidth = _lanes.Bounds.Width;
        if (laneWidth <= 0) laneWidth = (_scroller?.Bounds.Width ?? 0) - HeaderW;
        if (laneWidth <= 0 || _pixelsPerBeat <= 0) return false;
        double viewportBeats = laneWidth / _pixelsPerBeat;

        double target = _scrollBeats;

        switch (FollowMode)
        {
            case PlayheadFollowMode.ContinuousLeft:
                // Mode 1: Continuous from Start - playhead stays fixed at the left edge while grid scrolls
                target = Math.Max(0.0, _playheadBeats);
                break;

            case PlayheadFollowMode.ContinuousCenter:
                // Mode 3: Continuous Centered - playhead stays centered while grid scrolls once it reaches midpoint
                target = Math.Max(0.0, _playheadBeats - viewportBeats / 2.0);
                break;

            case PlayheadFollowMode.Paged:
            default:
                // Mode 2: Page Jump (Default) - playhead advances across screen, jumps to next page on reaching edge
                if (forceRecenter || _playheadBeats >= _scrollBeats + viewportBeats || _playheadBeats < _scrollBeats)
                {
                    double pageIndex = Math.Floor(_playheadBeats / Math.Max(1.0, viewportBeats));
                    target = Math.Max(0.0, pageIndex * viewportBeats);
                }
                break;
        }

        // Dynamically expand total timeline capacity so we never hit an artificial scroll boundary
        double requiredTotal = Math.Max(target + viewportBeats + 64, _playheadBeats + viewportBeats + 64);
        if (requiredTotal > _totalBeats)
        {
            _totalBeats = Math.Ceiling(requiredTotal / _beatsPerBar) * _beatsPerBar;
            SyncScroll(laneWidth);
        }

        if (Math.Abs(target - _scrollBeats) < 1e-4) return false;
        _scrollBeats = target;
        _hScroll.Maximum = Math.Max(0, _totalBeats - viewportBeats);
        _hScroll.Value = target;
        return true;
    }

    public void SetPlayhead(double beats)
    {
        _playheadBeats = beats;
        // While following, the view scrolls under the playhead — the lane surface (clips /
        // waveforms) and ruler must repaint along with the overlay.
        bool scrolled = false;
        if (FollowPlayhead)
        {
            scrolled = UpdateFollowScroll(forceRecenter: false);
        }
        if (scrolled)
        {
            Redraw();
        }
        else
        {
            _overlay.InvalidateVisual();
            _ruler.InvalidateVisual();
        }
        // Converge the loop highlight with the engine (~30 Hz) so transport-bar
        // toggles/changes reflect here too — except mid-drag, where we show a preview.
        if (!_loopDragging && _engine is { } eng)
        {
            bool on = eng.LoopEnabled; double s = eng.LoopStart, e = eng.LoopEnd;
            if (on != _loopActive || s != _loopS || e != _loopE)
            {
                _loopActive = on; _loopS = s; _loopE = e;
                _ruler.InvalidateVisual();
                _overlay.InvalidateVisual();
            }
        }
        // Only the overlay (playhead + loop band) moves each tick — the lane surface
        // beneath is unchanged, so leave it be to avoid a full clip/waveform repaint.
        _ruler.InvalidateVisual();
        _overlay.InvalidateVisual();
        _footerLanes.InvalidateVisual();

        // Automation mode: an empty lane draws a baseline at the param's live value, so it must
        // follow a knob turned in the device. Redraw the lanes only when a visible empty lane's
        // baseline actually changed (params with points are automation-driven — no baseline).
        if (_automationMode) RefreshAutoBaselines();
    }

    // Repaint the lane surface when a visible un-automated (empty-lane) target's live value
    // changed, so the dashed baseline follows the device knob in real time.
    private void RefreshAutoBaselines()
    {
        double sig = 17.0;
        var (rLo, rHi) = VisibleRowRange();
        for (int i = rLo; i <= rHi && i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            if (t.AutoPoints.Count != 0) continue;   // only empty lanes show a baseline
            sig = sig * 31.0 + (AutoCurrent(t) + 3.0) * (i * 7 + 1);
        }
        if (sig != _autoBaselineSig) { _autoBaselineSig = sig; _lanes.InvalidateVisual(); }
    }

    // ---- loop region authoring -------------------------------------------

    /// <summary>Live preview while dragging a loop range on the ruler (no engine write).</summary>
    private void SetLoopPreview(double a, double b)
    {
        double eff = Math.Max(0.0625, EffectiveSnapBeats);
        _loopDragging = true;
        _loopActive = true;
        _loopS = Math.Max(0, Snap(Math.Min(a, b)));
        _loopE = Math.Max(_loopS + eff, Snap(Math.Max(a, b)));
        Redraw();
    }

    /// <summary>Commit the current drag preview to the engine + notify the transport bar.</summary>
    private void CommitLoopPreview()
    {
        _loopDragging = false;
        SetLoopRegion(_loopS, _loopE);
    }

    private void CancelLoopPreview() { _loopDragging = false; }

    public void SeekTo(double beat)
    {
        double b = Math.Max(0.0, Snap(beat));
        _engine?.Seek(b);
        SetPlayhead(b);
    }

    /// <summary>Enable looping over [start,end] (snapped). Notifies the transport bar.</summary>
    internal void SetLoopRegion(double startBeat, double endBeat)
    {
        if (_engine is null) return;
        double eff = Math.Max(0.0625, EffectiveSnapBeats);
        double s = Math.Max(0, Snap(Math.Min(startBeat, endBeat)));
        double e = Math.Max(s + eff, Snap(Math.Max(startBeat, endBeat)));
        _engine.SetLoop(true, s, e);
        _loopActive = true; _loopS = s; _loopE = e; _loopDragging = false;
        Redraw();
        LoopChanged?.Invoke();
    }

    /// <summary>Loop over the beat span of the current clip selection. False if nothing selected.</summary>
    public bool LoopSelection()
    {
        if (_engine is null || _sel.Count == 0) return false;
        double min = double.MaxValue, max = double.MinValue;
        foreach (var t in _tracks)
            foreach (var c in t.Clips)
                if (_sel.Contains((t.Id, c.ClipIndex)))
                { min = Math.Min(min, c.StartBeat); max = Math.Max(max, c.StartBeat + c.LengthBeats); }
        if (max <= min) return false;
        SetLoopRegion(min, max);
        return true;
    }

    /// <summary>Pushes fresh per-track meter readings into the header meters (~30 Hz, M6-2),
    /// and refreshes the volume/pan controls so automation (playback or scrub) moves them live.</summary>
    public void UpdateMeters()
    {
        if (_engine is not { } eng) return;
        try
        {
            foreach (var (id, bar) in _meters.ToArray())
            {
                if (eng.TryGetTrackMeter(id, out var m)) bar.Push(m);
                else bar.Reset();
            }
            int n = eng.TrackCount;
            for (int i = 0; i < n; i++)
            {
                if (!eng.TryGetTrackInfo(i, out var ti)) continue;
                // Follow the live (automated) value; don't fight the user mid-drag.
                if (_volFaders.TryGetValue(ti.Id, out var f) && !f.Dragging && Math.Abs(f.Value - ti.Volume) > 1e-3)
                {
                    f.Value = ti.Volume;
                    if (_volDb.TryGetValue(ti.Id, out var db))
                        db.Text = ti.Volume <= 0.0011 ? "-∞" : AudioMath.LinToDb(ti.Volume).ToString("0.0");
                }
                if (_panBars.TryGetValue(ti.Id, out var p) && !p.Dragging && Math.Abs(p.Pan - ti.Pan) > 1e-3)
                    p.Pan = ti.Pan;
            }
        }
        catch { }
    }

    // Instrument tracks show their instrument (plugin) name; audio/return use a default.
    private static string TrackDisplayName(IAudioEngine eng, int id, bool isInstrument, bool isReturn)
    {
        if (isReturn) return $"Return {eng.TrackReturnIndex(id) + 1}";
        if (isInstrument)
        {
            string inst = eng.DeviceName(id, -1);   // Synth / Sampler / plugin name
            return string.IsNullOrWhiteSpace(inst) ? "Inst " + id : inst;
        }
        return "Audio " + id;
    }

    private void Zoom(double factor)
    {
        _pixelsPerBeat = Math.Clamp(_pixelsPerBeat * factor, 4, 240);
        SyncScroll(_lanes.Bounds.Width);
        Redraw();
    }

    // Zoom horizontally while keeping the beat under the cursor fixed (⌘/Ctrl+wheel).
    internal void ZoomAtBeat(double factor, double beatAnchor)
    {
        double old = _pixelsPerBeat;
        _pixelsPerBeat = Math.Clamp(_pixelsPerBeat * factor, 4, 240);
        if (Math.Abs(_pixelsPerBeat - old) < 1e-9) return;
        _scrollBeats = Math.Max(0, beatAnchor - (beatAnchor - _scrollBeats) * (old / _pixelsPerBeat));
        SyncScroll(_lanes.Bounds.Width);   // clamps _scrollBeats to range + syncs the scrollbar
        Redraw();
    }

    // Inclusive track-row range currently on screen, given the vertical scroll — the lane
    // render skips rows outside it so off-screen tracks pay no clip/waveform cost. A one-row
    // bleed each side keeps partially-scrolled rows fully painted. Returns (0,-1) when empty.
    internal (int lo, int hi) VisibleRowRange()
    {
        if (_tracks.Count == 0) return (0, -1);
        double offY = _scroller?.Offset.Y ?? 0;
        double vpH = _scroller is { } s && s.Viewport.Height > 0 ? s.Viewport.Height : _lanes.Bounds.Height;
        int tiLo = GetTrackIndexAtY(offY);
        int tiHi = GetTrackIndexAtY(offY + vpH);
        int lo = Math.Max(0, (tiLo >= 0 ? tiLo : 0) - 1);
        int hi = Math.Min(_tracks.Count - 1, (tiHi >= 0 ? tiHi : _tracks.Count - 1) + 1);
        return (lo, hi);
    }

    private void Redraw()
    {
        _ruler.InvalidateVisual();
        _lanes.InvalidateVisual();
        _overlay.InvalidateVisual();   // playhead/loop band track scroll+zoom+loop edits
        _footerLanes.InvalidateVisual();
    }

    /// <summary>Rebuilds the track/clip model + headers from the engine. Pass
    /// <paramref name="rebuildHeaders"/>=false to refresh only the clip data + lane drawing
    /// (used during recording, so the header controls — the input combobox — and any open
    /// context menu survive the 60 Hz tick instead of being torn down each frame).</summary>
    public void Refresh(bool rebuildHeaders = true)
    {
        // Peak reuse (⑤): on a live refresh (no header rebuild) the audio content of existing
        // clips can't change — recording appends a separate take drawn elsewhere, and the live
        // MIDI-drag path only touches a MIDI clip. So carry each audio clip's already-fetched
        // peaks over instead of recomputing 2048 buckets per clip on every 60 Hz tick. A full
        // Refresh (after an edit) always refetches, so trims/reverses/warps stay correct.
        Dictionary<(int track, int clip), ClipVM>? oldClips = null;
        if (!rebuildHeaders)
        {
            oldClips = new();
            foreach (var t in _tracks)
                foreach (var c in t.Clips)
                    if (!c.IsMidi && c.Peaks is not null) oldClips[(t.Id, c.ClipIndex)] = c;
        }
        _tracks.Clear();
        _returns.Clear();
        if (_engine is { } eng)
        {
            int n = eng.TrackCount;
            for (int i = 0; i < n; i++)
            {
                if (!eng.TryGetTrackInfo(i, out var ti)) continue;
                // Stored colour (set via the header menu) wins; otherwise auto by position.
                int effColor = EffectiveColorIndex(eng, ti.Id);
                // A stored name (set via the header menu) wins; otherwise the derived default.
                string storedName = eng.GetTrackName(ti.Id);
                var tvm = new TrackVM
                {
                    Id = ti.Id,
                    IsInstrument = ti.IsInstrument,
                    IsReturn = ti.IsReturn,
                    IsGroup = ti.IsGroup,
                    GroupId = ti.GroupId,
                    ColorIndex = effColor,
                    Muted = ti.Muted != 0,
                    Soloed = ti.Soloed != 0,
                    Armed = ti.Armed != 0,
                    Frozen = !ti.IsReturn && !ti.IsGroup && eng.IsTrackFrozen(ti.Id),   // M7
                    LiveRole = FreezeRole?.Invoke(ti.Id) ?? 0,                          // live-freeze (v1.1)
                    Name = storedName.Length > 0 ? storedName
                         : ti.IsGroup ? "Group"
                         : TrackDisplayName(eng, ti.Id, ti.IsInstrument, ti.IsReturn),
                    Volume = ti.Volume,
                    Pan = ti.Pan,
                };
                if (_automationMode)
                {
                    if (_autoTargets.TryGetValue(ti.Id, out var sel))
                        (tvm.AutoTarget, tvm.AutoDeviceIndex, tvm.AutoParamIndex, tvm.AutoParamId) = sel;
                    tvm.AutoLabel = AutoLabelFor(tvm);
                    LoadAutoPoints(tvm);

                    if (_subLaneState.TryGetValue(ti.Id, out var subLanes) && subLanes.Count > 0)
                    {
                        tvm.AutoExpanded = true;
                        foreach (var sl in subLanes)
                        {
                            tvm.SubLanes.Add(sl);
                            LoadSubLanePoints(tvm, sl);
                        }
                    }
                }
                for (int c = 0; c < ti.ClipCount; c++)
                {
                    if (!eng.TryGetClipInfo(ti.Id, c, out var ci)) continue;
                    var cvm = new ClipVM
                    {
                        TrackId = ti.Id,
                        ClipIndex = c,
                        StartBeat = ci.StartBeat,
                        LengthBeats = ci.LengthBeats,
                        IsMidi = ci.IsMidi,
                        Active = ci.IsActive,
                        Name = eng.GetClipName(ti.Id, c),
                    };
                    if (ci.IsMidi)
                    {
                        cvm.Notes = eng.GetClipNotes(ti.Id, c);
                    }
                    else if (oldClips is not null
                             && oldClips.TryGetValue((ti.Id, c), out var prev)
                             && Math.Abs(prev.StartBeat - ci.StartBeat) < 1e-9
                             && Math.Abs(prev.LengthBeats - ci.LengthBeats) < 1e-9
                             && prev.Peaks is not null
                             && prev.PeakCount > 0)
                    {
                        // Also verify that clip audio metadata didn't change (e.g. stretch/pitch/warp edit)
                        bool matches = true;
                        if (eng.TryGetAudioClipInfo(ti.Id, c, out var ai))
                        {
                            if (prev.WarpEnabled != (ai.WarpEnabled != 0)
                                || Math.Abs(prev.SourceOffsetFrames - ai.SourceOffsetFrames) > 1e-4
                                || Math.Abs(prev.WarpPlayStart - ai.WarpPlayStart) > 1e-4
                                || Math.Abs(prev.WarpPlayEnd - ai.WarpPlayEnd) > 1e-4
                                || Math.Abs(prev.WarpBeats - ai.WarpBeats) > 1e-4)
                            {
                                matches = false;
                            }
                        }
                        if (matches)
                        {
                            cvm.Peaks = prev.Peaks;
                            cvm.PeakCount = prev.PeakCount;
                            cvm.WarpEnabled = prev.WarpEnabled;
                            cvm.SourceOffsetFrames = prev.SourceOffsetFrames;
                            cvm.WarpPlayStart = prev.WarpPlayStart;
                            cvm.WarpPlayEnd = prev.WarpPlayEnd;
                            cvm.WarpBeats = prev.WarpBeats;
                        }
                        else
                        {
                            const int buckets = 8192;
                            var peaks = new float[buckets * 2];
                            cvm.PeakCount = eng.GetClipPeaks(ti.Id, c, peaks, buckets);
                            cvm.Peaks = peaks;
                            if (eng.TryGetAudioClipInfo(ti.Id, c, out var nai))
                            {
                                cvm.WarpEnabled = nai.WarpEnabled != 0;
                                cvm.SourceOffsetFrames = nai.SourceOffsetFrames;
                                cvm.WarpPlayStart = nai.WarpPlayStart;
                                cvm.WarpPlayEnd = nai.WarpPlayEnd;
                                cvm.WarpBeats = nai.WarpBeats;
                            }
                        }
                    }
                    else
                    {
                        // Request enough buckets that even wide/zoomed clips stay detailed
                        // (the lane draws one aggregated column per pixel).
                        const int buckets = 8192;
                        var peaks = new float[buckets * 2];
                        cvm.PeakCount = eng.GetClipPeaks(ti.Id, c, peaks, buckets);
                        cvm.Peaks = peaks;
                        if (eng.TryGetAudioClipInfo(ti.Id, c, out var nai))
                        {
                            cvm.WarpEnabled = nai.WarpEnabled != 0;
                            cvm.SourceOffsetFrames = nai.SourceOffsetFrames;
                            cvm.WarpPlayStart = nai.WarpPlayStart;
                            cvm.WarpPlayEnd = nai.WarpPlayEnd;
                            cvm.WarpBeats = nai.WarpBeats;
                        }
                    }
                    if (!ci.IsMidi)
                    {
                        var env = eng.GetClipVolumeEnvelope(ti.Id, c);
                        var (fIn, fOut) = GetClipFades(env, cvm.LengthBeats);
                        cvm.FadeInBeats = fIn;
                        cvm.FadeOutBeats = fOut;
                    }
                    tvm.Clips.Add(cvm);
                }
                (tvm.IsReturn ? _returns : _tracks).Add(tvm);
            }
        }

        ApplyHierarchy();   // reorder _tracks into group DFS order (depth + collapse-filtered)
        RecomputeTotalBeats();
        LoadMasterAuto();   // master-volume automation for the footer row (M9 follow-up)
        if (rebuildHeaders)
        {
            RebuildHeaders();
            RebuildFooterHeaders();
        }
        double minH = Math.Max(_scroller?.Bounds.Height ?? 600, GetTotalTracksHeight());
        _lanes.Height = minH;
        _overlay.Height = minH;   // keep the playhead/loop overlay the same span
        _headers.Height = minH;
        double footerH = (_returns.Count + 1) * FooterRowH;   // returns + master
        _footer.Height = footerH;
        _footerHeaders.Height = footerH;
        _footerLanes.InvalidateVisual();
        SyncScroll(_lanes.Bounds.Width);
        Redraw();
    }

    private void RecomputeTotalBeats()
    {
        double end = 0;
        foreach (var t in _tracks)
            foreach (var c in t.Clips)
                end = Math.Max(end, c.StartBeat + c.LengthBeats);
        _totalBeats = Math.Max(64, Math.Ceiling(end / _beatsPerBar) * _beatsPerBar + 16);
    }

    // Keep the horizontal scrollbar's range in sync with the visible width.
    internal void SyncScroll(double laneWidth)
    {
        double viewportBeats = laneWidth > 0 ? laneWidth / _pixelsPerBeat : _totalBeats;
        _hScroll.ViewportSize = viewportBeats;
        _hScroll.Maximum = Math.Max(0, _totalBeats - viewportBeats);
        if (_scrollBeats > _hScroll.Maximum) { _scrollBeats = _hScroll.Maximum; }
        _hScroll.Value = _scrollBeats;
    }

    internal void ScrollByBeats(double delta)
    {
        _scrollBeats = Math.Clamp(_scrollBeats + delta, 0, _hScroll.Maximum);
        _hScroll.Value = _scrollBeats;
        Redraw();
    }

    // Wheel zoom/scroll for the timeline. Shared by the lanes and the scroller so the empty
    // area below the tracks (when few lanes) reacts too. cursorX is the horizontal cursor
    // position in lane coordinates (for zoom-toward-cursor). Plain vertical is left unhandled
    // so the enclosing ScrollViewer can scroll the tracks.
    internal void HandleLaneWheel(PointerWheelEventArgs e, double cursorX)
    {
        var mods = e.KeyModifiers;

        // ⌘/Ctrl + wheel → horizontal zoom toward the cursor (DAW-standard).
        if ((mods & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            double beatAtCursor = _scrollBeats + cursorX / _pixelsPerBeat;
            ZoomAtBeat(WheelInput.ZoomFactor(e.Delta.Y, 1.25), beatAtCursor);
            e.Handled = true;
            return;
        }

        // Horizontal intent: a horizontal-dominant trackpad swipe, or Shift + wheel. Delta is
        // device-normalized to a pixel amount, then converted to beats at the current zoom so
        // it scrolls a consistent distance and can't fly to the ends.
        if (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
        {
            ScrollByBeats(-WheelInput.Pixels(e.Delta.X) / _pixelsPerBeat);
            e.Handled = true;
            return;
        }
        if ((mods & KeyModifiers.Shift) != 0)
        {
            ScrollByBeats(-WheelInput.Pixels(e.Delta.Y) / _pixelsPerBeat);
            e.Handled = true;
            return;
        }

        // Plain vertical wheel / trackpad → let the enclosing ScrollViewer scroll the tracks
        // vertically (don't handle, so the event bubbles up to it).
    }

    internal void OnClipDoubleClicked(int trackId, int clipIndex, bool isMidi)
    {
        if (isMidi) MidiClipActivated?.Invoke(trackId, clipIndex);
        else AudioClipActivated?.Invoke(trackId, clipIndex);
    }

    /// <summary>Currently selected track id, or -1 (M4.1-C: effect target).</summary>
    public int SelectedTrackId => SelTrackId;

    /// <summary>Explicitly select a track by ID.</summary>
    public void SelectTrack(int trackId)
    {
        SelTrackId = trackId;
        SelClipIndex = -1;
        UpdateHeaderSelection();
        Redraw();
    }

    /// <summary>Currently selected clip index within the selected track, or -1.</summary>
    public int SelectedClipIndex => SelClipIndex;

    /// <summary>Best track to auto-arm when Record is pressed with nothing armed:
    /// the selected track if it's a recordable (audio/instrument) row, else the
    /// last such row, else 0. Return buses (in <c>_returns</c>) are never a target.</summary>
    public int RecordArmTarget()
    {
        foreach (var t in _tracks)
            if (t.Id == SelTrackId) return t.Id;
        return _tracks.Count > 0 ? _tracks[^1].Id : 0;
    }

    /// <summary>Creates an empty MIDI clip on an instrument track at the bar containing
    /// <paramref name="beat"/> (double-click on empty lane space). Selects the new clip.</summary>
    internal void AddMidiClipAt(int trackId, double beat)
    {
        if (_engine is null) return;
        double bar = Math.Max(1, _beatsPerBar);
        double start = Math.Max(0, Math.Floor(beat / bar) * bar);
        int idx = _engine.AddMidiClip(trackId, start, bar);
        Refresh();
        if (idx >= 0) { SelTrackId = trackId; SelClipIndex = idx; UpdateHeaderSelection(); Redraw(); }
    }

    // The whole clip selection as an engine block: the multi-selection when non-empty,
    // else the primary clip, else empty. Copy/Cut/Duplicate/Paste all operate on this
    // block so a group of clips travels together (with its automation) preserving geometry.
    private (int trackId, int clipIndex)[] SelectionBlock()
    {
        if (_sel.Count > 0) return _sel.Select(s => (s.track, s.clip)).ToArray();
        if (SelTrackId > 0 && SelClipIndex >= 0) return new[] { (SelTrackId, SelClipIndex) };
        return Array.Empty<(int, int)>();
    }

    // Re-select exactly the clips the last block paste/duplicate produced.
    private void ReselectPlaced()
    {
        if (_engine is null) return;
        var placed = _engine.LastPlacedClips();
        if (placed.Length > 0) SetSelection(placed);
    }

    /// <summary>Duplicates the whole clip selection as one block, placed right after itself
    /// (keyboard Cmd+D / context menu). The copies become the new selection. False if none.</summary>
    public bool DuplicateSelectedClip()
    {
        if (_engine is null) return false;
        var sel = SelectionBlock();
        if (sel.Length == 0 || _engine.DuplicateClipBlock(sel) < 0) return false;
        Refresh();
        ReselectPlaced();
        return true;
    }

    /// <summary>Copies the whole clip selection (with its automation) to the block clipboard.</summary>
    public bool CopySelectedClip()
    {
        if (_engine is null) return false;
        var sel = SelectionBlock();
        return sel.Length > 0 && _engine.CopyClipBlock(sel);
    }

    /// <summary>Cuts the whole clip selection: copies the block to the clipboard, then removes
    /// the clips and their track automation.</summary>
    public bool CutSelectedClip()
    {
        if (_engine is null) return false;
        var sel = SelectionBlock();
        if (sel.Length == 0 || !_engine.CutClipBlock(sel)) return false;
        Select(-1, -1);
        Refresh();
        return true;
    }

    /// <summary>Pastes the block clipboard at <paramref name="atBeat"/>, remapped so its top
    /// track lands on <paramref name="trackId"/> (single clip → that track). Selects the paste.</summary>
    public bool PasteClipboardAt(int trackId, double atBeat)
    {
        if (_engine is null || trackId <= 0) return false;
        if (_engine.PasteClipBlock(atBeat, trackId) <= 0) return false;
        Refresh();
        ReselectPlaced();
        return true;
    }

    /// <summary>Keyboard paste: the block lands on its source tracks at the playhead.</summary>
    public bool PasteClipboard()
    {
        if (_engine is null || _engine.ClipboardBlockCount() == 0) return false;
        if (_engine.PasteClipBlock(Math.Max(0.0, _playheadBeats), -1) <= 0) return false;
        Refresh();
        ReselectPlaced();
        return true;
    }

    /// <summary>True when the block clipboard holds at least one clip (drives menu enablement).</summary>
    public bool HasClipClipboard => _engine?.ClipboardBlockCount() > 0;

    /// <summary>Clipboard clip kind: -1 empty, else 0 (block clipboard has content).</summary>
    public int ClipboardClipKind() => HasClipClipboard ? 0 : -1;

    internal double Snap(double beat)
    {
        double eff = EffectiveSnapBeats;
        return eff > 0 ? Math.Round(beat / eff) * eff : beat;
    }

    /// <summary>Snap unless Alt is held (Alt = free/fine positioning). Reads the live
    /// modifier state so it can be toggled mid-drag (req 1.2.1/2.4/3.1/8.2.1, 7.10).</summary>
    internal double SnapMaybe(double beat, KeyModifiers mods)
        => (mods & KeyModifiers.Alt) != 0 ? beat : Snap(beat);

    /// <summary>Snap to the grid, but let a nearby snap target (a neighbouring clip edge or
    /// marker within ~8px) win when it is closer — a magnetic snap (req 2.4). Alt bypasses all
    /// snapping. <paramref name="targets"/> are absolute beats.</summary>
    internal double SnapMagnetic(double raw, KeyModifiers mods, IReadOnlyList<double> targets)
    {
        if ((mods & KeyModifiers.Alt) != 0) return raw;
        double best = Snap(raw);
        double bestDist = Math.Abs(raw - best);
        double thresh = _pixelsPerBeat > 0 ? 8.0 / _pixelsPerBeat : 0;   // 8 logical px
        foreach (var t in targets)
        {
            double d = Math.Abs(raw - t);
            if (d <= thresh && d < bestDist) { best = t; bestDist = d; }
        }
        return best;
    }

    /// <summary>Absolute beats of every clip edge (start/end) plus the loop bounds, excluding
    /// the given clips — the magnetic-snap targets for a drag (req 2.4).</summary>
    internal List<double> SnapTargets(ISet<(int track, int clip)> exclude)
    {
        var list = new List<double>();
        foreach (var t in _tracks)
            foreach (var c in t.Clips)
            {
                if (exclude.Contains((t.Id, c.ClipIndex))) continue;
                list.Add(c.StartBeat);
                list.Add(c.StartBeat + c.LengthBeats);
            }
        if (_loopE > _loopS) { list.Add(_loopS); list.Add(_loopE); }
        return list;
    }

    /// <summary>Primary selection modifier: Cmd on macOS, Ctrl on Windows/Linux (req 7.11).
    /// On macOS Ctrl is reserved for the system context-menu click, so it never toggles.</summary>
    internal static bool IsPrimaryDown(KeyModifiers mods)
        => OperatingSystem.IsMacOS() ? (mods & KeyModifiers.Meta) != 0
                                     : (mods & KeyModifiers.Control) != 0;

    /// <summary>"bar.beat" label (1-indexed) for a beat position — drag tooltips (req 2.9).</summary>
    internal string FormatBarBeat(double beat)
    {
        double bpb = _beatsPerBar > 0 ? _beatsPerBar : 4;
        int bar = (int)Math.Floor(beat / bpb) + 1;
        double inBar = beat - (bar - 1) * bpb + 1;
        return $"{bar}.{inBar:0.##}";
    }

    // --- browser & file drag & drop (M7-5) ---
    private void OnLaneDragOver(object? sender, DragEventArgs e)
    {
        bool ok = BrowserView.IsAcceptableDrag(e);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        var p = e.GetPosition(_lanes);
        int ti = ok ? GetTrackIndexAtY(p.Y) : -1;
        if (ti != DropTrackIndex) { DropTrackIndex = ti; _lanes.InvalidateVisual(); }
        e.Handled = true;
    }

    private void OnLaneDrop(object? sender, DragEventArgs e)
    {
        DropTrackIndex = -1; _lanes.InvalidateVisual();
        var items = BrowserView.DroppedItems(e);
        if (items.Count == 0) return;
        var p = e.GetPosition(_lanes);
        int ti = GetTrackIndexAtY(p.Y);
        int trackId = (ti >= 0 && ti < _tracks.Count) ? _tracks[ti].Id : -1;
        double beat = Math.Max(0.0, Snap(_scrollBeats + p.X / _pixelsPerBeat));
        // One internal item drops as-is; multiple external files land the first on the
        // target track and each of the rest on its own new track (trackId -1), so they
        // never stack on top of each other.
        for (int i = 0; i < items.Count; i++)
            ItemDropped?.Invoke(items[i], i == 0 ? trackId : -1, beat);
        e.Handled = true;
    }
    internal void Select(int trackId, int clipIndex)
    {
        if (HasTimeSelection) ClearTimeSelection();   // clip- and time-selection are exclusive (1.2.5)
        SelTrackId = trackId;
        SelClipIndex = clipIndex;
        _sel.Clear();
        if (trackId > 0 && clipIndex >= 0) _sel.Add((trackId, clipIndex));
        UpdateHeaderSelection();
        RebuildFooterHeaders();   // returns/master carry the selection highlight too (1e)
        Redraw();
        TrackSelected?.Invoke(trackId);
    }

    /// <summary>True if the clip is part of the current multi-selection.</summary>
    internal bool IsSelected(int trackId, int clipIndex) => _sel.Contains((trackId, clipIndex));

    /// <summary>Whole current selection (for group move).</summary>
    internal IReadOnlyCollection<(int track, int clip)> Selection => _sel;

    /// <summary>Number of clips in the current multi-selection.</summary>
    internal int SelectionCount => _sel.Count;

    /// <summary>Cmd/Ctrl+click: toggle a clip in the multi-selection, leaving the rest intact
    /// (req 1.1.2). Clip- and automation-selection are mutually exclusive (req 1.2.5).</summary>
    internal void ToggleSelect(int trackId, int clipIndex)
    {
        if (trackId <= 0 || clipIndex < 0) return;
        if (HasTimeSelection) ClearTimeSelection();
        if (HasAutoSelection) ClearAutoSelection();
        if (_sel.Remove((trackId, clipIndex)))
        {
            // Removed: keep the primary stable unless we just removed it.
            if (SelTrackId == trackId && SelClipIndex == clipIndex)
            {
                if (_sel.Count > 0) { var f = _sel.First(); SelTrackId = f.track; SelClipIndex = f.clip; }
                else { SelTrackId = -1; SelClipIndex = -1; }
            }
        }
        else
        {
            _sel.Add((trackId, clipIndex));
            SelTrackId = trackId; SelClipIndex = clipIndex;   // the just-clicked clip is the primary
        }
        UpdateHeaderSelection();
        Redraw();
    }

    /// <summary>The live ClipVM for a (track, clip) pair, or null if it no longer exists.</summary>
    internal ClipVM? FindClipVM(int trackId, int clipIndex)
    {
        foreach (var t in _tracks)
            if (t.Id == trackId)
                foreach (var c in t.Clips)
                    if (c.ClipIndex == clipIndex) return c;
        return null;
    }

    /// <summary>Esc: cancel an in-progress lane gesture if one is active (no model change),
    /// otherwise clear any selection (req 1.2.6 / 2.11). Returns true if it did something.</summary>
    /// <summary>Abort any in-progress lane gesture with no model change (window focus loss).</summary>
    public void CancelActiveGesture() => _lanes.CancelGesture();

    internal bool EscapePressed()
    {
        if (_lanes.CancelGesture()) return true;
        bool had = _sel.Count > 0 || SelTrackId > 0 || HasAutoSelection || HasTimeSelection;
        if (HasAutoSelection) ClearAutoSelection();
        if (HasTimeSelection) ClearTimeSelection();
        if (_sel.Count > 0 || SelTrackId > 0) Select(-1, -1);
        return had;
    }

    // --- general time-range selection (req 1.2 / 1.2.3 / 5.3 / 4.1-range) -------
    private int RowOfTrack(int trackId)
    {
        for (int r = 0; r < _tracks.Count; r++) if (_tracks[r].Id == trackId) return r;
        return -1;
    }

    /// <summary>Sets (live, during a drag) the time-range selection over [a,b] beats and the
    /// visual rows spanned. Clears any clip selection — the two are mutually exclusive.</summary>
    internal void SetTimeSelection(double a, double b, int rowA, int rowB)
    {
        _timeSelStart = Math.Max(0, Math.Min(a, b));
        _timeSelEnd = Math.Max(a, b);
        _timeSelRowLo = Math.Max(0, Math.Min(rowA, rowB));
        _timeSelRowHi = Math.Min(Math.Max(0, _tracks.Count - 1), Math.Max(rowA, rowB));
        if (_sel.Count > 0) { _sel.Clear(); SelClipIndex = -1; UpdateHeaderSelection(); }
        Redraw();
    }

    internal void ClearTimeSelection()
    {
        if (_timeSelRowLo < 0) return;
        _timeSelRowLo = _timeSelRowHi = -1;
        Redraw();
    }

    // Non-return track IDs the time selection covers (target of range ops).
    private int[] TimeSelectionTrackIds()
    {
        var ids = new List<int>();
        for (int r = _timeSelRowLo; r <= _timeSelRowHi && r >= 0 && r < _tracks.Count; r++)
            if (!_tracks[r].IsReturn) ids.Add(_tracks[r].Id);
        return ids.ToArray();
    }

    /// <summary>Delete the covered clip content in the time selection (split at edges, no
    /// ripple; req 5.3). Consumes the key whenever a range is active.</summary>
    public bool DeleteTimeSelection()
    {
        if (_engine is null || !HasTimeSelection) return false;
        var ids = TimeSelectionTrackIds();
        if (ids.Length > 0) _engine.DeleteClipsInRange(ids, _timeSelStart, _timeSelEnd);
        Refresh();
        return true;
    }

    /// <summary>Cmd+E over a time selection: split every covered track's clips at the range
    /// edges, so the selected slice becomes its own clip(s). False when there's no range.</summary>
    public bool SplitTimeSelection()
    {
        if (_engine is null || !HasTimeSelection) return false;
        var ids = TimeSelectionTrackIds();
        if (ids.Length == 0 || !_engine.SplitClipsInRange(ids, _timeSelStart, _timeSelEnd)) return false;
        Refresh();
        return true;
    }

    /// <summary>Cmd+L over a time selection: loop exactly that range. False when there's no range.</summary>
    public bool LoopTimeSelection()
    {
        if (_engine is null || !HasTimeSelection) return false;
        SetLoopRegion(_timeSelStart, _timeSelEnd);
        return true;
    }

    /// <summary>Duplicate the time selection right after its end and move the selection onto
    /// the copy, so repeated presses chain (req 4.1-range).</summary>
    public bool DuplicateTimeSelection()
    {
        if (_engine is null || !HasTimeSelection) return false;
        var ids = TimeSelectionTrackIds();
        double len = ids.Length > 0 ? _engine.DuplicateRange(ids, _timeSelStart, _timeSelEnd) : 0;
        if (len <= 0) return false;
        _timeSelStart = _timeSelEnd;
        _timeSelEnd = _timeSelStart + len;
        Refresh();
        return true;
    }

    /// <summary>Shift+click rectangular clip selection: every clip between the primary-selection
    /// anchor and the clicked clip, across rows and beats (req 1.1.3).</summary>
    internal void ShiftSelectTo(int trackId, int clipIndex)
    {
        var target = FindClipVM(trackId, clipIndex);
        if (target is null) return;
        var anchor = SelClipIndex >= 0 ? FindClipVM(SelTrackId, SelClipIndex) : null;
        if (anchor is null) { Select(trackId, clipIndex); return; }
        int ra = RowOfTrack(SelTrackId), rt = RowOfTrack(trackId);
        if (ra < 0 || rt < 0) { Select(trackId, clipIndex); return; }
        int rlo = Math.Min(ra, rt), rhi = Math.Max(ra, rt);
        double blo = Math.Min(anchor.StartBeat, target.StartBeat);
        double bhi = Math.Max(anchor.StartBeat + anchor.LengthBeats, target.StartBeat + target.LengthBeats);
        ClearTimeSelection();
        _sel.Clear();
        for (int r = rlo; r <= rhi && r < _tracks.Count; r++)
            foreach (var c in _tracks[r].Clips)
                if (c.StartBeat < bhi - 1e-6 && c.StartBeat + c.LengthBeats > blo + 1e-6)
                    _sel.Add((_tracks[r].Id, c.ClipIndex));
        SelTrackId = trackId; SelClipIndex = clipIndex;
        UpdateHeaderSelection();
        Redraw();
    }

    /// <summary>Cmd+E: split every selected clip that spans the playhead, at the playhead
    /// (req 5.1). Returns false when nothing was actually split.</summary>
    public bool SplitSelectedAtPlayhead()
    {
        if (_engine is null || _sel.Count == 0) return false;
        double at = Math.Max(0, _playheadBeats);
        bool any = false;
        // Descending clip index per track: SplitClip inserts a new clip, so splitting a
        // higher index first never invalidates a lower one we still need to visit.
        foreach (var (track, clip) in _sel.OrderByDescending(c => c.clip).ToList())
        {
            var vm = FindClipVM(track, clip);
            if (vm is null) continue;
            if (at > vm.StartBeat + 1e-6 && at < vm.StartBeat + vm.LengthBeats - 1e-6)
            { _engine.SplitClip(track, clip, at); any = true; }
        }
        if (any) { Select(-1, -1); Refresh(); }
        return any;
    }

    /// <summary>Replaces the selection with the given clips (marquee). Does not open
    /// the detail panel — it only highlights; the primary becomes the first clip.</summary>
    internal void SetSelection(IEnumerable<(int track, int clip)> clips)
    {
        if (HasTimeSelection) ClearTimeSelection();
        _sel.Clear();
        foreach (var c in clips) _sel.Add(c);
        if (_sel.Count > 0) { var f = _sel.First(); SelTrackId = f.track; SelClipIndex = f.clip; }
        else { SelTrackId = -1; SelClipIndex = -1; }
        UpdateHeaderSelection();
        Redraw();
    }

    /// <summary>Deletes every clip in the multi-selection. False if nothing selected.</summary>
    /// <summary>Key 0: toggles the selected clip(s) active/inactive (clip deactivate).
    /// If any selected clip is active they all deactivate; otherwise they all activate. A
    /// deactivated clip stays on the timeline but plays nothing and is greyed. False if none.</summary>
    public bool ToggleSelectedClipsActive()
    {
        if (_engine is null) return false;
        var sel = SelectionBlock();
        if (sel.Length == 0) return false;
        // Deactivate if any is currently active, so a mixed selection turns fully off first.
        bool anyActive = false;
        foreach (var (track, clip) in sel)
            if (_engine.TryGetClipInfo(track, clip, out var ci) && ci.IsActive) { anyActive = true; break; }
        bool target = !anyActive, any = false;
        foreach (var (track, clip) in sel)
        {
            if (!_engine.TryGetClipInfo(track, clip, out _)) continue;   // tolerate a stale entry
            _engine.SetClipActive(track, clip, target);
            any = true;
        }
        if (any) Refresh();
        return any;
    }

    public bool DeleteSelectedClips()
    {
        if (_engine is null || _sel.Count == 0) return false;
        // Delete highest clip index first: DeleteClip shifts later indices within a
        // track, and cross-track order is irrelevant. A selection can hold stale entries
        // (it survives edits and isn't cleared in automation mode), so tolerate an index
        // the engine rejects rather than throwing out of the keystroke handler.
        bool any = false;
        foreach (var (track, clip) in _sel.OrderByDescending(c => c.clip))
            any |= _engine.TryDeleteClip(track, clip);
        _sel.Clear();
        SelTrackId = -1; SelClipIndex = -1;
        Refresh();
        return any;   // false when the selection was entirely stale — don't claim a delete
    }

    // Selection changed but the track set didn't: repaint only the two properties that
    // depend on selection (card background + name colour) on each existing header card,
    // instead of rebuilding the whole header column (faders, meters, combos) on every click.
    private void UpdateHeaderSelection()
    {
        foreach (var (id, h) in _headerCards)
        {
            bool selected = IsTrackMultiSelected(id);
            h.card.Background = selected ? SelHeaderBg
                : h.isGroup ? Brush("Brush.SurfaceRaised") : Brush("Brush.SurfaceCard");
            h.name.Foreground = Brush(selected ? "Brush.AccentBright" : "Brush.TextPrimary");
        }
    }

    private void RebuildHeaders()
    {
        _headers.Children.Clear();
        _meters.Clear();
        _volFaders.Clear();
        _panBars.Clear();
        _volDb.Clear();
        _headerCards.Clear();
        double curTop = 0;
        for (int i = 0; i < _tracks.Count; i++)
        {
            var card = BuildHeaderCard(_tracks[i]);
            double trackH = GetTrackHeight(_tracks[i].Id);
            card.Height = trackH;
            Canvas.SetLeft(card, 0);
            Canvas.SetTop(card, curTop); // pin to the same Y as the lane row
            _headers.Children.Add(card);
            curTop += trackH;
        }
        _headers.Children.Add(_trackDropLine);   // insertion marker (kept invisible until a drag)
        _headers.Height = Math.Max(_scroller?.Bounds.Height ?? 600, GetTotalTracksHeight());
    }

    private void UpdateHeaderPositionsLive()
    {
        double curTop = 0;
        for (int i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            double trackH = GetTrackHeight(t.Id);
            if (_headerCards.TryGetValue(t.Id, out var hInfo))
            {
                hInfo.card.Height = trackH;
                Canvas.SetTop(hInfo.card, curTop);
            }
            curTop += trackH;
        }
        double minH = Math.Max(_scroller?.Bounds.Height ?? 600, curTop);
        _headers.Height = minH;
        _lanes.Height = minH;
        _overlay.Height = minH;
        _lanes.InvalidateVisual();
        _overlay.InvalidateVisual();
    }

    private Control BuildHeaderCard(TrackVM t)
    {
        bool selected = IsTrackMultiSelected(t.Id);
        var (_, _, _, spineBrush) = ClipColors(t.ColorIndex);
        double trackH = GetTrackHeight(t.Id);

        var name = new TextBlock
        {
            Text = t.Name, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Foreground = selected ? Brush("Brush.AccentBright") : (ClipDarkTextAndWaveform ? new SolidColorBrush(Color.Parse("#101014")) : Brush("Brush.TextPrimary")),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var typeTag = TypeTag(t.IsGroup ? "GROUP" : t.IsInstrument ? "MIDI" : "AUDIO");

        int volTrackId = t.Id;
        var fader = new MiniFader(t.Volume) { VerticalAlignment = VerticalAlignment.Center, Default = 1.0 };
        var db = new TextBlock
        {
            FontSize = 10, Classes = { "Mono" }, FontWeight = FontWeight.Medium,
            Foreground = Brush("Brush.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center, Width = 32, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(6, 0, 0, 0),
        };
        void ShowDb(double v) => db.Text = v <= 0.0001 ? "-∞" : AudioMath.LinToDb(v).ToString("0.0");
        ShowDb(t.Volume);
        fader.ValueChanged += v => { _engine?.SetTrackVolume(volTrackId, (float)v); ShowDb(v); };
        fader.GestureBegin += () => _engine?.BeginAutomationWrite(volTrackId, AutomationTarget.Volume, -1, -1, "");
        fader.GestureEnd   += () => _engine?.EndAutomationWrite(volTrackId, AutomationTarget.Volume, -1, -1, "");
        _volFaders[t.Id] = fader; _volDb[t.Id] = db;

        var pan = new PanBar(t.Pan) { VerticalAlignment = VerticalAlignment.Center };
        pan.PanChanged  += p => _engine?.SetTrackPan(volTrackId, (float)p);
        pan.GestureBegin += () => _engine?.BeginAutomationWrite(volTrackId, AutomationTarget.Pan, -1, -1, "");
        pan.GestureEnd   += () => _engine?.EndAutomationWrite(volTrackId, AutomationTarget.Pan, -1, -1, "");
        _panBars[t.Id] = pan;

        bool anySolo = _tracks.Any(x => x.Soloed);
        bool soloMuted = anySolo && !t.Soloed;

        Control headerBody;
        if (trackH <= 28)
        {
            // Micro mode (Logic Pro ultra-compact track lane): single row with Name + M/S/Arm
            var microBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            microBtns.Children.Add(ChipToggle("M", t.Muted, danger: false, v => { _engine?.SetTrackMute(t.Id, v); Refresh(rebuildHeaders: false); }, isSoloMuted: soloMuted));
            microBtns.Children.Add(ChipToggle("S", t.Soloed, danger: false, v => { _engine?.SetTrackSolo(t.Id, v); Refresh(); }));
            if (!t.IsReturn && !t.IsGroup)
                microBtns.Children.Add(ChipToggle("●", t.Armed, danger: true, v => _engine?.SetTrackArmed(t.Id, v)));

            var microRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(microBtns, 1);
            microRow.Children.Add(name);
            microRow.Children.Add(microBtns);

            headerBody = new StackPanel { Margin = new Thickness(6 + t.Depth * 10, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center, Children = { microRow } };
        }
        else if (trackH <= 40)
        {
            // Compact mode: Line 1 (Name + Type tag), Line 2 (M/S/Arm + mini fader)
            var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            if (t.IsGroup)
            {
                var tri = new TextBlock
                {
                    Text = _collapsed.Contains(t.Id) ? "▸" : "▾", FontSize = 9,
                    Foreground = Brush("Brush.TextSecondary"), VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0), Cursor = new Cursor(StandardCursorType.Hand),
                    Background = Brushes.Transparent, Padding = new Thickness(2, 0),
                };
                tri.PointerPressed += (_, e) => { e.Handled = true; ToggleCollapse(t.Id); };
                Grid.SetColumn(tri, 0);
                nameRow.Children.Add(tri);
            }
            Grid.SetColumn(name, 1);
            Grid.SetColumn(typeTag, 2);
            nameRow.Children.Add(name);
            nameRow.Children.Add(typeTag);

            var miniBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            miniBtns.Children.Add(ChipToggle("M", t.Muted, danger: false, v => { _engine?.SetTrackMute(t.Id, v); Refresh(rebuildHeaders: false); }, isSoloMuted: soloMuted));
            miniBtns.Children.Add(ChipToggle("S", t.Soloed, danger: false, v => { _engine?.SetTrackSolo(t.Id, v); Refresh(); }));
            if (!t.IsReturn && !t.IsGroup)
                miniBtns.Children.Add(ChipToggle("●", t.Armed, danger: true, v => _engine?.SetTrackArmed(t.Id, v)));

            var compactRow2 = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 5, Margin = new Thickness(0, 2, 0, 0) };
            Grid.SetColumn(miniBtns, 0);
            Grid.SetColumn(fader, 1);
            Grid.SetColumn(db, 2);
            compactRow2.Children.Add(miniBtns);
            compactRow2.Children.Add(fader);
            compactRow2.Children.Add(db);

            headerBody = new StackPanel { Margin = new Thickness(6 + t.Depth * 10, 3, 6, 3), Children = { nameRow, compactRow2 } };
        }
        else
        {
            // Standard / Large mode: full 3 rows (Name, Routing & Buttons, Volume & Pan)
            var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            if (t.IsGroup)
            {
                var tri = new TextBlock
                {
                    Text = _collapsed.Contains(t.Id) ? "▸" : "▾", FontSize = 9,
                    Foreground = Brush("Brush.TextSecondary"), VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0), Cursor = new Cursor(StandardCursorType.Hand),
                    Background = Brushes.Transparent, Padding = new Thickness(2, 0),
                };
                tri.PointerPressed += (_, e) => { e.Handled = true; ToggleCollapse(t.Id); };
                Grid.SetColumn(tri, 0);
                nameRow.Children.Add(tri);
            }
            else if (t.Frozen || t.LiveRole != 0)
            {
                Control badge = t.LiveRole == 1
                    ? new Avalonia.Controls.Shapes.Path
                    {
                        Data = NotaIcons.ChainLink, Stretch = Stretch.None,
                        Stroke = FrozenAccent, StrokeThickness = 1.4,
                        Width = 14, Height = 14,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                    }
                    : new TextBlock
                    {
                        Text = "❄", FontSize = 11, Foreground = FrozenAccent,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                    };
                Grid.SetColumn(badge, 0);
                nameRow.Children.Add(badge);
            }
            Grid.SetColumn(name, 1);
            Grid.SetColumn(typeTag, 2);
            nameRow.Children.Add(name);
            nameRow.Children.Add(typeTag);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 5, 0, 0) };
            btnRow.Children.Add(ChipToggle("M", t.Muted, danger: false, v => { _engine?.SetTrackMute(t.Id, v); Refresh(rebuildHeaders: false); }, isSoloMuted: soloMuted));
            btnRow.Children.Add(ChipToggle("S", t.Soloed, danger: false, v => { _engine?.SetTrackSolo(t.Id, v); Refresh(); }));
            if (!t.IsReturn && !t.IsGroup)
                btnRow.Children.Add(ChipToggle("●", t.Armed, danger: true, v => _engine?.SetTrackArmed(t.Id, v)));

            bool isAudioTrack = !t.IsInstrument && !t.IsReturn && !t.IsGroup;
            Control row2 = btnRow;
            if (isAudioTrack || t.IsInstrument)
            {
                var combo = isAudioTrack ? BuildInputCombo(t.Id) : BuildMidiSourceCombo(t.Id);
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 5, 0, 0), ColumnSpacing = 3 };
                btnRow.Margin = new Thickness(0);
                Grid.SetColumn(btnRow, 0);
                Grid.SetColumn(combo, 1);
                g.Children.Add(btnRow);
                g.Children.Add(combo);
                row2 = g;
            }

            var volRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetColumn(fader, 0);
            Grid.SetColumn(db, 1);
            Grid.SetColumn(pan, 2);
            volRow.Children.Add(fader);
            volRow.Children.Add(db);
            volRow.Children.Add(pan);

            headerBody = new StackPanel { Margin = new Thickness(8 + t.Depth * 14, 6, 8, 6), Children = { nameRow, row2, volRow } };
        }

        var meter = new MeterBar { VerticalAlignment = VerticalAlignment.Stretch, Width = trackH <= 28 ? 5 : 8, Margin = new Thickness(0, trackH <= 28 ? 2 : 6, 3, trackH <= 28 ? 2 : 6) };
        _meters[t.Id] = meter;
        DockPanel.SetDock(meter, Dock.Right);
        var body = new DockPanel { LastChildFill = true, Children = { meter, headerBody } };

        int trackNum = _tracks.IndexOf(t) + 1;
        var numLabel = new TextBlock
        {
            Text = trackNum > 0 ? trackNum.ToString() : "",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("Brush.TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var numBadge = new Border
        {
            Width = 16,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x00, 0x00)),
            BorderBrush = Brush("Brush.BorderDefault"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = numLabel,
        };

        // 3.5px track-colour spine on the left edge.
        var spine = new Border { Width = 3.5, Background = spineBrush };
        var leftSpineGroup = new StackPanel { Orientation = Orientation.Horizontal, Children = { spine, numBadge } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(leftSpineGroup, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(leftSpineGroup);
        grid.Children.Add(body);

        var resizeGrip = new Border
        {
            Height = 8,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            ZIndex = 30,
        };
        bool isResizing = false;
        double resizeStartY = 0;
        double resizeStartH = trackH;
        resizeGrip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(resizeGrip).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    ResetTrackHeight(t.Id);
                    return;
                }
                isResizing = true;
                resizeStartY = e.GetPosition(_headers).Y;
                resizeStartH = GetTrackHeight(t.Id);
                e.Pointer.Capture(resizeGrip);
                e.Handled = true;
            }
        };
        resizeGrip.PointerMoved += (_, e) =>
        {
            if (isResizing)
            {
                double curY = e.GetPosition(_headers).Y;
                double delta = curY - resizeStartY;
                double newH = Math.Clamp(resizeStartH + delta, 24, 360);
                _customTrackHeights[t.Id] = newH;
                UpdateHeaderPositionsLive();
                e.Handled = true;
            }
        };
        resizeGrip.PointerReleased += (_, e) =>
        {
            if (isResizing)
            {
                isResizing = false;
                e.Pointer.Capture(null);
                RebuildHeaders();
                Redraw();
                e.Handled = true;
            }
        };
        resizeGrip.PointerCaptureLost += (_, _) =>
        {
            if (isResizing)
            {
                isResizing = false;
                RebuildHeaders();
                Redraw();
            }
        };

        Control cardContent = grid;
        if (_automationMode && t.AutoExpanded && t.SubLanes.Count > 0)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            grid.Height = GetTrackMainHeight(t.Id);
            stack.Children.Add(grid);
            foreach (var sl in t.SubLanes)
            {
                var slHeader = BuildSubLaneHeaderCard(t, sl);
                stack.Children.Add(slHeader);
            }
            cardContent = stack;
        }

        var cardRoot = new Grid();
        cardRoot.Children.Add(cardContent);
        cardRoot.Children.Add(resizeGrip);

        var card = new Border
        {
            Width = HeaderW,
            Height = trackH,
            ClipToBounds = true,
            Background = selected ? SelHeaderBg : (t.Frozen || t.LiveRole != 0) ? FrozenHeaderBg : t.IsGroup ? Brush("Brush.SurfaceRaised") : Brush("Brush.SurfaceCard"),
            BorderBrush = Brush("Brush.BorderDefault"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = cardRoot,
        };
        _headerCards[t.Id] = (card, name, t.IsGroup);   // for in-place selection repaint
        card.PointerPressed += (_, e) =>
        {
            // A click inside the input ComboBox must reach it (and not reselect the track,
            // which would rebuild the header and destroy the combo before its dropdown opens).
            if (e.Source is Visual vsrc && vsrc.FindAncestorOfType<ComboBox>(includeSelf: true) is not null) return;
            if (e.GetCurrentPoint(card).Properties.IsRightButtonPressed) { ShowTrackMenu(card, t.Id); return; }
            // Double-click an instrument track → open its instrument/plugin GUI (if any).
            if (e.ClickCount == 2 && t.IsInstrument)
            {
                try { _engine?.OpenPluginEditor(t.Id, -1); } catch { /* builtin/no editor: no-op */ }
                return;
            }
            // Ctrl/Cmd-click extends the multi-track selection (used to form a group).
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed
                && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            {
                ToggleTrackInSelection(t.Id); e.Handled = true; return;
            }
            _selTracks.Clear();   // a plain click resets the multi-selection to just this track
            // Grab the title strip (top row, clear of the M/S/arm chips + faders below) to
            // start a reorder drag; selection is deferred to release so the capture survives
            // (Select rebuilds the headers). Returns aren't reorderable — select them outright.
            if (!t.IsReturn && e.GetCurrentPoint(card).Properties.IsLeftButtonPressed
                && e.GetPosition(card).Y < 24)
            {
                _hdrDragId = t.Id;
                _hdrDragFrom = _tracks.FindIndex(x => x.Id == t.Id);
                _hdrDragging = false;
                _hdrDragStart = e.GetPosition(_headers);
                e.Pointer.Capture(card);
                return;
            }
            Select(t.Id, -1);
        };
        card.PointerMoved += (_, e) =>
        {
            if (_hdrDragId < 0) return;
            var p = e.GetPosition(_headers);
            if (!_hdrDragging && Math.Abs(p.Y - _hdrDragStart.Y) < 5) return;
            _hdrDragging = true;
            card.Cursor = new Cursor(StandardCursorType.SizeAll);
            card.Opacity = 0.55;
            int gap = Math.Clamp(GetTrackIndexAtY(p.Y), 0, _tracks.Count);
            _hdrDropGap = gap;
            Canvas.SetTop(_trackDropLine, GetTrackTop(gap) - 1);
            _trackDropLine.IsVisible = true;
        };
        card.PointerReleased += (_, e) =>
        {
            if (_hdrDragId < 0) return;
            int id = _hdrDragId;
            bool dragged = _hdrDragging;
            double dropY = e.GetPosition(_headers).Y;
            _hdrDragId = _hdrDragFrom = _hdrDropGap = -1;
            _hdrDragging = false;
            _trackDropLine.IsVisible = false;
            e.Pointer.Capture(null);
            card.Cursor = null;
            card.Opacity = 1.0;
            if (!dragged) { Select(id, -1); return; }   // never crossed the threshold → plain select
            HandleHeaderDrop(id, dropY);
        };
        return card;
    }

    private Control BuildSubLaneHeaderCard(TrackVM t, AutoSubLaneVM sl)
    {
        var (_, _, _, spineBrush) = ClipColors(t.ColorIndex);

        var subLaneCard = new Border
        {
            Width = HeaderW,
            Height = sl.Height,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x16, 0x1A)),
            BorderBrush = Brush("Brush.BorderSubtle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };

        // Left sub-lane branch indicator / spine
        var spine = new Border { Width = 3.5, Background = spineBrush };
        var branchGlyph = new TextBlock
        {
            Text = "↳",
            FontSize = 11,
            Foreground = Brush("Brush.TextMuted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var branchBadge = new Border
        {
            Width = 16,
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00)),
            BorderBrush = Brush("Brush.BorderDefault"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = branchGlyph,
        };
        var leftGroup = new StackPanel { Orientation = Orientation.Horizontal, Children = { spine, branchBadge } };

        string slLabel = AutoLabelFor(sl);
        sl.Label = slLabel;

        // SubLane target picker button
        var targetText = new TextBlock
        {
            Text = $"{slLabel} ▾",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var targetBtn = new Button
        {
            Content = targetText,
            Background = Brush("Brush.SurfaceRaised"),
            BorderBrush = Brush("Brush.BorderDefault"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2),
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        targetBtn.Click += (_, _) => ShowSubLaneTargetMenu(targetBtn, t, sl);

        // SubLane Arm button
        var armBtn = ChipToggle("●", AutoArmed(sl), danger: true, _ => ToggleAutoArm(sl.TrackId, sl.Target, sl.DeviceIndex, sl.ParamIndex, sl.ParamId));
        armBtn.VerticalAlignment = VerticalAlignment.Center;

        // SubLane + button
        var addBtn = new Button
        {
            Content = new TextBlock { Text = "+", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brush("Brush.TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0),
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(addBtn, "Add automation sub-lane");
        addBtn.Click += (_, _) => AddAutoSubLane(t);

        // SubLane - (remove) button
        var removeBtn = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 9, Foreground = Brush("Brush.TextMuted"), HorizontalAlignment = HorizontalAlignment.Center },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0),
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(removeBtn, "Remove this automation sub-lane");
        removeBtn.Click += (_, _) => RemoveAutoSubLane(t, sl);

        var rightBtns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { armBtn, addBtn, removeBtn },
        };

        var mainRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(targetBtn, 0);
        Grid.SetColumn(rightBtns, 1);
        mainRow.Children.Add(targetBtn);
        mainRow.Children.Add(rightBtns);

        var cardLayout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        Grid.SetColumn(leftGroup, 0);
        Grid.SetColumn(mainRow, 1);
        cardLayout.Children.Add(leftGroup);
        cardLayout.Children.Add(mainRow);

        subLaneCard.Child = cardLayout;
        return subLaneCard;
    }

    // Track context menu (Duplicate / Delete). Both go through the engine's
    // snapshot ops, so undo/redo covers them; the whole view refreshes after.
    private static readonly string[] TrackColorNames = { "Rust", "Amber", "Olive", "Sage", "Teal", "Slate", "Mauve", "Rose" };
    private static readonly string[] ShadeNames = { "", " (light)", " (dark)" };

    // Right-click on the empty space below the tracks: add or paste tracks.
    private void ShowEmptyAreaMenu(Control anchor)
    {
        if (_engine is null) return;
        var flyout = new MenuFlyout();

        var addInst = new MenuItem { Header = "Add Instrument Track" };
        addInst.Click += (_, _) =>
        {
            int nid = _engine.AddInstrumentTrack();
            if (nid > 0) Select(nid, -1);
            Refresh();
            SessionChanged?.Invoke();
            TrackSelected?.Invoke(SelTrackId);
        };

        var addAudio = new MenuItem { Header = "Add Audio Track" };
        addAudio.Click += (_, _) =>
        {
            int nid = _engine.AddAudioTrack();
            if (nid > 0) Select(nid, -1);
            Refresh();
            SessionChanged?.Invoke();
            TrackSelected?.Invoke(SelTrackId);
        };

        var addSampler = new MenuItem { Header = "Add Sampler Track" };
        addSampler.Click += (_, _) =>
        {
            int nid = _engine.AddSamplerInstrumentTrack();
            if (nid > 0) Select(nid, -1);
            Refresh();
            SessionChanged?.Invoke();
            TrackSelected?.Invoke(SelTrackId);
        };

        var paste = new MenuItem { Header = "Paste track", IsEnabled = _engine.HasTrackClipboard() };
        paste.Click += (_, _) =>
        {
            int nid = _engine.PasteTrack();
            if (nid > 0) Select(nid, -1);
            Refresh();
            SessionChanged?.Invoke();
            TrackSelected?.Invoke(SelTrackId);
        };

        flyout.Items.Add(addInst);
        flyout.Items.Add(addAudio);
        flyout.Items.Add(addSampler);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(paste);
        flyout.ShowAt(anchor, showAtPointer: true);
    }

    private void ShowTrackMenu(Control anchor, int trackId)
    {
        if (_engine is null) return;
        var flyout = new MenuFlyout();

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => PromptRenameTrack(anchor, trackId);
        var color = BuildColorSubmenu(trackId);

        // Insert Track Below submenu
        var insertBelow = new MenuItem { Header = "Insert Track Below" };
        var insInst = new MenuItem { Header = "Instrument Track" };
        insInst.Click += (_, _) =>
        {
            int nid = _engine.AddInstrumentTrack();
            if (nid > 0)
            {
                int trackIdx = _tracks.FindIndex(t => t.Id == trackId);
                if (trackIdx >= 0 && trackIdx + 1 < _tracks.Count)
                    _engine.MoveTrack(nid, trackIdx + 1);
                Select(nid, -1);
            }
            Refresh(); SessionChanged?.Invoke(); TrackSelected?.Invoke(SelTrackId);
        };
        var insAudio = new MenuItem { Header = "Audio Track" };
        insAudio.Click += (_, _) =>
        {
            int nid = _engine.AddAudioTrack();
            if (nid > 0)
            {
                int trackIdx = _tracks.FindIndex(t => t.Id == trackId);
                if (trackIdx >= 0 && trackIdx + 1 < _tracks.Count)
                    _engine.MoveTrack(nid, trackIdx + 1);
                Select(nid, -1);
            }
            Refresh(); SessionChanged?.Invoke(); TrackSelected?.Invoke(SelTrackId);
        };
        var insSampler = new MenuItem { Header = "Sampler Track" };
        insSampler.Click += (_, _) =>
        {
            int nid = _engine.AddSamplerInstrumentTrack();
            if (nid > 0)
            {
                int trackIdx = _tracks.FindIndex(t => t.Id == trackId);
                if (trackIdx >= 0 && trackIdx + 1 < _tracks.Count)
                    _engine.MoveTrack(nid, trackIdx + 1);
                Select(nid, -1);
            }
            Refresh(); SessionChanged?.Invoke(); TrackSelected?.Invoke(SelTrackId);
        };
        insertBelow.Items.Add(insInst);
        insertBelow.Items.Add(insAudio);
        insertBelow.Items.Add(insSampler);

        // Record input ▸ (audio tracks only): hardware, master, or another track's output
        // (internal resampling). The active source is ticked.
        bool isAudio = _tracks.Any(v => v.Id == trackId && !v.IsInstrument && !v.IsReturn);
        MenuItem? recInput = null;
        if (isAudio)
        {
            int cur = _engine.GetTrackRecordInput(trackId);
            recInput = new MenuItem { Header = "Record input" };
            MenuItem Src(string label, int source)
            {
                var mi = new MenuItem { Header = (cur == source ? "✓ " : "   ") + label };
                mi.Click += (_, _) => _engine.SetTrackRecordInput(trackId, source);
                return mi;
            }
            recInput.Items.Add(Src("Audio input (hardware)", 0));
            recInput.Items.Add(Src("Master", -1));
            recInput.Items.Add(new Separator());
            foreach (var v in _tracks)
                if (v.Id != trackId)
                    recInput.Items.Add(Src(v.Name.Length > 0 ? v.Name : $"Track {v.Id}", v.Id));
            foreach (var v in _returns)
                recInput.Items.Add(Src(v.Name.Length > 0 ? v.Name : $"Return {v.Id}", v.Id));
        }

        // MIDI from ▸ (instrument tracks only): play another instrument track's MIDI through
        // this track's instrument. The current source is ticked.
        bool isInstr = _tracks.Any(v => v.Id == trackId && v.IsInstrument);
        MenuItem? midiFrom = null;
        if (isInstr)
        {
            int curSrc = _engine.GetTrackMidiSource(trackId);
            midiFrom = new MenuItem { Header = "MIDI from" };
            MenuItem Src(string label, int src)
            {
                var mi = new MenuItem { Header = (curSrc == src ? "✓ " : "   ") + label };
                mi.Click += (_, _) => _engine.SetTrackMidiSource(trackId, src);
                return mi;
            }
            midiFrom.Items.Add(Src("None", -1));
            var others = _tracks.Where(v => v.Id != trackId && v.IsInstrument).ToList();
            if (others.Count > 0) midiFrom.Items.Add(new Separator());
            foreach (var v in others)
                midiFrom.Items.Add(Src(v.Name.Length > 0 ? v.Name : $"Track {v.Id}", v.Id));
        }

        var copy = new MenuItem { Header = "Copy track" };
        copy.Click += (_, _) => _engine.CopyTrack(trackId);
        var cut = new MenuItem { Header = "Cut track" };
        cut.Click += (_, _) =>
        {
            if (!_engine.CopyTrack(trackId)) return;
            _engine.RemoveTrack(trackId);
            if (SelTrackId == trackId) SelTrackId = -1;
            Refresh(); SessionChanged?.Invoke(); TrackSelected?.Invoke(SelTrackId);
        };
        var paste = new MenuItem { Header = "Paste track", IsEnabled = _engine.HasTrackClipboard() };
        paste.Click += (_, _) =>
        {
            int nid = _engine.PasteTrack();
            if (nid > 0) Select(nid, -1);
            Refresh(); SessionChanged?.Invoke(); TrackSelected?.Invoke(SelTrackId);
        };

        var dup = new MenuItem { Header = "Duplicate track" };
        dup.Click += (_, _) =>
        {
            int nid = _engine.DuplicateTrack(trackId);
            if (nid > 0) Select(nid, -1);
            Refresh();
            SessionChanged?.Invoke();
            TrackSelected?.Invoke(SelTrackId);
        };
        var del = new MenuItem { Header = "Delete track" };
        del.Click += (_, _) =>
        {
            _engine.RemoveTrack(trackId);
            if (SelTrackId == trackId) SelTrackId = -1;
            Refresh();
            SessionChanged?.Invoke();
            TrackSelected?.Invoke(SelTrackId);
        };

        // Group / Ungroup (submix). "Group" folds the multi-selection (or this track) into a
        // new group; "Ungroup" dissolves the group this track is/belongs to.
        var group = new MenuItem { Header = "Group tracks" };
        group.Click += (_, _) => GroupTracks(trackId);
        int ungroupId = GroupToUngroupFor(trackId);
        MenuItem? ungroup = null;
        if (ungroupId > 0)
        {
            ungroup = new MenuItem { Header = "Ungroup" };
            ungroup.Click += (_, _) => UngroupGroup(ungroupId);
        }

        var heightMenu = new MenuItem { Header = "Track Height" };
        var hCompact = new MenuItem { Header = "Compact (36px)" };
        hCompact.Click += (_, _) => SetTrackHeight(trackId, 36);
        var hNormal = new MenuItem { Header = "Normal (74px)" };
        hNormal.Click += (_, _) => SetTrackHeight(trackId, 74);
        var hMedium = new MenuItem { Header = "Medium (120px)" };
        hMedium.Click += (_, _) => SetTrackHeight(trackId, 120);
        var hLarge = new MenuItem { Header = "Large (200px)" };
        hLarge.Click += (_, _) => SetTrackHeight(trackId, 200);
        var hReset = new MenuItem { Header = "Reset to Default" };
        hReset.Click += (_, _) => ResetTrackHeight(trackId);
        var hResetAll = new MenuItem { Header = "Reset All Track Heights" };
        hResetAll.Click += (_, _) => ResetAllTrackHeights();
        heightMenu.Items.Add(hCompact);
        heightMenu.Items.Add(hNormal);
        heightMenu.Items.Add(hMedium);
        heightMenu.Items.Add(hLarge);
        heightMenu.Items.Add(new Separator());
        heightMenu.Items.Add(hReset);
        heightMenu.Items.Add(hResetAll);

        flyout.Items.Add(insertBelow);
        flyout.Items.Add(rename);
        flyout.Items.Add(color);
        flyout.Items.Add(heightMenu);
        if (recInput is not null) flyout.Items.Add(recInput);
        if (midiFrom is not null) flyout.Items.Add(midiFrom);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(group);
        if (ungroup is not null) flyout.Items.Add(ungroup);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(copy);
        flyout.Items.Add(cut);
        flyout.Items.Add(paste);
        flyout.Items.Add(dup);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(del);
        flyout.ShowAt(anchor, showAtPointer: true);
    }

    // Compact record-input selector for an audio track header: Ext (hardware), Master,
    // or another track / send. Reflects and writes the track's stored input source.
    private ComboBox BuildInputCombo(int trackId)
    {
        var cb = new ComboBox
        {
            FontSize = 9, Height = 16, MinHeight = 0, Padding = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,   // let the item span the box (In left / name right)
            Margin = new Thickness(0, 0, 12, 0),   // a touch shorter than the row
        };
        var sources = new List<int>();
        // "In" pinned left (faded), the source name pinned right, so the name stands out.
        void Add(string value, int src)
        {
            var inTb = new TextBlock { Text = "In", FontSize = 9, Foreground = Brush("Brush.TextTertiary"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            var valTb = new TextBlock { Text = value, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0) };
            DockPanel.SetDock(inTb, Dock.Left);
            // MinWidth so the closed selection box (which doesn't stretch its content in
            // Avalonia) still spreads In to the left edge and the name to the right.
            var content = new DockPanel { LastChildFill = true, MinWidth = 84, Children = { inTb, valTb } };
            cb.Items.Add(new ComboBoxItem { Content = content, Padding = new Thickness(6, 2), MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            sources.Add(src);
        }
        Add("Ext", 0);
        Add("Master", -1);
        foreach (var v in _tracks) if (v.Id != trackId) Add(v.Name.Length > 0 ? v.Name : $"Track {v.Id}", v.Id);
        foreach (var v in _returns) Add(v.Name.Length > 0 ? v.Name : $"Return {v.Id}", v.Id);
        int cur = _engine?.GetTrackRecordInput(trackId) ?? 0;
        int sel = sources.IndexOf(cur);
        cb.SelectedIndex = sel >= 0 ? sel : 0;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedIndex >= 0 && cb.SelectedIndex < sources.Count)
                _engine?.SetTrackRecordInput(trackId, sources[cb.SelectedIndex]);
        };
        return cb;
    }

    // Compact MIDI-input selector for an instrument track header: None, or another instrument
    // track whose MIDI this one receives ("MIDI In"). Mirrors the record-input combo.
    private ComboBox BuildMidiSourceCombo(int trackId)
    {
        var cb = new ComboBox
        {
            FontSize = 9, Height = 16, MinHeight = 0, Padding = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var sources = new List<int>();
        // "In" pinned left (faded), the source name pinned right.
        void Add(string value, int src)
        {
            var inTb = new TextBlock { Text = "In", FontSize = 9, Foreground = Brush("Brush.TextTertiary"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            var valTb = new TextBlock { Text = value, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0) };
            DockPanel.SetDock(inTb, Dock.Left);
            var content = new DockPanel { LastChildFill = true, MinWidth = 84, Children = { inTb, valTb } };
            cb.Items.Add(new ComboBoxItem { Content = content, Padding = new Thickness(6, 2), MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            sources.Add(src);
        }
        Add("None", -1);
        foreach (var v in _tracks) if (v.Id != trackId && v.IsInstrument) Add(v.Name.Length > 0 ? v.Name : $"Track {v.Id}", v.Id);
        int cur = _engine?.GetTrackMidiSource(trackId) ?? -1;
        int sel = sources.IndexOf(cur);
        cb.SelectedIndex = sel >= 0 ? sel : 0;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedIndex >= 0 && cb.SelectedIndex < sources.Count)
                _engine?.SetTrackMidiSource(trackId, sources[cb.SelectedIndex]);
        };
        return cb;
    }

    // Colour ▸ one submenu per base hue, each with normal / light / dark shades shown as
    // swatches, plus Auto. Picking sets the track's stored palette index.
    private MenuItem BuildColorSubmenu(int trackId)
    {
        var color = new MenuItem { Header = "Color" };
        for (int b = 0; b < PaletteBases; b++)
        {
            var baseItem = new MenuItem { Header = TrackColorNames[b], Icon = Swatch(b * PaletteShades) };
            for (int s = 0; s < PaletteShades; s++)
            {
                int ci = b * PaletteShades + s;
                var shade = new MenuItem { Header = TrackColorNames[b] + ShadeNames[s], Icon = Swatch(ci) };
                shade.Click += (_, _) => { _engine?.SetTrackColorIndex(trackId, ci); Refresh(); SessionChanged?.Invoke(); };
                baseItem.Items.Add(shade);
            }
            color.Items.Add(baseItem);
        }
        var autoColor = new MenuItem { Header = "Auto" };
        autoColor.Click += (_, _) => { _engine?.SetTrackColorIndex(trackId, -1); Refresh(); SessionChanged?.Invoke(); };
        color.Items.Add(new Separator());
        color.Items.Add(autoColor);
        return color;
    }

    // A small rounded colour swatch for the given palette index (menu icon).
    private static Control Swatch(int colorIndex)
        => new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3),
                        Background = new SolidColorBrush(TrackColorForIndex(colorIndex)) };

    // Inline rename popup for a track header (Enter commits, Esc cancels).
    private void PromptRenameTrack(Control anchor, int trackId)
    {
        if (_engine is null) return;
        var box = new TextBox { Text = _engine.GetTrackName(trackId), Width = 180, FontSize = 12 };
        var flyout = new Flyout { Content = box, Placement = PlacementMode.Bottom };
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { _engine.SetTrackName(trackId, box.Text ?? ""); Refresh(); flyout.Hide(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
        };
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.Post(() => { box.SelectAll(); box.Focus(); }, DispatcherPriority.Input);
    }

    // Bordered MIDI/AUDIO type-tag chip.
    private Control TypeTag(string text)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = 8, FontWeight = FontWeight.Bold,
            Foreground = Brush("Brush.TextTertiary"), VerticalAlignment = VerticalAlignment.Center,
        };
        var b = new Border
        {
            BorderThickness = new Thickness(1), BorderBrush = Brush("Brush.BorderDefault"),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = tb,
        };
        return b;
    }

    // A small stateful chip toggle (M/S/●). Logic Pro X color scheme with Solo-Mute blinking feedback.
    private Control ChipToggle(string text, bool initial, bool danger, Action<bool> onChanged, bool isSoloMuted = false)
    {
        bool state = initial;
        var tb = new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = new Border
        {
            Width = 20, Height = 18, CornerRadius = new CornerRadius(3.5),
            BorderThickness = new Thickness(1), Child = tb,
        };
        void Paint()
        {
            if (state)
            {
                if (text == "M")
                {
                    chip.Background = Brush("Brush.Accent"); // Logic Blue
                    chip.BorderBrush = Brush("Brush.AccentBright");
                    tb.Foreground = Brush("Brush.TextOnAccent");
                }
                else if (text == "S")
                {
                    chip.Background = Brush("Brush.Warning"); // Logic Solo Yellow
                    chip.BorderBrush = Brush("Brush.Warning");
                    tb.Foreground = Brush("Brush.BgSunken");
                }
                else if (danger || text == "●" || text == "R")
                {
                    chip.Background = Brush("Brush.Danger"); // Logic Record Red
                    chip.BorderBrush = Brush("Brush.DangerHover");
                    tb.Foreground = Brush("Brush.TextOnAccent");
                }
                else
                {
                    chip.Background = Brush("Brush.Accent");
                    chip.BorderBrush = Brush("Brush.AccentBright");
                    tb.Foreground = Brush("Brush.TextOnAccent");
                }
            }
            else if (text == "M" && isSoloMuted)
            {
                if (SoloBlinkService.BlinkOn)
                {
                    chip.Background = Brush("Brush.Accent");
                    chip.BorderBrush = Brush("Brush.AccentBright");
                    tb.Foreground = Brush("Brush.TextOnAccent");
                }
                else
                {
                    chip.Background = Brush("Brush.SurfaceCard");
                    chip.BorderBrush = Brush("Brush.BorderDefault");
                    tb.Foreground = Brush("Brush.TextSecondary");
                }
            }
            else
            {
                chip.Background = Brush("Brush.SurfaceCard");
                chip.BorderBrush = Brush("Brush.BorderDefault");
                tb.Foreground = Brush("Brush.TextSecondary");
            }
        }
        Paint();

        if (text == "M" && isSoloMuted)
        {
            Action<bool> blinkHandler = _ => Paint();
            SoloBlinkService.BlinkChanged += blinkHandler;
            chip.DetachedFromVisualTree += (_, _) => SoloBlinkService.BlinkChanged -= blinkHandler;
        }

        chip.PointerPressed += (_, e) => { e.Handled = true; state = !state; Paint(); onChanged(state); };
        return chip;
    }

    // ---- Return / Master pinned footer -----------------------------------
    private void RebuildFooterHeaders()
    {
        _footerHeaders.Children.Clear();
        double y = 0;
        foreach (var r in _returns)
        {
            var row = BuildSlimRow(r.Name, "", r.ColorIndex, r.Id, "Brush.SurfaceCard");
            Canvas.SetLeft(row, 0);
            Canvas.SetTop(row, y);
            _footerHeaders.Children.Add(row);
            y += FooterRowH;
        }
        // Master row: selectable (opens the master effect chain) via the reserved id.
        int masterId = _engine?.MasterTrackId ?? -1;
        string masterName = _engine is { } me2 && me2.GetTrackName(masterId) is { Length: > 0 } mn ? mn : "Master";
        var master = BuildSlimRow(masterName, "", -1, masterId, "Brush.SurfaceRaised", masterSpine: true);
        Canvas.SetLeft(master, 0);
        Canvas.SetTop(master, y);
        _footerHeaders.Children.Add(master);
    }

    private Control BuildSlimRow(string title, string sub, int colorIndex, int trackId, string bgKey, bool masterSpine = false)
    {
        var spineBrush = masterSpine ? KnoxPalette.Accent : ClipColors(colorIndex).content;
        var name = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush("Brush.TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
        var db = new TextBlock { Text = masterSpine ? "0.0" : "-inf", FontSize = 9, Classes = { "Mono" }, Foreground = Brush("Brush.TextTertiary"), VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(db, 1);
        row.Children.Add(name);
        row.Children.Add(db);

        var spine = new Border { Width = 3, Background = spineBrush };
        var content = new Border { Padding = new Thickness(8, 0), Child = row };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*") };
        Grid.SetColumn(spine, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(spine);
        grid.Children.Add(content);

        bool sel = trackId > 0 && trackId == SelTrackId;
        if (sel) name.Foreground = Brush("Brush.AccentBright");
        var card = new Border
        {
            Width = HeaderW, Height = FooterRowH, ClipToBounds = true,
            Background = sel ? SelHeaderBg : Brush(bgKey),
            BorderBrush = Brush("Brush.BorderDefault"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = grid,
        };
        if (trackId > 0)
            card.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(card).Properties.IsRightButtonPressed) { ShowFooterMenu(card, trackId); return; }
                Select(trackId, -1);
            };
        return card;
    }

    // Context menu for a send/return or the master row: rename + colour (the same edits
    // regular tracks get; returns/master have no clips to copy/duplicate).
    private void ShowFooterMenu(Control anchor, int trackId)
    {
        if (_engine is null) return;
        var flyout = new MenuFlyout();
        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += (_, _) => PromptRenameTrack(anchor, trackId);
        flyout.Items.Add(rename);
        flyout.Items.Add(BuildColorSubmenu(trackId));
        flyout.ShowAt(anchor, showAtPointer: true);
    }

    private IBrush Brush(string key)
        => this.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Magenta;

    // Small bordered "−"/"+" zoom chip (HANDOFF 1b ruler: 11px, bordered, radius 4).
    private static Button ZoomChip(string text) => new()
    {
        Content = text, Width = 24, Height = 18, MinWidth = 0, FontSize = 11,
        Padding = new Thickness(0),
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    // Fixed-width header-column chrome (SurfaceCard + right divider).
    private Border HeaderPanel(Control? child)
    {
        var b = new Border { Width = HeaderW, BorderThickness = new Thickness(0, 0, 1, 0), Child = child };
        b.BindResource(Border.BackgroundProperty, "Brush.SurfaceCard");
        b.BindResource(Border.BorderBrushProperty, "Brush.BorderDefault");
        return b;
    }

    // Power-curve shaping, mirroring native AutomationLane::shape (M9-D).
    internal static double Shape(double t, float curve)
        => curve == 0f ? t : Math.Pow(t, Math.Pow(2.0, -curve * 4.0));

    // ---- data colours (Neutral studio palette with crisp, legible grid lines) --
    // ---- data colours (Customizable studio palette with crisp, legible grid lines) --
    public static string GridColorTheme { get; set; } = "Default";
    public static string GridCustomColorHex { get; set; } = "#18191C";
    public static string ClipShape { get; set; } = "Rounded"; // Rounded, Rectangle, Pill
    public static string ClipInteriorStyle { get; set; } = "Default"; // Default, Glass, Flat
    public static bool ClipDarkTextAndWaveform { get; set; } = false;

    internal static IBrush LaneBgA { get; private set; } = new SolidColorBrush(Color.Parse("#18191C")); // Studio lane A (unified dark graphite)
    internal static IBrush LaneBgB { get; private set; } = new SolidColorBrush(Color.Parse("#141518")); // Studio lane B (alternating dark tone)
    private static readonly IBrush ChromeBg = new SolidColorBrush(Color.Parse("#808080")); // Chrome
    private static readonly IBrush RulerBg = new SolidColorBrush(Color.Parse("#121315"));  // Ruler bar
    private static readonly IPen RulerBorderPen = new Pen(new SolidColorBrush(Color.Parse("#232428")), 1); // 1px bottom border of ruler
    private static readonly IPen RulerBarPen = new Pen(new SolidColorBrush(Color.Parse("#3D3E46")), 1);    // Ruler bar marker
    private static readonly IPen RulerBeatPen = new Pen(new SolidColorBrush(Color.Parse("#27282D")), 1);   // Ruler beat marker
    private static readonly IPen RulerSnapPen = new Pen(new SolidColorBrush(Color.Parse("#1C1D21")), 1);   // Ruler sub-beat marker
    private static readonly IBrush RulerText = new SolidColorBrush(Color.Parse("#C8CAD4"));     // Bar numbers on dark ruler
    private static readonly IBrush RulerSubBeatText = new SolidColorBrush(Color.Parse("#7C8090"));// Sub-beat numbers
    internal static IPen TrackDividerPen { get; private set; } = new Pen(new SolidColorBrush(Color.Parse("#232428")), 1); // 1px horizontal track divider
    internal static IPen BeatPen { get; private set; } = new Pen(new SolidColorBrush(Color.Parse("#27282D")), 1); // Subtle beat line
    internal static IPen BarPen { get; private set; } = new Pen(new SolidColorBrush(Color.Parse("#3D3E46")), 1);   // Crisp distinct Bar line
    internal static IPen SnapPen { get; private set; } = new Pen(new SolidColorBrush(Color.Parse("#1C1D21")), 1); // Crisp subtle snap division lines
    internal static Avalonia.Media.Imaging.Bitmap? _bgBitmap;
    private static string _lastBgPath = "";
    internal static double _bgOpacity = 0.20;
    internal static string BgImageMode = "Stretch";

    public static void ApplySettings(Settings s)
    {
        if (s == null) return;
        GridColorTheme = s.ArrangementGridColor ?? "Default";
        GridCustomColorHex = s.ArrangementGridCustomColor ?? "#18191C";
        ClipShape = s.ClipShape ?? "Rounded";
        ClipInteriorStyle = s.ClipInteriorStyle ?? "Default";
        ClipDarkTextAndWaveform = s.ClipDarkTextAndWaveform;
        BgImageMode = string.IsNullOrEmpty(s.ArrangementBgImageMode) ? "Stretch" : s.ArrangementBgImageMode;

        Color baseA = GridColorTheme switch
        {
            "Obsidian" => Color.Parse("#101012"),
            "Charcoal" => Color.Parse("#1E1C1A"),
            "Navy" => Color.Parse("#121620"),
            "Slate" => Color.Parse("#161A1D"),
            "Cyber" => Color.Parse("#0C1014"),
            "Custom" => Color.TryParse(GridCustomColorHex, out var cc) ? cc : Color.Parse("#18191C"),
            _ => Color.Parse("#18191C")
        };
        Color baseB = Mix(baseA, Colors.Black, 0.18);
        Color divCol = Mix(baseA, Colors.White, 0.08);
        Color barCol = Mix(baseA, Colors.White, 0.22);
        Color beatCol = Mix(baseA, Colors.White, 0.12);
        Color snapCol = Mix(baseA, Colors.White, 0.05);

        LaneBgA = new SolidColorBrush(baseA);
        LaneBgB = new SolidColorBrush(baseB);
        TrackDividerPen = new Pen(new SolidColorBrush(divCol), 1);
        BarPen = new Pen(new SolidColorBrush(barCol), 1);
        BeatPen = new Pen(new SolidColorBrush(beatCol), 1);
        SnapPen = new Pen(new SolidColorBrush(snapCol), 1);

        _bgOpacity = Math.Clamp(s.ArrangementBgImageOpacity, 0.0, 1.0);
        if (_lastBgPath != s.ArrangementBgImagePath)
        {
            _lastBgPath = s.ArrangementBgImagePath ?? "";
            _bgBitmap?.Dispose();
            _bgBitmap = null;
            if (!string.IsNullOrWhiteSpace(_lastBgPath) && System.IO.File.Exists(_lastBgPath))
            {
                try { _bgBitmap = new Avalonia.Media.Imaging.Bitmap(_lastBgPath); } catch { }
            }
        }

        _clipColors.Clear();
    }
    private static readonly Color PlayheadColor = Color.Parse("#F5B838");                 // Golden yellow playhead
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(PlayheadColor);
    private static readonly IPen PlayheadPen = new Pen(PlayheadBrush, 1.5);
    private static readonly IPen PlayheadGlow = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xF5, 0xB8, 0x38)), 4);
    private static readonly IPen ClipSelBorder = new Pen(PlayheadBrush, 2);
    private static readonly IBrush EdgeHighlight = new SolidColorBrush(Color.Parse("#CDEEF8")); // resize-edge affordance
    private static readonly IBrush InactiveVeil = new SolidColorBrush(Color.FromArgb(0xB0, 0x12, 0x12, 0x12)); // deactivated-clip scrim
    // Drag position tooltip (req 2.9): dark pill + bright text near the cursor.
    private static readonly IBrush TooltipBg = new SolidColorBrush(Color.FromArgb(0xDC, 0x1C, 0x1C, 0x1C));
    private static readonly IBrush TooltipText = new SolidColorBrush(Color.Parse("#F0E4C8"));
    // Selected-track highlight: a subtle clean highlight on the lane row.
    // Header uses a clean raised surface.
    private static readonly IBrush SelWash = new SolidColorBrush(Color.FromArgb(0x22, 0x3D, 0x84, 0xE8));
    // Browser drag-over: the lane the drop would land on glows (bright accent wash + edge).
    private static readonly IBrush DropWash = new SolidColorBrush(Color.FromArgb(0x28, 0xF0, 0xC0, 0x60));
    private static readonly IBrush DropEdge = new SolidColorBrush(Color.FromArgb(0xC0, 0xF0, 0xC0, 0x60));
    internal int DropTrackIndex = -1;   // -1 = no drag over; set by OnLaneDragOver, drawn by LaneControl
    // In-progress audio take (M-fix): audio clips only materialise on stop, so a
    // translucent red region grows from the take start to the playhead as feedback.
    private static readonly IBrush RecFill = new SolidColorBrush(Color.FromArgb(0x33, 0xD8, 0x56, 0x4B));
    private static readonly IPen   RecBorder = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xD8, 0x56, 0x4B)), 1.5);
    private static readonly IBrush RecText = new SolidColorBrush(Color.Parse("#F0928A"));
    // Live capture waveform inside the growing take region — a brighter red so it
    // reads clearly against the translucent RecFill.
    private static readonly IBrush RecWave = new SolidColorBrush(Color.FromArgb(0xE0, 0xF0, 0x92, 0x8A));
    // Marquee rubber-band (multi-select): accent wash + accent border.
    private static readonly IBrush MarqueeFill = new SolidColorBrush(Color.FromArgb(0x28, 0x7F, 0xCC, 0xE1));
    private static readonly IPen   MarqueePen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x7F, 0xCC, 0xE1)), 1);

    // Loop region: the transport-accent hue (AccentBright #F0C060). A solid brace
    // fills the ruler strip; a faint wash + edge lines mark the span over the lanes.
    private static readonly IBrush LoopBrace = new SolidColorBrush(Color.FromArgb(0x66, 0xF0, 0xC0, 0x60));
    private static readonly IBrush LoopBand = new SolidColorBrush(Color.FromArgb(0x14, 0xF0, 0xC0, 0x60));
    private static readonly IPen   LoopEdge = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xF0, 0xC0, 0x60)), 1);
    // Automation overlay (M9-A3): a scrim mutes the clip content, a dashed line marks
    // the flat baseline of an empty lane.
    private static readonly IBrush AutoScrim = new SolidColorBrush(Color.FromArgb(0x50, 0x0C, 0x0C, 0x0C));
    private static readonly IPen AutoBasePen = new Pen(new SolidColorBrush(Color.Parse("#6A6558")), 1)
        { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
    // Write-arm REC button (M9-C): red dot + "REC", lit when armed.
    private static readonly IBrush AutoArmOn = new SolidColorBrush(Color.Parse("#D8564B"));   // Brush.Danger-ish
    private static readonly IPen   AutoArmOff = new Pen(new SolidColorBrush(Color.Parse("#6A6558")), 1);
    private static readonly IBrush RecBgOff  = new SolidColorBrush(Color.Parse("#1E1B16"));   // sunken chip
    private static readonly IBrush RecBgOn   = new SolidColorBrush(Color.FromArgb(0x33, 0xD8, 0x56, 0x4B));
    private static readonly IBrush RecDotOff = new SolidColorBrush(Color.Parse("#6A6558"));
    private static readonly IBrush RecTextOff = new SolidColorBrush(Color.Parse("#9A9384"));
    // Hover feedback (M9-D): brass-accent brighter line + point ring under the cursor.
    private static readonly IPen   AutoHoverPen = new Pen(PlayheadBrush, 2.6);
    private static readonly IPen   AutoHoverRing = new Pen(PlayheadBrush, 1.5);
    // Automation range selection (Phase 2): teal band + edges over the picked time span,
    // distinct from the brass loop band so the two never read as the same thing.
    private static readonly IBrush AutoSelBand = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC2, 0xB0));
    private static readonly IPen   AutoSelEdge = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x4C, 0xC2, 0xB0)), 1);
    private static readonly IBrush SelHeaderBg = new SolidColorBrush(Color.Parse("#505050")); // Raised selection surface
    // Fade rendering: shaded attenuated triangle + brass curve + notch handles
    private static readonly IBrush FadeAreaFill = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00));
    private static readonly IPen   FadeCurvePen = new Pen(new SolidColorBrush(Color.FromArgb(0xEE, 0xF5, 0xB8, 0x38)), 1.5);
    private static readonly IBrush FadeHandleFill = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush FadeHandleHoverFill = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IPen   FadeHandleBorder = new Pen(new SolidColorBrush(Color.Parse("#000000")), 1.2);
    private static readonly IPen   FadeHandleHoverBorder = new Pen(new SolidColorBrush(Color.Parse("#000000")), 1.6);

    // Freeze (M7): cool ice tint for a frozen track's header + its snowflake glyph.
    private static readonly IBrush FrozenHeaderBg = new SolidColorBrush(Color.Parse("#1B2A33"));
    private static readonly IBrush FrozenAccent   = new SolidColorBrush(Color.Parse("#7FC7EC"));

    // Track palette (mirror Brush.Track1..8 + ReturnA/B), each base offered in 3 shades
    // (normal / lighter / darker) so a colour index is base*Shades + shade. Return buses
    // sit at ReturnColorBase..; clips derive fill @16%, border @50%, strip @20%, content full.
    internal const int PaletteShades = 3;
    internal static readonly int PaletteBases = KnoxPalette.TrackColors.Length;   // 8
    internal static readonly int ReturnColorBase = PaletteBases * PaletteShades;  // 24
    /// <summary>Auto colour index (shade 0) for the Nth non-return track.</summary>
    internal static int AutoTrackColorIndex(int nth) => (nth % PaletteBases) * PaletteShades;
    /// <summary>Colour index for a return bus (0/1).</summary>
    internal static int ReturnColorIndex(int returnIdx) => ReturnColorBase + (Math.Max(0, returnIdx) % KnoxPalette.ReturnColors.Length);

    /// <summary>The effective palette index for a track: its stored colour if set,
    /// otherwise the automatic colour for its position. Shared by every view so a
    /// user-picked colour shows consistently in the arrangement, mixer and detail.</summary>
    public static int EffectiveColorIndex(IAudioEngine eng, int trackId)
    {
        int stored = eng.GetTrackColorIndex(trackId);
        if (stored >= 0) return stored;
        int nth = 0;
        for (int i = 0; i < eng.TrackCount; i++)
        {
            if (!eng.TryGetTrackInfo(i, out var ti)) continue;
            if (ti.IsReturn) { if (ti.Id == trackId) return ReturnColorIndex(eng.TrackReturnIndex(ti.Id)); continue; }
            if (ti.Id == trackId) return AutoTrackColorIndex(nth);
            nth++;
        }
        return 0;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    /// <summary>The track colour for a palette index (shared with the clip editor, 1e).</summary>
    public static Color TrackColorForIndex(int idx)
    {
        if (idx >= ReturnColorBase)
            return KnoxPalette.ReturnColors[(idx - ReturnColorBase) % KnoxPalette.ReturnColors.Length];
        idx = ((idx % ReturnColorBase) + ReturnColorBase) % ReturnColorBase;
        var baseColor = KnoxPalette.TrackColors[(idx / PaletteShades) % PaletteBases];
        return (idx % PaletteShades) switch
        {
            1 => Mix(baseColor, Colors.White, 0.24),                 // lighter
            2 => Mix(baseColor, Color.FromRgb(0x14, 0x12, 0x10), 0.30), // darker
            _ => baseColor,
        };
    }

    private static readonly Dictionary<int, (IBrush fill, IPen border, IBrush strip, IBrush content)> _clipColors = new();
    private static (IBrush fill, IPen border, IBrush strip, IBrush content) ClipColors(int idx)
    {
        if (_clipColors.TryGetValue(idx, out var v)) return v;
        var c = TrackColorForIndex(idx);

        IBrush fillBrush;
        IPen borderPen;
        IBrush stripBrush;
        IBrush contentBrush;

        if (ClipInteriorStyle == "Glass")
        {
            // Translucent glass fill with glowing frosted border
            var glassFill = Color.FromArgb(0x45, c.R, c.G, c.B);
            fillBrush = new SolidColorBrush(glassFill);
            borderPen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, (byte)Math.Min(255, c.R + 60), (byte)Math.Min(255, c.G + 60), (byte)Math.Min(255, c.B + 60))), 1.2);
            stripBrush = new SolidColorBrush(Color.FromArgb(0x65, (byte)(c.R * 0.85), (byte)(c.G * 0.85), (byte)(c.B * 0.85)));
            contentBrush = ClipDarkTextAndWaveform ? new SolidColorBrush(Color.Parse("#101014")) : new SolidColorBrush(Colors.White);
        }
        else if (ClipInteriorStyle == "Flat")
        {
            // Solid, vibrant flat fill
            var flatFill = Color.FromRgb((byte)(c.R * 0.78), (byte)(c.G * 0.78), (byte)(c.B * 0.78));
            fillBrush = new SolidColorBrush(flatFill);
            borderPen = new Pen(new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.60), (byte)(c.G * 0.60), (byte)(c.B * 0.60))), 1);
            stripBrush = new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.65), (byte)(c.G * 0.65), (byte)(c.B * 0.65)));
            contentBrush = ClipDarkTextAndWaveform ? new SolidColorBrush(Color.Parse("#101014")) : new SolidColorBrush(Colors.White);
        }
        else
        {
            // Default Studio Contrast
            var bodyColor = Color.FromRgb(
                (byte)Math.Clamp(0x18 + c.R * 0.16, 0, 255),
                (byte)Math.Clamp(0x1A + c.G * 0.16, 0, 255),
                (byte)Math.Clamp(0x22 + c.B * 0.16, 0, 255));
            fillBrush = new SolidColorBrush(bodyColor);
            borderPen = new Pen(new SolidColorBrush(Mix(c, Colors.White, 0.18)), 1);
            stripBrush = new SolidColorBrush(Mix(c, Color.FromRgb(0x12, 0x12, 0x16), 0.12));
            var contentColor = ClipDarkTextAndWaveform ? Color.Parse("#101014") : Mix(c, Colors.White, 0.72);
            contentBrush = new SolidColorBrush(contentColor);
        }

        v = (fillBrush, borderPen, stripBrush, contentBrush);
        _clipColors[idx] = v;
        return v;
    }
    private static IBrush Alpha(Color c, double a) => new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B));

    internal double BeatToX(double beat) => (beat - _scrollBeats) * _pixelsPerBeat;
    internal double XToBeat(double x) => x / Math.Max(1e-6, _pixelsPerBeat) + _scrollBeats;

    internal static (double fadeInBeats, double fadeOutBeats) GetClipFades(AutomationPoint[]? env, double clipLenBeats)
    {
        double fIn = 0;
        double fOut = 0;
        if (env == null || env.Length == 0 || clipLenBeats <= 0) return (0, 0);

        if (env.Length >= 2 && env[0].Beat <= 0.01 && env[0].Value < 0.1f)
        {
            fIn = Math.Clamp(env[1].Beat, 0, clipLenBeats);
        }

        int last = env.Length - 1;
        if (env.Length >= 2 && env[last].Beat >= clipLenBeats - 0.01 && env[last].Value < 0.1f)
        {
            fOut = Math.Clamp(clipLenBeats - env[last - 1].Beat, 0, clipLenBeats);
        }

        return (fIn, fOut);
    }

    internal static AutomationPoint[] BuildFadeEnvelope(double fIn, double fOut, double clipLenBeats)
    {
        var pts = new List<AutomationPoint>();
        if (fIn > 0.001)
        {
            pts.Add(new AutomationPoint(0.0, 0.0f));
            pts.Add(new AutomationPoint(Math.Min(fIn, clipLenBeats), 1.0f));
        }
        if (fOut > 0.001)
        {
            double fadeStart = Math.Max(0.0, clipLenBeats - fOut);
            if (pts.Count == 0 || pts[^1].Beat < fadeStart - 0.001)
            {
                pts.Add(new AutomationPoint(fadeStart, 1.0f));
            }
            pts.Add(new AutomationPoint(clipLenBeats, 0.0f));
        }
        return pts.ToArray();
    }
}

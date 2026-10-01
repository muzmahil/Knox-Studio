// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// A tactile rotary knob for device / instrument parameters (Logic / Studio Hardware style):
// a dark recessed gauge track with an illuminated LED value arc, and an elevated, satin-finished
// tactile cap with an inset bevel and needle indicator. Drag vertically to change (up = increase).

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Knox.App;

public sealed class Knob : Control
{
    private static readonly IBrush Groove = new SolidColorBrush(Color.Parse("#16171B"));     // dark track groove
    private static readonly IBrush TrackArc = new SolidColorBrush(Color.Parse("#545864"));   // neutral value arc
    private static readonly IBrush Brass = KnoxPalette.Accent;                               // Accent
    private static readonly IBrush CapShadow = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));
    private static readonly IBrush CapBezel = new SolidColorBrush(Color.Parse("#121316"));
    private static readonly IBrush PointerBrush = new SolidColorBrush(Color.Parse("#F2F3F5"));
    private static readonly IBrush PointerActiveBrush = new SolidColorBrush(Color.Parse("#6EAAFA"));

    private const double StartDeg = 135.0;   // down-left
    private const double SweepDeg = 270.0;   // clockwise to down-right

    /// <summary>Brass value arc instead of neutral grey.</summary>
    public bool Accent { get; init; }

    /// <summary>Overrides the value-arc colour (e.g. teal for modulation knobs).</summary>
    public IBrush? ArcColor { get; init; }

    private double _default = double.NaN;
    /// <summary>Value restored on double-click or right-click context menu (NaN = no reset).</summary>
    public double Default
    {
        get => _default;
        set
        {
            _default = value;
            EnsureDefaultContextMenu();
        }
    }

    public double Min { get; set; } = 0.0;
    public double Max { get; set; } = 1.0;
    public double Step { get; set; } = 0.0;
    public bool Bipolar { get; set; } = false;

    private double _value;
    private bool _drag;
    private double _lastY;

    public event Action<double>? ValueChanged;
    public event Action? GestureBegin;
    public event Action? GestureEnd;

    /// <summary>True while the user is dragging — live-follow refreshers skip it then.</summary>
    public bool Dragging => _drag;

    public double Value
    {
        get => _value;
        set
        {
            double v = Math.Clamp(value, Min, Max);
            if (Step > 0) v = Min + Math.Round((v - Min) / Step) * Step;
            _value = Math.Clamp(v, Min, Max);
            InvalidateVisual();
        }
    }

    public Knob(double value = 1.0, double max = 1.0)
    {
        Min = 0.0;
        Max = max <= 0 ? 1.0 : max;
        _value = Math.Clamp(value, Min, Max);
        Width = 38;
        Height = 38;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public Knob(double value, double min, double max, double defaultValue = double.NaN, double step = 0.0)
    {
        Min = min;
        Max = max <= min ? min + 1.0 : max;
        Default = defaultValue;
        Step = step;
        _value = Math.Clamp(value, Min, Max);
        Width = 38;
        Height = 38;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void EnsureDefaultContextMenu()
    {
        if (ContextMenu == null && !double.IsNaN(_default))
        {
            var menu = new ContextMenu();
            var resetItem = new MenuItem { Header = "Reset to Default" };
            resetItem.Click += (_, _) => ResetToDefault();
            menu.Items.Add(resetItem);
            ContextMenu = menu;
        }
    }

    public void ResetToDefault()
    {
        if (double.IsNaN(Default)) return;
        double d = Math.Clamp(Default, Min, Max);
        if (Math.Abs(d - _value) > 1e-6)
        {
            GestureBegin?.Invoke();
            _value = d;
            InvalidateVisual();
            ValueChanged?.Invoke(_value);
            GestureEnd?.Invoke();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed)
        {
            if (!double.IsNaN(Default) && ContextMenu == null)
            {
                EnsureDefaultContextMenu();
                ContextMenu?.Open(this);
                e.Handled = true;
            }
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        // Double-click restores the parameter's default.
        if (e.ClickCount == 2 && !double.IsNaN(Default))
        {
            ResetToDefault();
            e.Handled = true;
            return;
        }
        _drag = true;
        _lastY = e.GetPosition(this).Y;
        e.Pointer.Capture(this);
        GestureBegin?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_drag) return;
        double y = e.GetPosition(this).Y;
        double dy = _lastY - y;                       // up = increase
        _lastY = y;
        if (dy == 0) return;
        double span = Max - Min;
        bool fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        double speed = fine ? 560.0 : 140.0;
        double v = Math.Clamp(_value + dy / speed * span, Min, Max);
        if (Step > 0) v = Min + Math.Round((v - Min) / Step) * Step;
        v = Math.Clamp(v, Min, Max);
        if (Math.Abs(v - _value) < 1e-6) return;
        _value = v;
        InvalidateVisual();
        ValueChanged?.Invoke(_value);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag) GestureEnd?.Invoke();
        _drag = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;
        double span = Max - Min;
        bool fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        double step = Step > 0 ? (fine ? Step : Step * 2) : (fine ? span * 0.005 : span * 0.02);
        double v = Math.Clamp(_value + delta * step, Min, Max);
        if (Step > 0) v = Min + Math.Round((v - Min) / Step) * Step;
        v = Math.Clamp(v, Min, Max);
        if (Math.Abs(v - _value) < 1e-6) return;
        GestureBegin?.Invoke();
        _value = v;
        InvalidateVisual();
        ValueChanged?.Invoke(_value);
        GestureEnd?.Invoke();
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        double cx = w / 2, cy = h / 2;
        double r = Math.Min(w, h) / 2 - 2.5;
        if (r <= 1) return;
        double span = Max - Min;
        double frac = span > 0 ? (_value - Min) / span : 0;
        frac = Math.Clamp(frac, 0, 1);

        var arcBrush = ArcColor ?? (Accent ? Brass : TrackArc);
        double aw = Math.Max(2.5, r * 0.18);

        // 1. Dark 270° recessed track groove
        DrawArc(ctx, cx, cy, r, 0, 1, new Pen(Groove, aw + 0.8, lineCap: PenLineCap.Round));

        // 2. Illuminated LED value arc
        if (Bipolar)
        {
            double midFrac = span > 0 ? (0.0 - Min) / span : 0.5;
            midFrac = Math.Clamp(midFrac, 0, 1);
            if (frac > midFrac + 0.001) DrawArc(ctx, cx, cy, r, midFrac, frac, new Pen(arcBrush, aw, lineCap: PenLineCap.Round));
            else if (frac < midFrac - 0.001) DrawArc(ctx, cx, cy, r, frac, midFrac, new Pen(arcBrush, aw, lineCap: PenLineCap.Round));
        }
        else
        {
            if (frac > 0.001) DrawArc(ctx, cx, cy, r, 0, frac, new Pen(arcBrush, aw, lineCap: PenLineCap.Round));
        }

        // 3. Tactile 3D Knob Cap
        double capR = Math.Max(2.0, r * 0.68);

        // Cap drop shadow
        ctx.DrawEllipse(CapShadow, null, new Point(cx, cy + 1.5), capR, capR);

        // Outer dark bezel rim
        ctx.DrawEllipse(CapBezel, null, new Point(cx, cy), capR, capR);

        // Cap face with tactile satin gradient
        var capGrad = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.1, 0.0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.9, 1.0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(_drag ? "#464A54" : "#383C44"), 0.0),
                new GradientStop(Color.Parse(_drag ? "#282B32" : "#22242A"), 1.0)
            }
        };
        ctx.DrawEllipse(capGrad, null, new Point(cx, cy), capR - 0.8, capR - 0.8);

        // Subtle top-edge inner highlight rim
        var highlightPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 0.75);
        ctx.DrawEllipse(null, highlightPen, new Point(cx, cy - 0.3), capR - 1.2, capR - 1.2);

        // Inset concentric center dial
        if (capR > 6)
        {
            var innerDialBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            ctx.DrawEllipse(innerDialBrush, null, new Point(cx, cy), capR * 0.45, capR * 0.45);
        }

        // 4. Pointer needle from inner cap toward rim at the value angle
        double a = (StartDeg + frac * SweepDeg) * Math.PI / 180.0;
        double dx = Math.Cos(a), dy = Math.Sin(a);
        var pointerPen = new Pen(_drag ? PointerActiveBrush : PointerBrush, Math.Max(1.5, capR * 0.18), lineCap: PenLineCap.Round);
        ctx.DrawLine(pointerPen,
            new Point(cx + dx * (capR * 0.28), cy + dy * (capR * 0.28)),
            new Point(cx + dx * (capR * 0.88), cy + dy * (capR * 0.88)));
    }

    // Draws the sweep arc from fraction t0..t1 as a short polyline (robust, cheap).
    private static void DrawArc(DrawingContext ctx, double cx, double cy, double r, double t0, double t1, IPen pen)
    {
        const int seg = 40;
        int i0 = (int)Math.Floor(t0 * seg), i1 = (int)Math.Ceiling(t1 * seg);
        Point? prev = null;
        for (int i = i0; i <= i1; i++)
        {
            double t = Math.Clamp((double)i / seg, t0, t1);
            double a = (StartDeg + t * SweepDeg) * Math.PI / 180.0;
            var p = new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
            if (prev is { } pp) ctx.DrawLine(pen, pp, p);
            prev = p;
        }
    }
}

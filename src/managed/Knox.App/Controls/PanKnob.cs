// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Knox.App;

/// <summary>Tactile radial pan knob (−1..1), vertical-drag; raises PanChanged + write gestures.
/// Extracted from MixerView (R2-3); self-contained with studio hardware styling.</summary>
internal sealed class PanKnob : Control
{
    private static readonly IBrush Groove = new SolidColorBrush(Color.Parse("#141518"));
    private static readonly IBrush Ring = new SolidColorBrush(Color.Parse("#464952"));
    private static readonly IBrush CenterTick = new SolidColorBrush(Color.Parse("#6EAAFA"));
    private static readonly IBrush Ind = new SolidColorBrush(Color.Parse("#EDEDF0"));
    private static readonly IBrush IndActive = new SolidColorBrush(Color.Parse("#6EAAFA"));
    private static readonly IBrush CapShadow = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));

    private double _pan;
    private bool _drag;
    private double _startY, _startPan;
    public event Action<double>? PanChanged;
    public event Action? GestureBegin;   // M9-C automation write
    public event Action? GestureEnd;

    public PanKnob(double pan)
    {
        _pan = Math.Clamp(pan, -1, 1);
        Width = 28;
        Height = 28;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            GestureBegin?.Invoke();
            _pan = 0.0;
            InvalidateVisual();
            PanChanged?.Invoke(_pan);
            GestureEnd?.Invoke();
            e.Handled = true;
            return;
        }
        _drag = true;
        _startY = e.GetPosition(this).Y;
        _startPan = _pan;
        e.Pointer.Capture(this);
        GestureBegin?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_drag) return;
        double dp = (_startY - e.GetPosition(this).Y) / 60.0;   // drag up → pan right
        _pan = Math.Clamp(_startPan + dp, -1, 1);
        InvalidateVisual();
        PanChanged?.Invoke(_pan);
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
        bool fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        double step = fine ? 0.01 : 0.05;
        double p = Math.Clamp(_pan + delta * step, -1, 1);
        if (Math.Abs(p - _pan) < 1e-4) return;
        GestureBegin?.Invoke();
        _pan = p;
        InvalidateVisual();
        PanChanged?.Invoke(_pan);
        GestureEnd?.Invoke();
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        double cx = Bounds.Width / 2, cy = Bounds.Height / 2, r = 11.5;

        // Outer groove well
        ctx.DrawEllipse(Groove, null, new Point(cx, cy), r, r);
        ctx.DrawEllipse(null, new Pen(Ring, 1.0), new Point(cx, cy), r, r);

        // Center 12 o'clock center tick
        ctx.DrawLine(new Pen(Math.Abs(_pan) < 0.03 ? CenterTick : Ring, 1.5, lineCap: PenLineCap.Round),
            new Point(cx, cy - r), new Point(cx, cy - r + 2.5));

        // Tactile cap
        double capR = r - 2.5;
        ctx.DrawEllipse(CapShadow, null, new Point(cx, cy + 1), capR, capR);

        var capGrad = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.1, 0.0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.9, 1.0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(_drag ? "#464A54" : "#363A42"), 0.0),
                new GradientStop(Color.Parse(_drag ? "#282B32" : "#202227"), 1.0)
            }
        };
        ctx.DrawEllipse(capGrad, new Pen(new SolidColorBrush(Color.Parse("#16171B")), 0.8), new Point(cx, cy), capR, capR);

        // Indicator needle
        double ang = _pan * 135 * Math.PI / 180.0;   // ±135° sweep
        var p0 = new Point(cx + Math.Sin(ang) * 2.0, cy - Math.Cos(ang) * 2.0);
        var p1 = new Point(cx + Math.Sin(ang) * (capR - 0.8), cy - Math.Cos(ang) * (capR - 0.8));
        ctx.DrawLine(new Pen(_drag ? IndActive : Ind, 1.6, lineCap: PenLineCap.Round), p0, p1);
    }
}

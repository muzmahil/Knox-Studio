// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Knox.App;

/// <summary>Thin vertical fader (0..1.5) with drag + automation-write gestures.
/// Extracted from MixerView (R2-3); self-contained.</summary>
internal sealed class VFader : Control
{
    public const double Max = 2.0;
    private const double CapH = 20, CapW = 24;
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#0C0D10"));
    private static readonly IPen TrackPen = new Pen(new SolidColorBrush(Color.Parse("#2B2E38")), 1);
    private static readonly IBrush TrackSlot = new SolidColorBrush(Color.Parse("#060708"));

    private static readonly IBrush CapMetallic = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#F2F5FA"), 0.0),
            new GradientStop(Color.Parse("#D4D8E2"), 0.15),
            new GradientStop(Color.Parse("#9AA0B0"), 0.48),
            new GradientStop(Color.Parse("#6E7382"), 0.52),
            new GradientStop(Color.Parse("#B4BAC8"), 0.9),
            new GradientStop(Color.Parse("#4A4E5A"), 1.0),
        }
    };
    private static readonly IPen CapBorder = new Pen(new SolidColorBrush(Color.Parse("#353842")), 1);
    private static readonly IBrush CapShadow = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private static readonly IPen GroovePen = new Pen(new SolidColorBrush(Color.Parse("#424652")), 1);

    private readonly IBrush _capLine;
    private double _value;
    private bool _drag;
    public event Action<double>? ValueChanged;
    public event Action? GestureBegin;   // M9-C automation write
    public event Action? GestureEnd;

    public VFader(double value, Color capLine)
    {
        _value = Math.Clamp(value, 0, Max);
        _capLine = new SolidColorBrush(capLine);
        Width = 30;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Sets the value without raising ValueChanged (external sync, e.g. dB field).</summary>
    public void SetValueExternal(double v) { _value = Math.Clamp(v, 0, Max); InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2 || (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            _value = 1.0; // 0.0 dB / unity gain
            InvalidateVisual();
            ValueChanged?.Invoke(_value);
            e.Handled = true;
            return;
        }
        _drag = true;
        e.Pointer.Capture(this);
        GestureBegin?.Invoke();
        SetY(e.GetPosition(this).Y);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e) { if (_drag) SetY(e.GetPosition(this).Y); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { if (_drag) GestureEnd?.Invoke(); _drag = false; e.Pointer.Capture(null); }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;
        bool fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        double step = fine ? 0.005 : 0.02;
        double currentPos = AudioMath.GainToFaderPos(_value, Max);
        double newPos = Math.Clamp(currentPos + delta * step, 0, 1);
        double v = AudioMath.FaderPosToGain(newPos, Max);
        if (Math.Abs(v - _value) < 1e-4) return;
        GestureBegin?.Invoke();
        _value = v;
        InvalidateVisual();
        ValueChanged?.Invoke(_value);
        GestureEnd?.Invoke();
        e.Handled = true;
    }

    private void SetY(double y)
    {
        double h = Bounds.Height; if (h <= CapH) return;
        double frac = Math.Clamp(1 - (y - CapH / 2) / (h - CapH), 0, 1);
        double v = AudioMath.FaderPosToGain(frac, Max);
        if (Math.Abs(v - _value) < 1e-4) return;
        _value = v; InvalidateVisual(); ValueChanged?.Invoke(v);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, cx = w / 2;

        // 1. Carved fader slot (outer bezel + deep inner well)
        ctx.DrawRectangle(Track, TrackPen, new Rect(cx - 3, 2, 6, h - 4), 2, 2);
        ctx.DrawRectangle(TrackSlot, null, new Rect(cx - 1.5, 4, 3, h - 8), 1, 1);

        // 2. 0dB Unity center indicator notch (at 75% travel)
        double unityFrac = AudioMath.GainToFaderPos(1.0, Max);
        double unityY = (h - CapH) * (1 - unityFrac) + CapH / 2;
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#8E94A5")), 1.5), new Point(cx - 7, unityY), new Point(cx - 3, unityY));
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#8E94A5")), 1.5), new Point(cx + 3, unityY), new Point(cx + 7, unityY));

        // 3. Fader position calculation
        double frac = AudioMath.GainToFaderPos(_value, Max);
        double capY = (h - CapH) * (1 - frac);

        // 4. Cap drop shadow
        ctx.DrawRectangle(CapShadow, null, new Rect(cx - CapW / 2, capY + 2, CapW, CapH), 3, 3);

        // 5. Solid brushed aluminum cap
        var capRect = new Rect(cx - CapW / 2, capY, CapW, CapH);
        ctx.DrawRectangle(CapMetallic, CapBorder, capRect, 3, 3);

        // 6. Tactile milled grip ridges
        ctx.DrawLine(GroovePen, new Point(cx - 8, capY + 4), new Point(cx + 8, capY + 4));
        ctx.DrawLine(GroovePen, new Point(cx - 8, capY + CapH - 4), new Point(cx + 8, capY + CapH - 4));

        // 7. Illuminated center indicator line with high-contrast core
        double midY = capY + CapH / 2;
        ctx.DrawLine(new Pen(_capLine, 2), new Point(cx - 9, midY), new Point(cx + 9, midY));
        ctx.DrawLine(new Pen(Brushes.White, 1), new Point(cx - 7, midY), new Point(cx + 7, midY));
    }
}

// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// A high-precision horizontal volume fader matching modern DAW ergonomics
// (FL Studio / Ableton / Logic Pro): sunken track, level fill, 0 dB unity mark,
// tactile fader thumb cap with grip notches, smooth scrubbing, direct seek,
// and double-click / Alt-click reset to 0.0 dB.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Knox.App;

public sealed class MiniFader : Control
{
    private static readonly IBrush TrackBg = KnoxPalette.BgSunken;
    private static readonly IPen TrackBorder = new Pen(KnoxPalette.BorderDefault, 1);
    private static readonly IBrush TrackFill = KnoxPalette.BorderStrong;
    private static readonly IBrush TrackFillAccent = KnoxPalette.Accent;
    private static readonly IPen UnityTick = new Pen(new SolidColorBrush(Color.Parse("#666670")), 1);

    private static readonly IBrush CapBrush = new SolidColorBrush(Color.Parse("#C8C8D0"));
    private static readonly IBrush CapHoverBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush CapAccentBrush = KnoxPalette.AccentBright;
    private static readonly IPen CapBorder = new Pen(new SolidColorBrush(Color.Parse("#1A1A1D")), 1);
    private static readonly IPen CapNotch = new Pen(new SolidColorBrush(Color.Parse("#25252A")), 1);

    /// <summary>Brass fill + cap instead of the neutral grey (used by the synth editor).</summary>
    public bool Accent { get; init; }
    /// <summary>Double-click target value, or negative to disable reset.</summary>
    public double Default { get; init; } = 1.0;

    private readonly double _max;
    private double _value;
    private bool _drag;
    private bool _hover;

    public event Action<double>? ValueChanged;
    public event Action? GestureBegin;   // pointer down (M9-C automation write)
    public event Action? GestureEnd;     // pointer up

    /// <summary>True while the user is dragging — callers skip external value writes then.</summary>
    public bool Dragging => _drag;

    public double Value
    {
        get => _value;
        set
        {
            double clamped = Math.Clamp(value, 0, _max);
            if (Math.Abs(clamped - _value) > 1e-4)
            {
                _value = clamped;
                UpdateTip();
                InvalidateVisual();
            }
        }
    }

    public MiniFader(double value = 1.0, double max = 2.0)
    {
        _max = max;
        _value = Math.Clamp(value, 0, max);
        Height = 16;
        MinWidth = 48;
        Cursor = new Cursor(StandardCursorType.Hand);
        UpdateTip();
    }

    private void UpdateTip()
    {
        string dbStr = _value <= 0.0001 ? "-∞ dB" : $"{AudioMath.LinToDb(_value):+0.0;-0.0;0.0} dB";
        int pct = (int)Math.Round((_value / 1.0) * 100.0);
        ToolTip.SetTip(this, $"Volume: {dbStr} ({pct}%)\nDouble-click to reset (0.0 dB)");
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Double-click or Alt-click resets to default or unity gain (0 dB / 1.0)
        if (e.ClickCount == 2 || (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            double d = Math.Clamp(Default >= 0 ? Default : 1.0, 0, _max);
            if (Math.Abs(d - _value) > 1e-6)
            {
                _value = d;
                UpdateTip();
                InvalidateVisual();
                ValueChanged?.Invoke(_value);
            }
            e.Handled = true;
            return;
        }

        _drag = true;
        e.Pointer.Capture(this);
        GestureBegin?.Invoke();
        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag) SetFromX(e.GetPosition(this).X);
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
        double step = fine ? 0.01 : 0.03;
        double currentPos = AudioMath.GainToFaderPos(_value, _max);
        double newPos = Math.Clamp(currentPos + delta * step, 0, 1);
        double v = AudioMath.FaderPosToGain(newPos, _max);
        if (Math.Abs(v - _value) < 1e-4) return;
        GestureBegin?.Invoke();
        _value = v;
        UpdateTip();
        InvalidateVisual();
        ValueChanged?.Invoke(_value);
        GestureEnd?.Invoke();
        e.Handled = true;
    }

    private void SetFromX(double x)
    {
        double w = Bounds.Width;
        if (w <= 0) return;
        double frac = Math.Clamp(x / w, 0, 1);
        double v = AudioMath.FaderPosToGain(frac, _max);
        if (Math.Abs(v - _value) < 1e-4) return;
        _value = v;
        UpdateTip();
        InvalidateVisual();
        ValueChanged?.Invoke(_value);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var rrect = new RoundedRect(new Rect(0, 0, w, h), 4.0);
        ctx.DrawRectangle(TrackBg, TrackBorder, rrect);

        double frac = AudioMath.GainToFaderPos(_value, _max);
        double fx = frac * w;

        // Level fill
        using (ctx.PushClip(rrect))
        {
            if (fx > 0)
            {
                ctx.FillRectangle(Accent ? TrackFillAccent : TrackFill, new Rect(0, 0, fx, h));
            }

            // 0 dB / Unity tick mark (at 1.0 / max, 75% travel)
            double unityX = AudioMath.GainToFaderPos(1.0, _max) * w;
            if (unityX > 2 && unityX < w - 2)
            {
                ctx.DrawLine(UnityTick, new Point(unityX, 1), new Point(unityX, h - 1));
            }
        }

        // Tactile Fader Thumb Cap
        double capW = 12, capH = h;
        double capX = Math.Clamp(fx - capW / 2.0, 0, w - capW);
        var capRect = new Rect(capX, 0, capW, capH);
        var capFill = Accent ? CapAccentBrush : (_drag || _hover ? CapHoverBrush : CapBrush);

        ctx.DrawRectangle(capFill, CapBorder, new RoundedRect(capRect, 3.0));

        // Dual tactile grip notches on fader cap
        double notchX = capX + capW / 2.0;
        ctx.DrawLine(CapNotch, new Point(notchX - 1.5, 3.5), new Point(notchX - 1.5, capH - 3.5));
        ctx.DrawLine(CapNotch, new Point(notchX + 1.5, 3.5), new Point(notchX + 1.5, capH - 3.5));
    }
}

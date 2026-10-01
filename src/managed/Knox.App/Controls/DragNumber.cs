// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// A numeric field that edits by vertical drag or double-click-to-type
// (HANDOFF §4: "all numerics editable by drag (vertical) and double-click-type;
// mono font keeps width stable"). Shows a mono read-out; drag up/down nudges the
// value, double-click swaps in a flat text box that commits on Enter / blur.

using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Knox.App;

public sealed class DragNumber : UserControl
{
    private readonly TextBlock _display = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _editor;
    private readonly double _min, _max, _step;
    private readonly string _format;
    private readonly string _fgResource;
    private double _value;
    private bool _drag, _editing;
    private double _startY, _startValue;

    public event Action<double>? ValueChanged;

    public DragNumber(double value, double min, double max, double step, string format = "0", double fontSize = 12, string fgResource = "Brush.TextPrimary")
    {
        _min = min; _max = max; _step = step; _format = format;
        _fgResource = fgResource;
        _value = Math.Clamp(value, min, max);

        _display.FontSize = fontSize;
        _display.FontWeight = FontWeight.SemiBold;
        _display.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
        _display.BindResource(TextBlock.ForegroundProperty, _fgResource);

        _editor = new TextBox
        {
            FontSize = fontSize, IsVisible = false, Padding = new Avalonia.Thickness(0), MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Avalonia.Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _editor.BindResource(TextBox.FontFamilyProperty, "Font.Mono");
        _editor.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitEditor(); else if (e.Key == Key.Escape) CancelEditor(); };
        _editor.LostFocus += (_, _) => CommitEditor();

        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        Content = new Panel { Children = { _display, _editor } };
        Refresh();
    }

    public bool IsDragging => _drag;

    public double Value
    {
        get => _value;
        set
        {
            double clamped = Math.Clamp(value, _min, _max);
            if (Math.Abs(clamped - _value) > 1e-6)
            {
                _value = clamped;
                Refresh();
            }
        }
    }

    public bool UseCommaSeparator { get; set; }

    private void Refresh()
    {
        string text = _value.ToString(_format, CultureInfo.InvariantCulture);
        if (UseCommaSeparator) text = text.Replace('.', ',');
        if (_display.Text != text)
            _display.Text = text;
    }

    private double _accumulatedDelta;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_editing) return;
        if (e.ClickCount == 2) { BeginEditor(); e.Handled = true; return; }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = true;
        _startY = e.GetPosition(this).Y;
        _accumulatedDelta = 0;
        _startValue = _value;
        _display.BindResource(TextBlock.ForegroundProperty, "Brush.AccentBright");
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_drag) return;
        double currentY = e.GetPosition(this).Y;
        double dy = _startY - currentY;   // up = increase
        _startY = currentY;
        if (Math.Abs(dy) < 1e-4) return;

        double step = _step;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) step *= 0.1;
        else if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) step *= 5.0;

        _accumulatedDelta += dy;
        const double pxPerStep = 4.0;
        if (Math.Abs(_accumulatedDelta) >= pxPerStep)
        {
            double steps = Math.Truncate(_accumulatedDelta / pxPerStep);
            _accumulatedDelta -= steps * pxPerStep;
            double v = Math.Clamp(_value + steps * step, _min, _max);
            if (Math.Abs(v - _value) > 1e-6)
            {
                _value = v;
                Refresh();
                ValueChanged?.Invoke(_value);
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag)
        {
            _drag = false;
            _accumulatedDelta = 0;
            _display.BindResource(TextBlock.ForegroundProperty, _fgResource);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editing) return;
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;
        double step = _step;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) step *= 0.1;
        else if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) step *= 5.0;

        double v = Math.Clamp(_value + (delta > 0 ? step : -step), _min, _max);
        if (Math.Abs(v - _value) < 1e-6) return;
        _value = v;
        Refresh();
        ValueChanged?.Invoke(_value);
        e.Handled = true;
    }

    private void BeginEditor()
    {
        _editing = true;
        string t = _value.ToString(_format, CultureInfo.InvariantCulture);
        if (UseCommaSeparator) t = t.Replace('.', ',');
        _editor.Text = t;
        _editor.IsVisible = true;
        _display.IsVisible = false;
        _editor.Focus();
        _editor.SelectAll();
    }

    private void CommitEditor()
    {
        if (!_editing) return;
        _editing = false;
        _editor.IsVisible = false;
        _display.IsVisible = true;
        string raw = (_editor.Text ?? "").Replace(',', '.');
        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
        {
            _value = Math.Clamp(v, _min, _max);
            Refresh();
            ValueChanged?.Invoke(_value);
        }
    }

    private void CancelEditor()
    {
        _editing = false;
        _editor.IsVisible = false;
        _display.IsVisible = true;
    }
}

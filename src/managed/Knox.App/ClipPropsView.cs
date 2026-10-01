// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The 200px clip props rail beside the piano roll (mockup 1e): clip name,
// Start/Length in bars.beats.16ths (mono), a Loop chip, a grid picker with a
// one-tap Quantize + strength, a transpose stepper and a live selection line.
// Edits drive the PianoRollView (grid / quantize / transpose); read-outs follow
// its Changed event.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Knox.Application;

namespace Knox.App;

public sealed class ClipPropsView : UserControl
{
    private static readonly IBrush Panel = KnoxPalette.SurfaceCard;
    private static readonly IBrush Sunken = KnoxPalette.BgSunken;
    private static readonly IBrush Raised = KnoxPalette.SurfaceRaised;
    private static readonly IBrush BorderDef = KnoxPalette.BorderDefault;
    private static readonly IBrush BorderStrong = KnoxPalette.BorderStrong;
    private static readonly IBrush Brass = KnoxPalette.Accent;
    private static readonly IBrush AccentBright = KnoxPalette.AccentBright;
    private static readonly IBrush AccentSubtle = KnoxPalette.AccentSubtle;
    private static readonly IBrush TextPrimary = KnoxPalette.TextPrimary;
    private static readonly IBrush TextSecondary = KnoxPalette.TextSecondary;
    private static readonly IBrush TextTertiary = KnoxPalette.TextTertiary;

    // Grid steps in beats (4/4, one beat = 1.0 beat = quarter note).
    // Straight divisions + Bar Triplets (1/3 bar = 4/3 beats) + Beat Triplets (1/8T, 1/16T, etc.)
    public static readonly double[] GridValues =
    {
        4.0,           // 1 Bar (1/1)
        2.0,           // 1/2 Bar (1/2)
        4.0 / 3.0,     // 1/3 Bar (Bar Triplet / 3 per Bar)
        1.0,           // 1/4 Bar (1 Beat)
        2.0 / 3.0,     // 1/6 Bar (1/4T / 6 per Bar)
        0.5,           // 1/2 Beat (1/8)
        1.0 / 3.0,     // 1/3 Beat (1/8T)
        0.25,          // 1/16 Span (1/4 Beat / 1/16 / Step)
        1.0 / 6.0,     // 1/6 Beat (1/16T / 1/16 Span Triplet)
        0.125,         // 1/8 Beat (1/32)
        1.0 / 12.0,    // 1/12 Beat (1/32T)
        0.0625,        // 1/16 Beat (1/64)
        1.0 / 24.0,    // 1/24 Beat (1/64T)
        0.03125,       // 1/32 Beat (1/128)
        0.0            // Adaptive (Auto)
    };
    public static readonly string[] GridLabels =
    {
        "1 Bar (1/1)",
        "1/2 Bar (1/2)",
        "1/3 Bar (Triplet)",
        "1/4 Bar (1 Beat)",
        "1/6 Bar (1/4T)",
        "1/2 Beat (1/8)",
        "1/3 Beat (1/8T)",
        "1/16 Span (1/16)",
        "1/6 Beat (1/16T)",
        "1/8 Beat (1/32)",
        "1/12 Beat (1/32T)",
        "1/16 Beat (1/64)",
        "1/24 Beat (1/64T)",
        "1/32 Beat (1/128)",
        "Adaptive (Auto)"
    };

    private readonly PianoRollView _roll;
    private readonly double _startBeat;
    private readonly TextBlock _lengthText;
    private readonly TextBlock _loopText;
    private readonly TextBlock _selInfo;
    private readonly TextBlock _gridText;
    private int _gridIndex = 7;   // 1/16 Span (0.25)
    private double _transpose;
    private double _velocityScale = 100.0;
    private double _strength = 80.0;
    private double _humanize;

    public ClipPropsView(PianoRollView roll, string clipName, double startBeat)
    {
        _roll = roll;
        _startBeat = startBeat;
        Width = 200;
        Background = Panel;
        BorderBrush = BorderDef;
        BorderThickness = new Thickness(0, 0, 1, 0);

        _lengthText = Mono(Duration(roll.LengthBeats));
        _loopText = Mono($"{roll.LengthBeats:0.#}b");
        _loopText.Foreground = AccentBright;
        _selInfo = new TextBlock { Text = roll.SelectionInfo, FontSize = 9, Foreground = TextTertiary, TextWrapping = TextWrapping.Wrap };
        _gridText = new TextBlock { Text = GridLabels[_gridIndex] + " ▾", FontSize = 10, Foreground = TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(10) };
        body.Children.Add(Section(L10n.Tr("Clip.Title", "CLIP"), ReadoutBox(clipName, TextPrimary, 24, 11, false)));

        var startLen = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        startLen.Children.Add(Labeled(L10n.Tr("Clip.Start", "START"), ReadoutBox(Position(startBeat), TextPrimary, 22, 10, true)));
        var lenBox = FieldBox(22); lenBox.Child = _lengthText;
        var lenWrap = Labeled(L10n.Tr("Clip.Length", "LENGTH"), lenBox);
        Grid.SetColumn(lenWrap, 1);
        startLen.Children.Add(lenWrap);
        body.Children.Add(startLen);

        // Loop chip (clips loop by default in this model).
        body.Children.Add(new Border
        {
            Height = 24, Background = AccentSubtle, BorderBrush = Brass, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { new TextBlock { Text = L10n.Tr("Clip.Loop", "Loop"), FontSize = 10, Foreground = AccentBright, VerticalAlignment = VerticalAlignment.Center }, _loopText },
            },
        });

        // 1. TRANSPOSE & VELOCITY KNOBS (with default reset)
        var transKnob = new Knob(0, -24, 24, defaultValue: 0, step: 1) { Accent = true, Bipolar = true, Width = 34, Height = 34 };
        var transVal = Mono("0 st");
        transKnob.ValueChanged += v =>
        {
            int diff = (int)v - (int)_transpose;
            if (diff != 0)
            {
                _transpose = (int)v;
                _roll.TransposeBy(diff);
            }
            transVal.Text = $"{(int)v:+0;-0;0} st";
        };

        var velKnob = new Knob(100, 10, 200, defaultValue: 100, step: 1) { Accent = true, Width = 34, Height = 34 };
        var velVal = Mono("100%");
        velKnob.ValueChanged += v =>
        {
            double factor = v / _velocityScale;
            _velocityScale = v;
            _roll.ScaleVelocity(factor);
            velVal.Text = $"{(int)v}%";
        };

        var pitchVelGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        pitchVelGrid.Children.Add(KnobCard(L10n.Tr("Clip.Transpose", "TRANSPOSE"), transKnob, transVal));
        var velCell = KnobCard(L10n.Tr("Clip.Velocity", "VELOCITY"), velKnob, velVal);
        Grid.SetColumn(velCell, 1);
        pitchVelGrid.Children.Add(velCell);
        body.Children.Add(Section(L10n.Tr("Clip.PitchAndVel", "PITCH & VELOCITY"), pitchVelGrid));

        // 2. TIMING & GROOVE KNOBS
        var quantKnob = new Knob(80, 0, 100, defaultValue: 80, step: 1) { Accent = true, Width = 34, Height = 34 };
        var quantVal = Mono("80%");
        quantKnob.ValueChanged += v => { _strength = v; quantVal.Text = $"{(int)v}%"; };

        var humKnob = new Knob(0, 0, 100, defaultValue: 0, step: 1) { Accent = true, Width = 34, Height = 34 };
        var humVal = Mono("0%");
        humKnob.ValueChanged += v =>
        {
            double diff = v - _humanize;
            _humanize = v;
            if (diff > 0) _roll.Humanize(diff / 100.0);
            humVal.Text = $"{(int)v}%";
        };

        var timeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        timeGrid.Children.Add(KnobCard(L10n.Tr("Clip.Quantize", "QUANTIZE"), quantKnob, quantVal));
        var humCell = KnobCard(L10n.Tr("Clip.Humanize", "HUMANIZE"), humKnob, humVal);
        Grid.SetColumn(humCell, 1);
        timeGrid.Children.Add(humCell);
        body.Children.Add(Section(L10n.Tr("Clip.TimingAndGroove", "TIMING & GROOVE"), timeGrid));

        // 3. GRID & QUICK ACTIONS
        var gridChip = Chip(_gridText);
        gridChip.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            var f = new MenuFlyout();
            for (int i = 0; i < GridValues.Length; i++)
            {
                int idx = i;
                var mi = new MenuItem { Header = GridLabels[i] };
                if (i == _gridIndex) mi.Icon = new TextBlock { Text = "•", Foreground = AccentBright };
                mi.Click += (_, _) =>
                {
                    _gridIndex = idx;
                    _gridText.Text = GridLabels[idx] + " ▾";
                    _roll.Grid = GridValues[idx];
                };
                f.Items.Add(mi);
            }
            f.ShowAt(gridChip);
        };
        var quantizeBtn = ActionChip(L10n.Tr("Clip.QuantizeNow", "Quantize Now"), () => _roll.Quantize(_strength / 100.0));
        var gridRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        gridRow.Children.Add(gridChip);
        Grid.SetColumn(quantizeBtn, 1); gridRow.Children.Add(quantizeBtn);

        var legatoBtn = ActionChip(L10n.Tr("Clip.Legato", "Legato"), () => _roll.Legato());
        var doubleBtn = ActionChip("2x Speed", () => _roll.DoubleTempo());
        var halfBtn = ActionChip("½x Speed", () => _roll.HalfTempo());
        var reverseBtn = ActionChip(L10n.Tr("Clip.Reverse", "Reverse"), () => _roll.ReverseNotes());
        var invertBtn = ActionChip(L10n.Tr("Clip.Invert", "Invert"), () => _roll.InvertNotes());

        var toolsRow1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        toolsRow1.Children.Add(legatoBtn);
        Grid.SetColumn(reverseBtn, 1); toolsRow1.Children.Add(reverseBtn);

        var toolsRow2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        toolsRow2.Children.Add(doubleBtn);
        Grid.SetColumn(halfBtn, 1); toolsRow2.Children.Add(halfBtn);

        body.Children.Add(Section(L10n.Tr("Clip.GridTools", "GRID & MIDI TOOLS"), new StackPanel { Spacing = 4, Children = { gridRow, toolsRow1, toolsRow2, invertBtn } }));

        _selInfo.Margin = new Thickness(0, 4, 0, 0);
        var hint = new TextBlock { Text = "Double-click knob = Reset default · Drag note edge = Snap resize", FontSize = 9, Foreground = TextTertiary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        body.Children.Add(_selInfo);
        body.Children.Add(hint);

        Content = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _roll.Changed += OnRollChanged;
    }

    private void OnRollChanged()
    {
        _selInfo.Text = _roll.SelectionInfo;
        _lengthText.Text = Duration(_roll.LengthBeats);
        _loopText.Text = $"{_roll.LengthBeats:0.#}b";
    }

    // --- helpers ----------------------------------------------------------

    private static Control KnobCard(string label, Knob knob, TextBlock valueText)
    {
        var panel = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock { Text = label, FontSize = 8, FontWeight = FontWeight.Bold, Foreground = TextTertiary, HorizontalAlignment = HorizontalAlignment.Center },
                knob,
                valueText
            }
        };
        return new Border
        {
            Background = Sunken,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(4, 6),
            Child = panel
        };
    }

    private static TextBlock Mono(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 9, Foreground = KnoxPalette.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.BindResource(FontFamilyProperty, "Font.Mono");
        return t;
    }

    private static Control Section(string title, Control content) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = title, FontSize = 9, FontWeight = FontWeight.Bold, Foreground = TextTertiary },
            content,
        },
    };

    private static Control Labeled(string title, Control content) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = title, FontSize = 9, Foreground = TextTertiary }, content },
    };

    private static Border FieldBox(double h) => new()
    {
        Height = h, Background = Sunken, BorderBrush = BorderDef, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
    };

    private static Border ReadoutBox(string text, IBrush fg, double h, double fs, bool mono)
    {
        var t = new TextBlock { Text = text, FontSize = fs, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(mono ? 7 : 8, 0, 0, 0) };
        if (mono) t.BindResource(FontFamilyProperty, "Font.Mono");
        var box = FieldBox(h);
        box.Child = t;
        return box;
    }

    private static Border Chip(Control child) => new()
    {
        Height = 22, Background = Raised, BorderBrush = BorderStrong, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
        Cursor = new Cursor(StandardCursorType.Hand), Child = child,
    };

    private static Border ActionChip(string label, Action onClick)
    {
        var tb = new TextBlock { Text = label, FontSize = 10, Foreground = TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var chip = Chip(tb);
        chip.PointerPressed += (_, e) => { e.Handled = true; onClick(); };
        return chip;
    }

    // bars.beats.16ths (4/4), 1-based position.
    private static string Position(double beat)
    {
        int bar = (int)(beat / 4) + 1, be = (int)(beat % 4) + 1, six = (int)Math.Round(beat % 1 * 4) + 1;
        return string.Format(CultureInfo.InvariantCulture, "{0}. {1}. {2}", bar, be, six);
    }

    // bars.beats.16ths as a duration (0-based).
    private static string Duration(double beats)
    {
        int bars = (int)(beats / 4), be = (int)(beats % 4), six = (int)Math.Round(beats % 1 * 4);
        return string.Format(CultureInfo.InvariantCulture, "{0}. {1}. {2}", bars, be, six);
    }
}

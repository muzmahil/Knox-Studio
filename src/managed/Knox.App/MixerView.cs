// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Mixer (mockup 1f): a dedicated strip mixer as a third Detail mode. One strip
// per track (+ returns + master): name with a 2px colour top, I/O rows, A/B
// sends, a pan knob, a 190px vertical fader with a stereo meter, a dB read-out
// and M/S/Arm. All strips ride the existing engine ops (vol/pan/mute/solo/send/
// meter); meters are pushed each UI tick by MainWindow. Crossfader is M7+.

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Knox.Application;

namespace Knox.App;

public sealed class MixerView : UserControl
{
    private static readonly IBrush Card = KnoxPalette.SurfaceCard;
    private static readonly IBrush MasterBg = KnoxPalette.SurfaceCard;
    private static readonly IBrush Sunken = KnoxPalette.BgSunken;
    private static readonly IBrush BorderDef = KnoxPalette.BorderDefault;
    private static readonly IBrush BorderInner = new SolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly IBrush BorderStrong = KnoxPalette.BorderStrong;
    private static readonly IBrush Brass = KnoxPalette.Accent;
    private static readonly IBrush Danger = KnoxPalette.Danger;
    private static readonly IBrush TextPrimary = KnoxPalette.TextPrimary;
    private static readonly IBrush TextSecondary = KnoxPalette.TextSecondary;
    private static readonly IBrush TextTertiary = KnoxPalette.TextTertiary;
    private static readonly IBrush TextDisabled = KnoxPalette.TextDisabled;
    private static readonly IBrush OnAccent = KnoxPalette.TextOnAccent;

    private readonly IAudioEngine _engine;
    private readonly IPluginCatalog? _catalog;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(10) };
    private readonly List<(int trackId, MeterBar meter)> _meters = new();
    // Sends / I/O sections are collapsible across every strip (mockup 1f header
    // toggles); we keep the section borders so a toggle can hide them all at once.
    private readonly List<Control> _sendSections = new();
    private readonly List<Control> _ioSections = new();
    private readonly List<Control> _fxSections = new();
    private bool _sendsVisible = true, _ioVisible = true, _fxVisible = true;
    private float _masterVolume = 1.0f;

    public event Action<int>? TrackSelected;
    public event Action<int, int>? DeviceOpenRequested;
    public event Action? PopoutRequested;

    public MixerView(IAudioEngine engine, IPluginCatalog? catalog = null)
    {
        _engine = engine;
        _catalog = catalog;
        var scroller = new ScrollViewer
        {
            Content = _row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var dock = new DockPanel();
        var footer = Footer();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(scroller);
        Content = dock;
    }

    public void Refresh()
    {
        _row.Children.Clear();
        _meters.Clear();
        _sendSections.Clear();
        _ioSections.Clear();
        _fxSections.Clear();

        // 1. Master Channel Strip on the far LEFT
        _row.Children.Add(MasterStrip());

        // 2. Regular tracks and return buses
        int returns = _engine.ReturnTrackCount;
        int n = _engine.TrackCount;
        for (int i = 0; i < n; i++)
        {
            if (!_engine.TryGetTrackInfo(i, out var ti)) continue;
            Color c = ArrangementView.TrackColorForIndex(ArrangementView.EffectiveColorIndex(_engine, ti.Id));
            _row.Children.Add(Strip(ti, c, returns));
        }
    }

    /// <summary>Pushes fresh meter readings to every strip (called at the UI clock).</summary>
    public void UpdateMeters()
    {
        foreach (var (id, meter) in _meters)
        {
            if (id < 0) meter.Push(_engine.MasterMeter());
            else if (_engine.TryGetTrackMeter(id, out var m)) meter.Push(m);
        }
    }

    // ---- strips -----------------------------------------------------------

    private Control Strip(KnoxTrackInfo ti, Color color, int returns)
    {
        int id = ti.Id;
        bool isReturn = ti.IsReturn;
        var col = new StackPanel { Spacing = 2 };

        // 1. Setting Button (Top)
        col.Children.Add(SettingBox());

        // 2. EQ Thumbnail curve box
        col.Children.Add(EqThumbnailBox());

        // 3. Input / Instrument Slot (Logic Blue chip)
        string io1 = isReturn ? "Return in" : ti.IsInstrument ? (_engine.GetTrackName(id) is { Length: > 0 } nm ? nm : "Instrument") : "Input 1-2";
        var ioRow = ti.IsInstrument ? MidiIoRow(id) : IoRow(io1, "St Out");
        ioRow.IsVisible = _ioVisible;
        _ioSections.Add(ioRow);
        col.Children.Add(ioRow);

        // 4. Audio FX Slots Rack (Vertical list of plugin chips)
        var fxRow = AudioFxRow(id);
        fxRow.IsVisible = _fxVisible;
        _fxSections.Add(fxRow);
        col.Children.Add(fxRow);

        // 5. Sends
        if (!isReturn)
        {
            var sends = SendsRow(id, returns);
            sends.IsVisible = _sendsVisible;
            _sendSections.Add(sends);
            col.Children.Add(sends);
        }

        // 6. Output & Automation
        col.Children.Add(OutputAndAutomationRow());

        // 7. Pan Knob
        col.Children.Add(PanRow(id, ti.Pan));

        // 8. Fader + dB Readout + Stereo Peak Meter
        col.Children.Add(FaderRow(id, ti.Volume, color, out var db, out var meter));
        _meters.Add((id, meter));
        col.Children.Add(db);

        // 9. M / S / R Buttons
        col.Children.Add(ButtonsRow(id, ti, isReturn));

        // 10. Bottom Track Name Tag with Color Fill
        string trackDisplayName = _engine.GetTrackName(id) is { Length: > 0 } tname ? tname : NameFor(ti);
        col.Children.Add(BottomTrackTag(trackDisplayName, color));

        return new Border
        {
            Width = 112,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#464646"), 0.0),
                    new GradientStop(Color.Parse("#383838"), 1.0)
                }
            },
            BorderBrush = new SolidColorBrush(Color.Parse("#5A5A5A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = col,
        };
    }

    private Control MasterStrip()
    {
        var col = new StackPanel { Spacing = 2 };
        col.Children.Add(SettingBox("MASTER"));
        col.Children.Add(EqThumbnailBox());

        var fxRow = AudioFxRow(-1);
        fxRow.IsVisible = _fxVisible;
        _fxSections.Add(fxRow);
        col.Children.Add(fxRow);

        var ioRow = IoRow("Stereo Out", "Speakers");
        ioRow.IsVisible = _ioVisible;
        _ioSections.Add(ioRow);
        col.Children.Add(ioRow);

        col.Children.Add(OutputAndAutomationRow("Read"));

        Control db;
        var faderRow = FaderRow(-1, _masterVolume, Color.Parse("#6EAAFA"), out db, out var meter);
        _meters.Add((-1, meter));
        col.Children.Add(faderRow);
        col.Children.Add(db);

        col.Children.Add(new Border { Height = 26, BorderBrush = BorderInner, BorderThickness = new Thickness(0, 1, 0, 0) });
        col.Children.Add(BottomTrackTag(L10n.Tr("Mixer.StereoOut", "Stereo Out"), Color.Parse("#7B5EC7")));

        return new Border
        {
            Width = 112,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#4E4E4E"), 0.0),
                    new GradientStop(Color.Parse("#404040"), 1.0)
                }
            },
            BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = col,
        };
    }

    private Control SettingBox(string title = "Setting")
    {
        var tb = new TextBlock
        {
            Text = title, FontSize = 9, FontWeight = FontWeight.Medium,
            Foreground = TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Height = 18, Background = new SolidColorBrush(Color.Parse("#303030")), BorderBrush = new SolidColorBrush(Color.Parse("#484848")), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tb,
        };
    }

    private Control EqThumbnailBox()
    {
        var eqBorder = new Border
        {
            Height = 22, Background = new SolidColorBrush(Color.Parse("#282828")), BorderBrush = new SolidColorBrush(Color.Parse("#464646")), BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(2, 0), CornerRadius = new CornerRadius(2),
        };
        var canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        // Stylized EQ curve line
        var path = new Path
        {
            Data = Geometry.Parse("M 4,14 C 20,18 40,6 60,11 C 75,16 90,8 96,10"),
            Stroke = new SolidColorBrush(Color.Parse("#4EA0E8")),
            StrokeThickness = 1.5,
        };
        canvas.Children.Add(path);
        eqBorder.Child = canvas;
        return eqBorder;
    }

    private Control OutputAndAutomationRow(string autoText = "Read")
    {
        var stOutBtn = new Border
        {
            Height = 16, Background = new SolidColorBrush(Color.Parse("#282828")),
            BorderBrush = new SolidColorBrush(Color.Parse("#404040")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Child = new TextBlock { Text = "St Out", FontSize = 8, Foreground = TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var readBtn = new Border
        {
            Height = 16, Background = new SolidColorBrush(Color.Parse("#1A2D20")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2C6E3A")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Child = new TextBlock { Text = autoText, FontSize = 8, FontWeight = FontWeight.Bold, Foreground = KnoxPalette.Success, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 3, Margin = new Thickness(4, 2) };
        grid.Children.Add(stOutBtn);
        Grid.SetColumn(readBtn, 1);
        grid.Children.Add(readBtn);
        return grid;
    }

    private Control BottomTrackTag(string name, Color color)
    {
        var label = new TextBlock
        {
            Text = name, FontSize = 9, FontWeight = FontWeight.Bold,
            Foreground = KnoxPalette.TextOnAccent, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0),
        };
        return new Border
        {
            Height = 22, Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(0, 0, 3, 3), Child = label,
        };
    }

    // ---- footer: section toggles + crossfader placeholder ----------------

    private Control Footer()
    {
        var fxToggle = SectionToggle(L10n.Tr("Mixer.AudioFx", "Audio FX"), _fxVisible, on => { _fxVisible = on; foreach (var s in _fxSections) s.IsVisible = on; });
        var sendsToggle = SectionToggle(L10n.Tr("Mixer.Sends", "Sends"), _sendsVisible, on => { _sendsVisible = on; foreach (var s in _sendSections) s.IsVisible = on; });
        var ioToggle = SectionToggle(L10n.Tr("Mixer.Io", "I/O"), _ioVisible, on => { _ioVisible = on; foreach (var s in _ioSections) s.IsVisible = on; });
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = L10n.Tr("Mixer.Show", "Show"), FontSize = 9, Foreground = TextTertiary, VerticalAlignment = VerticalAlignment.Center }, fxToggle, sendsToggle, ioToggle },
        };

        var popout = new Border
        {
            Height = 22,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse("#2E2E2E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#484848")),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "⤢", FontSize = 12, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = L10n.Tr("Mixer.PopOut", "Detach Window"), FontSize = 9.5, FontWeight = FontWeight.SemiBold, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        popout.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            PopoutRequested?.Invoke();
        };

        var rightStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { popout, Crossfader() }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(rightStack, 2);
        grid.Children.Add(rightStack);
        return new Border { Height = 34, Background = Sunken, BorderBrush = BorderDef, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 0), Child = grid };
    }

    private Border SectionToggle(string label, bool initial, Action<bool> set)
    {
        bool on = initial;
        var t = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Height = 22, Padding = new Thickness(9, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Child = t };
        void Paint()
        {
            b.Background = on ? new SolidColorBrush(Color.Parse("#3D84E8")) : new SolidColorBrush(Color.Parse("#2E2E2E"));
            b.BorderBrush = on ? new SolidColorBrush(Color.Parse("#255598")) : new SolidColorBrush(Color.Parse("#484848"));
            t.Foreground = on ? Brushes.White : new SolidColorBrush(Color.Parse("#DCDCDC"));
        }
        b.PointerPressed += (_, e) => { e.Handled = true; on = !on; set(on); Paint(); };
        Paint();
        return b;
    }

    // Crossfader is not in the engine yet — a disabled A/B track with an M7+ badge.
    private Control Crossfader()
    {
        var track = new Border { Width = 120, Height = 4, Background = Sunken, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
        var knob = new Border { Width = 12, Height = 14, Background = KnoxPalette.SurfaceHover, BorderBrush = BorderStrong, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var host = new Panel { Width = 120, Children = { track, knob } };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, IsEnabled = false,
            Children =
            {
                new TextBlock { Text = "A", FontSize = 9, Foreground = TextTertiary, VerticalAlignment = VerticalAlignment.Center },
                host,
                new TextBlock { Text = "B", FontSize = 9, Foreground = TextTertiary, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { row, new NaBadge { Kind = NaBadgeKind.Future } },
        };
    }

    private string NameFor(KnoxTrackInfo ti)
        => ti.IsReturn ? $"Return {_engine.TrackReturnIndex(ti.Id) + 1}"
                       : (ti.IsInstrument ? "Inst " : "Audio ") + ti.Id;

    private Control Header(string name, Color topColor, IBrush bg)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("2,*") };
        grid.Children.Add(new Border { Background = new SolidColorBrush(topColor), Height = 2, VerticalAlignment = VerticalAlignment.Top });
        var label = new TextBlock { Text = name, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 7, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(label, 1);
        grid.Children.Add(label);
        return new Border { Height = 24, Background = bg, BorderBrush = BorderDef, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    private Control IoRow(string io1, string io2) => new Border
    {
        Height = 34, BorderBrush = BorderInner, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(7, 4),
        Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = io1, FontSize = 8, Foreground = TextTertiary },
                new TextBlock { Text = io2, FontSize = 8, Foreground = TextTertiary },
            },
        },
    };

    // Instrument-track I/O row: a "MIDI In" selector (None, or another instrument track whose
    // MIDI this one receives) over the label. Writes/reads via the engine.
    private Control MidiIoRow(int id)
    {
        var cb = new ComboBox
        {
            FontSize = 8, Height = 16, MinHeight = 0, Padding = new Thickness(5, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var sources = new List<int>();
        void Add(string label, int src)
        {
            cb.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = label, FontSize = 8, TextTrimming = TextTrimming.CharacterEllipsis }, Padding = new Thickness(5, 1), MinHeight = 0 });
            sources.Add(src);
        }
        Add("None", -1);
        int n = _engine.TrackCount;
        for (int i = 0; i < n; i++)
            if (_engine.TryGetTrackInfo(i, out var ti) && ti.IsInstrument && ti.Id != id)
                Add(_engine.GetTrackName(ti.Id) is { Length: > 0 } nm ? nm : $"Inst {ti.Id}", ti.Id);
        int cur = _engine.GetTrackMidiSource(id);
        int sel = sources.IndexOf(cur);
        cb.SelectedIndex = sel >= 0 ? sel : 0;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedIndex >= 0 && cb.SelectedIndex < sources.Count)
                _engine.SetTrackMidiSource(id, sources[cb.SelectedIndex]);
        };
        return new Border
        {
            Height = 34, BorderBrush = BorderInner, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(7, 4),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "MIDI in", FontSize = 8, Foreground = TextTertiary },
                    cb,
                },
            },
        };
    }

    private Control SendsRow(int id, int returns)
    {
        var body = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        for (int bus = 0; bus < 2; bus++)
        {
            char letter = (char)('A' + bus);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            grid.Children.Add(new TextBlock { Text = letter.ToString(), FontSize = 8, Foreground = TextTertiary, Width = 8, VerticalAlignment = VerticalAlignment.Center });
            if (bus < returns)
            {
                var f = new MiniFader(_engine.GetTrackSend(id, bus), 1.0) { VerticalAlignment = VerticalAlignment.Center };
                int b = bus;
                f.ValueChanged += v => _engine.SetTrackSend(id, b, (float)v);
                Grid.SetColumn(f, 1); grid.Children.Add(f);
            }
            else
            {
                var flat = new Border { Height = 3, Background = Sunken, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 };
                Grid.SetColumn(flat, 1); grid.Children.Add(flat);
            }
            body.Children.Add(grid);
        }
        return new Border { Height = 42, BorderBrush = BorderInner, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(7, 5), Child = body };
    }

    private Control PanRow(int id, float pan)
    {
        var knob = new PanKnob(pan);
        knob.PanChanged += p => _engine.SetTrackPan(id, (float)p);
        if (id > 0)   // M9-C: record pan moves (master pan isn't an automation target)
        {
            knob.GestureBegin += () => _engine.BeginAutomationWrite(id, AutomationTarget.Pan, -1, -1, "");
            knob.GestureEnd   += () => _engine.EndAutomationWrite(id, AutomationTarget.Pan, -1, -1, "");
            MidiLearn.Bind(knob, MidiTarget.TrackPan(id), "Track Pan");
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { knob, Mono("PAN", TextTertiary) } };
        return new Border { Height = 38, BorderBrush = BorderInner, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }

    // Fader travel is 0..2.0 linear (VFader.Max), i.e. up to +6.0 dB; the dB
    // read-out is drag-editable and double-click-typeable (HANDOFF §4), kept in
    // two-way sync with the fader.
    private const double MaxLin = 2.0;
    private static readonly double MaxDb = AudioMath.LinToDb(MaxLin);
    private const double MinDb = -70.0;
    private static double LinToDb(double v) => v <= 1e-4 ? MinDb : Math.Clamp(AudioMath.LinToDb(v), MinDb, MaxDb);
    private static double DbToLin(double db) => db <= MinDb + 1e-6 ? 0.0 : Math.Clamp(AudioMath.DbToLin(db), 0, MaxLin);

    private Control FaderRow(int id, float vol, Color color, out Control db, out MeterBar meter)
    {
        var fader = new VFader(vol, color);
        var dbNum = new DragNumber(LinToDb(vol), MinDb, MaxDb, 0.3, "+0.0;-0.0;0.0", 8)
        {
            Width = 52, HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(dbNum, "Drag or double-click to set gain (dB)");
        void SetVolume(double v) { if (id < 0) { _masterVolume = (float)v; _engine.SetMasterVolume(_masterVolume); } else _engine.SetTrackVolume(id, (float)v); }
        fader.ValueChanged += v => { SetVolume(v); dbNum.Value = LinToDb(v); };
        dbNum.ValueChanged += d => { double v = DbToLin(d); SetVolume(v); fader.SetValueExternal(v); };
        if (id > 0)   // M9-C: record volume moves (master volume isn't a track target)
        {
            fader.GestureBegin += () => _engine.BeginAutomationWrite(id, AutomationTarget.Volume, -1, -1, "");
            fader.GestureEnd   += () => _engine.EndAutomationWrite(id, AutomationTarget.Volume, -1, -1, "");
        }
        MidiLearn.Bind(fader, id > 0 ? MidiTarget.TrackVolume(id) : MidiTarget.MasterVolume, id > 0 ? "Track Volume" : "Master Volume");
        db = dbNum;

        meter = new MeterBar { Width = 12 };
        var scaleGrid = new Grid { Width = 16, RowDefinitions = new RowDefinitions("*,*,*,*,*") };
        string[] marks = { "+6", "0", "-6", "-12", "-48" };
        for (int i = 0; i < 5; i++)
        {
            var t = new TextBlock
            {
                Text = marks[i],
                FontSize = 7,
                Foreground = new SolidColorBrush(Color.Parse("#A0A0A8")),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = i == 0 ? VerticalAlignment.Top : i == 4 ? VerticalAlignment.Bottom : VerticalAlignment.Center
            };
            t.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
            Grid.SetRow(t, i);
            scaleGrid.Children.Add(t);
        }

        var grid = new Grid { Height = 190, ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(6, 8) };
        grid.Children.Add(scaleGrid);
        Grid.SetColumn(fader, 1); grid.Children.Add(fader);
        Grid.SetColumn(meter, 2); meter.Margin = new Thickness(4, 0, 0, 0); grid.Children.Add(meter);
        return grid;
    }

    private Control ButtonsRow(int id, KnoxTrackInfo ti, bool isReturn)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        bool anySolo = false;
        for (int i = 0; i < _engine.TrackCount; i++)
        {
            if (_engine.TryGetTrackInfo(i, out var oti) && oti.Soloed != 0)
            {
                anySolo = true;
                break;
            }
        }
        bool soloMuted = anySolo && (ti.Soloed == 0);

        var mute = Toggle("M", false, ti.Muted != 0, v => { _engine.SetTrackMute(id, v); Refresh(); }, isSoloMuted: soloMuted);
        var solo = Toggle("S", false, ti.Soloed != 0, v => { _engine.SetTrackSolo(id, v); Refresh(); });
        MidiLearn.Bind(mute, MidiTarget.TrackMute(id), "Mute");
        MidiLearn.Bind(solo, MidiTarget.TrackSolo(id), "Solo");
        row.Children.Add(mute);
        row.Children.Add(solo);
        if (!isReturn) row.Children.Add(Toggle("●", true, ti.Armed != 0, v => _engine.SetTrackArmed(id, v)));
        return new Border { Height = 30, BorderBrush = BorderInner, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 2), Child = row };
    }

    private Border Toggle(string label, bool danger, bool initial, Action<bool> set, bool isSoloMuted = false)
    {
        bool on = initial;
        var t = new TextBlock { Text = label, FontSize = 9.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border
        {
            Width = 24, Height = 20,
            CornerRadius = new CornerRadius(3.5),
            BorderThickness = new Thickness(1.2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = t
        };
        void Paint()
        {
            if (on)
            {
                if (label == "M")
                {
                    b.Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.Parse("#3D84E8"), 0.0),
                            new GradientStop(Color.Parse("#2560B0"), 1.0)
                        }
                    };
                    b.BorderBrush = new SolidColorBrush(Color.Parse("#6EAAFA"));
                    t.Foreground = Brushes.White;
                }
                else if (label == "S")
                {
                    b.Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.Parse("#EAB308"), 0.0),
                            new GradientStop(Color.Parse("#CA8A04"), 1.0)
                        }
                    };
                    b.BorderBrush = new SolidColorBrush(Color.Parse("#FDE047"));
                    t.Foreground = new SolidColorBrush(Color.Parse("#1A1608"));
                }
                else if (danger || label == "●" || label == "R")
                {
                    b.Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.Parse("#EF4444"), 0.0),
                            new GradientStop(Color.Parse("#B91C1C"), 1.0)
                        }
                    };
                    b.BorderBrush = new SolidColorBrush(Color.Parse("#FCA5A5"));
                    t.Foreground = Brushes.White;
                }
                else
                {
                    b.Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.Parse("#3D84E8"), 0.0),
                            new GradientStop(Color.Parse("#2560B0"), 1.0)
                        }
                    };
                    b.BorderBrush = new SolidColorBrush(Color.Parse("#6EAAFA"));
                    t.Foreground = Brushes.White;
                }
            }
            else if (label == "M" && isSoloMuted)
            {
                if (SoloBlinkService.BlinkOn)
                {
                    b.Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.Parse("#3D84E8"), 0.0),
                            new GradientStop(Color.Parse("#2560B0"), 1.0)
                        }
                    };
                    b.BorderBrush = new SolidColorBrush(Color.Parse("#6EAAFA"));
                    t.Foreground = Brushes.White;
                }
                else
                {
                    b.Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.Parse("#323232"), 0.0),
                            new GradientStop(Color.Parse("#282828"), 1.0)
                        }
                    };
                    b.BorderBrush = new SolidColorBrush(Color.Parse("#444444"));
                    t.Foreground = new SolidColorBrush(Color.Parse("#A0A0A0"));
                }
            }
            else
            {
                b.Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse("#323232"), 0.0),
                        new GradientStop(Color.Parse("#282828"), 1.0)
                    }
                };
                b.BorderBrush = new SolidColorBrush(Color.Parse("#444444"));
                t.Foreground = new SolidColorBrush(Color.Parse("#A0A0A0"));
            }
        }

        if (label == "M" && isSoloMuted)
        {
            Action<bool> blinkHandler = _ => Paint();
            SoloBlinkService.BlinkChanged += blinkHandler;
            b.DetachedFromVisualTree += (_, _) => SoloBlinkService.BlinkChanged -= blinkHandler;
        }

        b.PointerPressed += (_, e) => { e.Handled = true; on = !on; set(on); Paint(); };
        Paint();
        return b;
    }

    private TextBlock Mono(string text, IBrush fg)
    {
        var t = new TextBlock { Text = text, FontSize = 8, Foreground = fg, VerticalAlignment = VerticalAlignment.Center };
        t.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
        return t;
    }

    private Control AudioFxRow(int trackId)
    {
        int effectiveTrackId = trackId < 0 ? _engine.MasterTrackId : trackId;
        var panel = new StackPanel { Spacing = 2 };
        int devCount = _engine.TrackDeviceCount(effectiveTrackId);

        for (int i = 0; i < devCount; i++)
        {
            int slotIdx = i;
            string name = _engine.DeviceName(effectiveTrackId, slotIdx);
            bool isBypassed = _engine.DeviceBypassed(effectiveTrackId, slotIdx);

            // Power / Bypass toggle icon
            var powerBtn = new Border
            {
                Width = 14, Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = isBypassed ? new SolidColorBrush(Color.Parse("#2A2D36")) : new SolidColorBrush(Color.Parse("#3884E0")),
                BorderBrush = isBypassed ? new SolidColorBrush(Color.Parse("#444856")) : new SolidColorBrush(Color.Parse("#93C5FD")),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(3, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = "⏻",
                    FontSize = 8,
                    Foreground = isBypassed ? new SolidColorBrush(Color.Parse("#7E8494")) : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            ToolTip.SetTip(powerBtn, isBypassed ? "Activate Effect" : "Bypass Effect");
            powerBtn.PointerPressed += (_, e) =>
            {
                _engine.SetDeviceBypassed(effectiveTrackId, slotIdx, !isBypassed);
                Refresh();
                e.Handled = true;
            };

            var label = new TextBlock
            {
                Text = name,
                FontSize = 8.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = isBypassed ? new SolidColorBrush(Color.Parse("#7E8494")) : Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 78
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { powerBtn, label }
            };

            var slotBorder = new Border
            {
                Height = 19,
                Background = isBypassed ? new SolidColorBrush(Color.Parse("#242730")) : new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse("#1D62AB"), 0.0),
                        new GradientStop(Color.Parse("#144C8C"), 1.0)
                    }
                },
                BorderBrush = isBypassed ? new SolidColorBrush(Color.Parse("#3A3E4D")) : new SolidColorBrush(Color.Parse("#4A96E6")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 1, Blur = 3, Color = Color.FromArgb(0x50, 0, 0, 0) }),
                Cursor = new Cursor(StandardCursorType.Hand),
                Padding = new Thickness(1, 0),
                Child = row
            };

            slotBorder.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(slotBorder).Properties.IsRightButtonPressed)
                {
                    ShowSlotMenu(slotBorder, effectiveTrackId, slotIdx);
                }
                else
                {
                    TrackSelected?.Invoke(effectiveTrackId);
                    DeviceOpenRequested?.Invoke(effectiveTrackId, slotIdx);
                }
                e.Handled = true;
            };
            panel.Children.Add(slotBorder);
        }

        // Plus / Add Effect button slot
        var plusText = new TextBlock
        {
            Text = L10n.Tr("Mixer.AddAudioFx", "+ Audio FX"),
            FontSize = 8.5,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#93C5FD")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var emptySlot = new Border
        {
            Height = 19,
            Background = new SolidColorBrush(Color.Parse("#263244")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3B82F6")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = plusText
        };
        emptySlot.PointerEntered += (_, _) =>
        {
            emptySlot.Background = new SolidColorBrush(Color.Parse("#354660"));
            emptySlot.BorderBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
            plusText.Foreground = Brushes.White;
        };
        emptySlot.PointerExited += (_, _) =>
        {
            emptySlot.Background = new SolidColorBrush(Color.Parse("#263244"));
            emptySlot.BorderBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
            plusText.Foreground = new SolidColorBrush(Color.Parse("#93C5FD"));
        };
        emptySlot.PointerPressed += (_, e) =>
        {
            ShowAddEffectMenu(emptySlot, effectiveTrackId);
            e.Handled = true;
        };
        panel.Children.Add(emptySlot);

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(new TextBlock { Text = "AUDIO FX", FontSize = 8, FontWeight = FontWeight.Bold, Foreground = TextTertiary, VerticalAlignment = VerticalAlignment.Center });
        var addBtn = new TextBlock { Text = "+", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = KnoxPalette.TextSecondary, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
        addBtn.PointerPressed += (_, e) => { ShowAddEffectMenu(addBtn, effectiveTrackId); e.Handled = true; };
        Grid.SetColumn(addBtn, 1);
        headerGrid.Children.Add(addBtn);

        var fxContainer = new StackPanel
        {
            Spacing = 3,
            Children = { headerGrid, panel }
        };

        return new Border
        {
            BorderBrush = BorderInner,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 4),
            Child = fxContainer
        };
    }

    private void ShowSlotMenu(Control anchor, int trackId, int slotIdx)
    {
        var flyout = new MenuFlyout();
        bool isBypassed = _engine.DeviceBypassed(trackId, slotIdx);
        int devCount = _engine.TrackDeviceCount(trackId);

        var bypassItem = new MenuItem { Header = isBypassed ? "✓ Bypass (Inactive)" : "Bypass Effect" };
        bypassItem.Click += (_, _) =>
        {
            _engine.SetDeviceBypassed(trackId, slotIdx, !isBypassed);
            Refresh();
        };
        flyout.Items.Add(bypassItem);

        var openItem = new MenuItem { Header = "Open Editor" };
        openItem.Click += (_, _) =>
        {
            TrackSelected?.Invoke(trackId);
            DeviceOpenRequested?.Invoke(trackId, slotIdx);
            try { _engine.OpenPluginEditor(trackId, slotIdx); } catch { }
        };
        flyout.Items.Add(openItem);

        if (slotIdx > 0)
        {
            var upItem = new MenuItem { Header = "Move Up" };
            upItem.Click += (_, _) =>
            {
                _engine.MoveDevice(trackId, slotIdx, slotIdx - 1);
                Refresh();
            };
            flyout.Items.Add(upItem);
        }

        if (slotIdx < devCount - 1)
        {
            var downItem = new MenuItem { Header = "Move Down" };
            downItem.Click += (_, _) =>
            {
                _engine.MoveDevice(trackId, slotIdx, slotIdx + 1);
                Refresh();
            };
            flyout.Items.Add(downItem);
        }

        flyout.Items.Add(new Separator());

        var delItem = new MenuItem { Header = "Remove Effect" };
        delItem.Click += (_, _) =>
        {
            _engine.RemoveDevice(trackId, slotIdx);
            Refresh();
        };
        flyout.Items.Add(delItem);

        flyout.ShowAt(anchor, showAtPointer: true);
    }

    private void ShowAddEffectMenu(Control anchor, int trackId)
    {
        var flyout = new MenuFlyout();

        var builtinEffects = new (string name, int kind)[]
        {
            ("EQ-8", 0),
            ("EQ-3", 16),
            ("Dynamic EQ", 13),
            ("Compressor", 1),
            ("Ceiling (Limiter)", 14),
            ("Auto Gain", 18),
            ("Reverb", 2),
            ("Delay", 3),
            ("Auto Filter", 7),
            ("Auto Pan", 9),
            ("Auto Shift", 10),
            ("Beat Repeat", 11),
            ("Amp", 6),
            ("Crush", 12),
            ("Vintage Tape", 8),
            ("Forge", 17),
            ("Strata", 15),
            ("Shutter", 19),
            ("Utility", 4),
        };
        foreach (var (name, kind) in builtinEffects)
        {
            int k = kind;
            var mi = new MenuItem { Header = name };
            mi.Click += (_, _) =>
            {
                _engine.AddBuiltinDevice(trackId, k);
                Refresh();
                TrackSelected?.Invoke(trackId);
                DeviceOpenRequested?.Invoke(trackId, _engine.TrackDeviceCount(trackId) - 1);
            };
            flyout.Items.Add(mi);
        }

        if (_catalog is not null && _catalog.Count > 0)
        {
            var pluginMenu = new MenuItem { Header = "Plug-ins (VST3/AU)" };
            int fxCount = 0;
            for (int i = 0; i < _catalog.Count; i++)
            {
                string? desc = _catalog.Description(i);
                if (desc is not null && desc.Contains("| fx |", StringComparison.OrdinalIgnoreCase))
                {
                    int catIdx = i;
                    var parts = desc.Split('|', StringSplitOptions.TrimEntries);
                    string title = parts.Length > 0 ? parts[0] : $"Plugin {i}";
                    if (parts.Length > 3 && parts[3].Length > 0) title = $"{parts[3]} - {title}";
                    var mi = new MenuItem { Header = title };
                    mi.Click += (_, _) =>
                    {
                        _engine.AddTrackEffectPlugin(trackId, catIdx);
                        Refresh();
                        TrackSelected?.Invoke(trackId);
                        DeviceOpenRequested?.Invoke(trackId, _engine.TrackDeviceCount(trackId) - 1);
                    };
                    pluginMenu.Items.Add(mi);
                    fxCount++;
                }
            }
            if (fxCount > 0)
            {
                flyout.Items.Add(new Separator());
                flyout.Items.Add(pluginMenu);
            }
        }

        flyout.ShowAt(anchor, showAtPointer: true);
    }
}

// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Export audio dialog inspired by FL Studio's rendering control layout while adhering
// to Nota's Ember Graphite design system (KnoxPalette neutrals + brass accent).
// Supports WAV, MP3, OGG, FLAC, and M4A formats with format-specific controls,
// project range, tail modes, stems, normalization, and auto-open folder upon completion.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Knox.Application;

namespace Knox.App;

public sealed record ExportOptions(
    ExportAudioFormat Format,
    int SampleRate,
    WavBitDepth Depth,
    int BitrateKbps,
    int FlacCompression,
    ExportTailMode TailMode,
    double TailBeats,
    double RangeBeats,
    bool Stems,
    bool Normalize,
    bool Dither,
    bool OpenDirectoryWhenComplete);

public sealed class ExportWindow : KnoxWindow
{
    private static readonly IBrush Panel = KnoxPalette.SurfaceCard;
    private static readonly IBrush Sunken = KnoxPalette.BgSunken;
    private static readonly IBrush Raised = KnoxPalette.SurfaceRaised;
    private static readonly IBrush BorderDef = KnoxPalette.BorderDefault;
    private static readonly IBrush BorderStrong = KnoxPalette.BorderStrong;
    private static readonly IBrush Brass = KnoxPalette.Accent;
    private static readonly IBrush OnAccent = KnoxPalette.TextOnAccent;
    private static readonly IBrush TextPrimary = KnoxPalette.TextPrimary;
    private static readonly IBrush TextSecondary = KnoxPalette.TextSecondary;
    private static readonly IBrush TextTertiary = KnoxPalette.TextTertiary;

    private static readonly int[] Rates = { 44100, 48000, 96000 };
    private static readonly int[] Bitrates = { 128, 192, 256, 320 };
    private const int BeatsPerBar = 4;

    private readonly double _bpm;
    private readonly double _fullBeats;
    private readonly double _loopBeats;
    private double _rangeBeats;

    // State
    private ExportAudioFormat _format = ExportAudioFormat.Wav;
    private ExportTailMode _tailMode = ExportTailMode.LeaveRemainder;
    private int _rateIndex = 1; // 48000 default
    private int _depthIndex = 1; // 24-bit default
    private int _bitrateIndex = 2; // 256 kbps default
    private int _flacCompression = 5;
    private bool _stems;
    private bool _normalize;
    private bool _dither;
    private bool _openWhenComplete = true;

    // Visual Controls
    private TextBlock _lengthReadout = null!;
    private TextBlock _timeReadout = null!;
    private TextBlock _sizeReadout = null!;
    private TextBlock _exportButtonLabel = null!;
    private Border _flacCompressionTextBg = null!;
    private TextBlock _flacCompressionLabel = null!;

    // Dynamic Format Options Panels
    private Border _wavOptionsPanel = null!;
    private Border _mp3OptionsPanel = null!;
    private Border _oggOptionsPanel = null!;
    private Border _flacOptionsPanel = null!;
    private Border _m4aOptionsPanel = null!;

    private ComboBox _wavDepthCombo = null!;
    private ComboBox _flacDepthCombo = null!;
    private Action? _refreshDitherEnabled;

    public ExportWindow(double fullBeats, double loopBeats, double bpm)
    {
        _bpm = bpm;
        _fullBeats = fullBeats;
        _loopBeats = loopBeats > 0 ? loopBeats : fullBeats;
        _rangeBeats = _fullBeats;

        Title = "Export Project";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = KnoxPalette.BgApp;

        var mainLayout = new StackPanel
        {
            Margin = new Thickness(20, 16),
            Spacing = 12,
        };

        // --- SECTION 1: PROJECT TYPE (Mode, Tail, Readouts) ---
        mainLayout.Children.Add(BuildProjectTypeSection());

        // --- SECTION 2: OUTPUT FORMAT & FORMAT SETTINGS ---
        mainLayout.Children.Add(BuildOutputFormatSection());

        // --- SECTION 3: QUALITY & MISCELLANEOUS ---
        mainLayout.Children.Add(BuildQualityAndMiscSection());

        // --- BOTTOM ACTION BAR ---
        mainLayout.Children.Add(BuildBottomActionBar());

        SetBody(mainLayout);
        UpdateFormatVisibility();
        RefreshReadouts();
    }

    private Control BuildProjectTypeSection()
    {
        var container = new Border
        {
            Background = Panel,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10),
        };

        var vstack = new StackPanel { Spacing = 10 };

        // Title row
        vstack.Children.Add(new TextBlock
        {
            Text = "PROJECT TYPE & RANGE",
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = TextTertiary,
            LetterSpacing = 0.5,
        });

        // Mode & Tail selectors
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Mode selector (Full song / Loop / Custom)
        var modeGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        modeGroup.Children.Add(new TextBlock { Text = "Mode:", FontSize = 11, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        var modeSeg = Segmented(new[] { ("Full song", true), ("Loop", true), ("Custom", false) }, 0, idx =>
        {
            _rangeBeats = idx == 0 ? _fullBeats : _loopBeats;
            RefreshReadouts();
        });
        modeGroup.Children.Add(modeSeg);
        row.Children.Add(modeGroup);

        // Tail selector (Cut remainder / Leave remainder / Wrap remainder)
        var tailGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        tailGroup.Children.Add(new TextBlock { Text = "Tail:", FontSize = 11, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        var tailSeg = Segmented(new[] { ("Cut", true), ("Leave", true), ("Wrap", true) }, 1, idx =>
        {
            _tailMode = idx switch
            {
                0 => ExportTailMode.CutRemainder,
                2 => ExportTailMode.WrapRemainder,
                _ => ExportTailMode.LeaveRemainder,
            };
            RefreshReadouts();
        });
        tailGroup.Children.Add(tailSeg);
        Grid.SetColumn(tailGroup, 2);
        row.Children.Add(tailGroup);

        vstack.Children.Add(row);

        // Readouts Bar (Length, Time, Est. Disk Space)
        var readoutsBar = new Border
        {
            Background = Sunken,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 2, 0, 0),
        };

        var readoutsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _lengthReadout = Mono("Length: 1.1.1 → 1.1.1", TextSecondary);
        _timeReadout = Mono("Time: 0.0s", TextSecondary);
        _sizeReadout = Mono("Est. size: 0.0 MB", TextPrimary);

        readoutsGrid.Children.Add(_lengthReadout);
        Grid.SetColumn(_timeReadout, 1);
        readoutsGrid.Children.Add(_timeReadout);
        Grid.SetColumn(_sizeReadout, 2);
        readoutsGrid.Children.Add(_sizeReadout);

        readoutsBar.Child = readoutsGrid;
        vstack.Children.Add(readoutsBar);

        container.Child = vstack;
        return container;
    }

    private Control BuildOutputFormatSection()
    {
        var container = new Border
        {
            Background = Panel,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10),
        };

        var vstack = new StackPanel { Spacing = 10 };

        // Title row
        vstack.Children.Add(new TextBlock
        {
            Text = "OUTPUT FORMAT",
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = TextTertiary,
            LetterSpacing = 0.5,
        });

        // Format tabs (WAV, MP3, OGG, FLAC, M4A)
        var formatTabs = Segmented(
            new[] { ("WAV", true), ("MP3", true), ("OGG", true), ("FLAC", true), ("M4A", true) },
            0,
            idx =>
            {
                _format = idx switch
                {
                    1 => ExportAudioFormat.Mp3,
                    2 => ExportAudioFormat.Ogg,
                    3 => ExportAudioFormat.Flac,
                    4 => ExportAudioFormat.M4a,
                    _ => ExportAudioFormat.Wav,
                };
                UpdateFormatVisibility();
                RefreshReadouts();
            });
        vstack.Children.Add(formatTabs);

        // Format-specific subpanels
        _wavOptionsPanel = BuildWavPanel();
        _mp3OptionsPanel = BuildBitratePanel("MP3");
        _oggOptionsPanel = BuildBitratePanel("OGG VORBIS");
        _flacOptionsPanel = BuildFlacPanel();
        _m4aOptionsPanel = BuildBitratePanel("M4A / AAC");

        var panelsHost = new Panel();
        panelsHost.Children.Add(_wavOptionsPanel);
        panelsHost.Children.Add(_mp3OptionsPanel);
        panelsHost.Children.Add(_oggOptionsPanel);
        panelsHost.Children.Add(_flacOptionsPanel);
        panelsHost.Children.Add(_m4aOptionsPanel);

        vstack.Children.Add(panelsHost);

        container.Child = vstack;
        return container;
    }

    private Border BuildWavPanel()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };

        // Sample Rate
        var rateCombo = Combo(new[] { "44 100 Hz", "48 000 Hz", "96 000 Hz" }, _rateIndex);
        rateCombo.SelectionChanged += (_, _) =>
        {
            _rateIndex = rateCombo.SelectedIndex >= 0 ? rateCombo.SelectedIndex : 1;
            RefreshReadouts();
        };
        var col0 = Section("SAMPLE RATE", rateCombo);
        grid.Children.Add(col0);

        // Bit Depth
        _wavDepthCombo = Combo(new[] { "16-bit PCM", "24-bit PCM", "32-bit Float" }, _depthIndex);
        _wavDepthCombo.SelectionChanged += (_, _) =>
        {
            _depthIndex = _wavDepthCombo.SelectedIndex >= 0 ? _wavDepthCombo.SelectedIndex : 1;
            _refreshDitherEnabled?.Invoke();
            RefreshReadouts();
        };
        var col1 = Section("BIT DEPTH", _wavDepthCombo);
        Grid.SetColumn(col1, 1);
        grid.Children.Add(col1);

        return new Border { Child = grid, Margin = new Thickness(0, 4, 0, 0) };
    }

    private Border BuildFlacPanel()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };

        // Bit Depth (16-bit or 24-bit)
        _flacDepthCombo = Combo(new[] { "16-bit FLAC", "24-bit FLAC" }, _depthIndex == 0 ? 0 : 1);
        _flacDepthCombo.SelectionChanged += (_, _) =>
        {
            _depthIndex = _flacDepthCombo.SelectedIndex == 0 ? 0 : 1;
            _refreshDitherEnabled?.Invoke();
            RefreshReadouts();
        };
        var col0 = Section("BIT DEPTH", _flacDepthCombo);
        grid.Children.Add(col0);

        // Compression level
        var compStack = new StackPanel { Spacing = 4 };
        var sliderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 8,
            Value = _flacCompression,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _flacCompressionLabel = Mono($"Level {_flacCompression}", TextPrimary);
        _flacCompressionTextBg = new Border
        {
            Background = Sunken,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 2),
            Child = _flacCompressionLabel,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            _flacCompression = (int)Math.Round(e.NewValue);
            _flacCompressionLabel.Text = $"Level {_flacCompression}";
        };
        sliderRow.Children.Add(slider);
        Grid.SetColumn(_flacCompressionTextBg, 1);
        sliderRow.Children.Add(_flacCompressionTextBg);
        compStack.Children.Add(sliderRow);

        var col1 = Section("COMPRESSION LEVEL", compStack);
        Grid.SetColumn(col1, 1);
        grid.Children.Add(col1);

        return new Border { Child = grid, Margin = new Thickness(0, 4, 0, 0) };
    }

    private Border BuildBitratePanel(string formatName)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };

        // Sample Rate
        var rateCombo = Combo(new[] { "44 100 Hz", "48 000 Hz" }, _rateIndex > 1 ? 1 : _rateIndex);
        rateCombo.SelectionChanged += (_, _) =>
        {
            _rateIndex = rateCombo.SelectedIndex >= 0 ? rateCombo.SelectedIndex : 1;
            RefreshReadouts();
        };
        var col0 = Section("SAMPLE RATE", rateCombo);
        grid.Children.Add(col0);

        // Bitrate selector
        var bitrateCombo = Combo(new[] { "128 kbps", "192 kbps", "256 kbps", "320 kbps" }, _bitrateIndex);
        bitrateCombo.SelectionChanged += (_, _) =>
        {
            _bitrateIndex = bitrateCombo.SelectedIndex >= 0 ? bitrateCombo.SelectedIndex : 2;
            RefreshReadouts();
        };
        var col1 = Section("BITRATE", bitrateCombo);
        Grid.SetColumn(col1, 1);
        grid.Children.Add(col1);

        return new Border { Child = grid, Margin = new Thickness(0, 4, 0, 0) };
    }

    private Control BuildQualityAndMiscSection()
    {
        var container = new Border
        {
            Background = Panel,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10),
        };

        var vstack = new StackPanel { Spacing = 10 };

        // Title row
        vstack.Children.Add(new TextBlock
        {
            Text = "QUALITY & MISCELLANEOUS",
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = TextTertiary,
            LetterSpacing = 0.5,
        });

        // First row of options: Normalize & Dither
        var optRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        var normalizeToggle = WorkingToggle("Normalize −1 dBTP", () => _normalize, v => { _normalize = v; });
        var ditherToggle = WorkingToggle("Dither", () => _dither, v => { _dither = v; },
            enabled: () => (_format == ExportAudioFormat.Wav && _depthIndex == 0) || (_format == ExportAudioFormat.Flac && _depthIndex == 0),
            hint: "16-bit only");
        optRow1.Children.Add(normalizeToggle);
        optRow1.Children.Add(ditherToggle);
        vstack.Children.Add(optRow1);

        // Second row of options: Split mixer tracks (Stems)
        var stemsToggle = WorkingToggle("Split mixer tracks (Stems)", () => _stems, v =>
        {
            _stems = v;
            RefreshReadouts();
        }, hint: "one file per track, post-fader");
        vstack.Children.Add(stemsToggle);

        container.Child = vstack;
        return container;
    }

    private Control BuildBottomActionBar()
    {
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Checkbox: "Show file(s) when complete"
        var showFileCheck = ModernCheck("Show file(s) when complete", _openWhenComplete, v => _openWhenComplete = v);
        footer.Children.Add(showFileCheck);

        // Cancel button
        var cancel = new Border
        {
            Background = Sunken,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(16, 0),
            Height = 32,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Cancel",
                FontSize = 11,
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        cancel.PointerPressed += (_, _) => Close(null);
        Grid.SetColumn(cancel, 2);
        cancel.Margin = new Thickness(0, 0, 10, 0);
        footer.Children.Add(cancel);

        // Start / Export button
        _exportButtonLabel = new TextBlock
        {
            Text = "Start Export",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = OnAccent,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var export = new Border
        {
            Background = Brass,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(22, 0),
            Height = 32,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _exportButtonLabel,
        };
        export.PointerPressed += (_, _) => OnExportConfirmed();
        Grid.SetColumn(export, 3);
        footer.Children.Add(export);

        return footer;
    }

    private void OnExportConfirmed()
    {
        int sr = Rates[_rateIndex < Rates.Length ? _rateIndex : 1];
        var d = _depthIndex switch
        {
            0 => WavBitDepth.Pcm16,
            2 => WavBitDepth.Float32,
            _ => WavBitDepth.Pcm24,
        };
        int bitrate = Bitrates[_bitrateIndex < Bitrates.Length ? _bitrateIndex : 2];
        double tailBeats = _tailMode switch
        {
            ExportTailMode.CutRemainder => 0.0,
            _ => BeatsPerBar,
        };
        bool dither = _dither && d == WavBitDepth.Pcm16;

        var result = new ExportOptions(
            _format,
            sr,
            d,
            bitrate,
            _flacCompression,
            _tailMode,
            tailBeats,
            _rangeBeats,
            _stems,
            _normalize,
            dither,
            _openWhenComplete);

        Close(result);
    }

    private void UpdateFormatVisibility()
    {
        _wavOptionsPanel.IsVisible = _format == ExportAudioFormat.Wav;
        _mp3OptionsPanel.IsVisible = _format == ExportAudioFormat.Mp3;
        _oggOptionsPanel.IsVisible = _format == ExportAudioFormat.Ogg;
        _flacOptionsPanel.IsVisible = _format == ExportAudioFormat.Flac;
        _m4aOptionsPanel.IsVisible = _format == ExportAudioFormat.M4a;

        _refreshDitherEnabled?.Invoke();
    }

    private void RefreshReadouts()
    {
        if (_lengthReadout is null || _timeReadout is null || _sizeReadout is null) return;

        double tail = _tailMode == ExportTailMode.CutRemainder ? 0 : BeatsPerBar;
        double totalBeats = _rangeBeats + tail;
        double sec = _bpm > 0 ? totalBeats * 60.0 / _bpm : 0;
        int endBar = (int)(_rangeBeats / BeatsPerBar) + 1;

        _lengthReadout.Text = string.Format(CultureInfo.InvariantCulture, "Length: 1.1.1 → {0}.1.1", endBar);
        _timeReadout.Text = string.Format(CultureInfo.InvariantCulture, "Time: {0:0.0}s ({1:0.0} beats)", sec, totalBeats);

        int sr = Rates[_rateIndex < Rates.Length ? _rateIndex : 1];
        double mb = 0.0;

        switch (_format)
        {
            case ExportAudioFormat.Wav:
                int bytesPerSample = _depthIndex switch { 0 => 2, 2 => 4, _ => 3 };
                mb = sec * sr * 2 * bytesPerSample / 1_000_000.0;
                break;
            case ExportAudioFormat.Flac:
                int flacBps = _depthIndex == 0 ? 2 : 3;
                mb = (sec * sr * 2 * flacBps / 1_000_000.0) * 0.60; // FLAC ~60% ratio
                break;
            case ExportAudioFormat.Mp3:
            case ExportAudioFormat.Ogg:
            case ExportAudioFormat.M4a:
                int kbps = Bitrates[_bitrateIndex < Bitrates.Length ? _bitrateIndex : 2];
                mb = (sec * kbps * 1000.0 / 8.0) / 1_000_000.0;
                break;
        }

        string fmtName = _format.ToString().ToUpperInvariant();
        if (_stems)
        {
            _sizeReadout.Text = string.Format(CultureInfo.InvariantCulture, "Stems: ≈{0:0.0} MB / track", mb);
            _exportButtonLabel.Text = $"Export Stems ({fmtName})";
        }
        else
        {
            _sizeReadout.Text = string.Format(CultureInfo.InvariantCulture, "Est. size: ≈{0:0.0} MB", mb);
            _exportButtonLabel.Text = $"Export {fmtName}";
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private Control Section(string title, Control content) => new StackPanel
    {
        Spacing = 5,
        Children =
        {
            new TextBlock
            {
                Text = title,
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = TextTertiary,
                LetterSpacing = 0.5,
            },
            content
        },
    };

    private ComboBox Combo(string[] items, int selected)
    {
        var c = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 28,
            FontSize = 11,
        };
        foreach (var it in items) c.Items.Add(it);
        c.SelectedIndex = selected >= 0 && selected < items.Length ? selected : 0;
        return c;
    }

    private TextBlock Mono(string text, IBrush fg)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        };
        t.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
        return t;
    }

    private Control WorkingToggle(string label, Func<bool> get, Action<bool> set,
                                  Func<bool>? enabled = null, string? hint = null)
    {
        var knob = new Border
        {
            Width = 26,
            Height = 15,
            CornerRadius = new CornerRadius(8),
            Background = Raised,
            BorderBrush = BorderStrong,
            BorderThickness = new Thickness(1),
        };
        var dot = new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(6),
            Background = TextTertiary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        knob.Child = dot;
        var text = new TextBlock { Text = label, FontSize = 11, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { knob, text }
        };
        if (hint is not null)
            row.Children.Add(new TextBlock { Text = "· " + hint, FontSize = 9, Foreground = TextTertiary, VerticalAlignment = VerticalAlignment.Center });

        void Paint()
        {
            bool on = get();
            knob.Background = on ? Brass : Raised;
            dot.Background = on ? OnAccent : TextTertiary;
            dot.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            dot.Margin = on ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
            text.Foreground = on ? TextPrimary : TextSecondary;
        }

        void Sync()
        {
            bool en = enabled?.Invoke() ?? true;
            if (!en && get()) set(false);
            row.Opacity = en ? 1.0 : 0.4;
            row.IsHitTestVisible = en;
            Paint();
        }

        row.PointerPressed += (_, _) => { set(!get()); Paint(); };
        if (enabled is not null) _refreshDitherEnabled += Sync;
        Sync();
        return row;
    }

    private Control ModernCheck(string label, bool initial, Action<bool> onChange)
    {
        bool state = initial;
        var box = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
        var check = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0 3.5 L3.2 6.7 L9 0"),
            Stroke = OnAccent,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        var glyph = new Viewbox
        {
            Width = 9,
            Height = 7,
            Stretch = Stretch.Uniform,
            Child = check,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Child = glyph;
        var text = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        void Paint()
        {
            box.Background = state ? Brass : Sunken;
            box.BorderBrush = state ? Brass : BorderStrong;
            check.IsVisible = state;
            text.Foreground = state ? TextPrimary : TextSecondary;
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { box, text }
        };
        row.PointerPressed += (_, _) =>
        {
            state = !state;
            Paint();
            onChange(state);
        };
        Paint();
        return row;
    }

    private Control Segmented((string label, bool enabled)[] items, int active, Action<int> onSelect)
    {
        var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var cells = new Border[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            int idx = i;
            var t = new TextBlock { Text = items[i].label, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            var cell = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(12, 3),
                Child = t,
            };
            if (items[i].enabled)
            {
                cell.Cursor = new Cursor(StandardCursorType.Hand);
                cell.PointerPressed += (_, _) =>
                {
                    onSelect(idx);
                    Paint(idx);
                };
            }
            else cell.Opacity = 0.4;
            cells[i] = cell;
            inner.Children.Add(cell);
        }

        void Paint(int a)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                bool on = i == a;
                cells[i].Background = on ? Brass : Brushes.Transparent;
                ((TextBlock)cells[i].Child!).Foreground = on ? OnAccent : TextSecondary;
                ((TextBlock)cells[i].Child!).FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }

        Paint(active);
        return new Border
        {
            Background = Sunken,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(2),
            Child = inner,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}


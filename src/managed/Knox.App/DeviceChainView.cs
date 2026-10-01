// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Detail · Devices (mockup 1d): the selected track's device chain as horizontal
// cards, signal order left→right. Each card has a 26px header (power dot =
// bypass, name, type tag, reorder ◀▶, remove ✕, drag handle ⠿) over a body:
// built-in EQ → interactive response curve, Compressor → param bars + GR meter,
// other built-ins → param bars, hosted plugins → editor stub + latency. Then a
// Sends card (M6-1) and a dashed "+ Add device" target. Reads over the engine's
// device API; edits go straight to the engine.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Knox.Application;
using Knox.Presentation;
using static Knox.App.DeviceCardKit;

namespace Knox.App;

public sealed partial class DeviceChainView : UserControl
{
    // Built-in instrument editor (Synth / Physical): graphs + faders re-read live so
    // automation moves show (RefreshSynthLive). _instLiveViz updates the active card's graphs.
    private Action? _instLiveViz;
    private readonly System.Collections.Generic.List<(int idx, Knob fader, TextBlock val, System.Func<float, string>? fmt)> _instFaders = new();
    // Live device visuals (compressor gain-reduction meter) refreshed on the UI tick.
    private readonly System.Collections.Generic.List<Action> _deviceLiveRefreshers = new();
    // Rack chain param faders that follow their mapped macro (and automation); cleared each Rebuild.
    private readonly System.Collections.Generic.List<Action> _rackParamRefreshers = new();
    private int _rackSelChain;   // which rack/drum chain's device area is shown (persists across Rebuild)

    /// <summary>Raised to save a rack chain's instrument as a preset; arg is the chain index.</summary>
    public event Action<int>? RackPresetSaveRequested;

    private readonly IAudioEngine _engine;
    private readonly IFactoryPresets _factory;
    private readonly IPluginCatalog? _catalog;   // maps a copied plugin's id → catalog index for paste
    private readonly DeviceCardFactory _cardFactory = new();
    private readonly InstrumentCardFactory _instrumentFactory = new();
    private readonly MidiDeviceCardFactory _midiFactory = new();
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10) };
    private int _trackId = -1;

    /// <summary>Raised after a change that affects track headers (remove/reorder).</summary>
    public event Action? Changed;

    /// <summary>Raised to save a device/instrument as a preset (M7-4c); arg is the
    /// device index, or -1 for the track's instrument. MainWindow does the prompt+save.</summary>
    public event Action<int>? PresetSaveRequested;

    /// <summary>Raised when a browser item is dropped on the device panel; MainWindow
    /// routes it (effect → the shown track; instrument → a rack chain / drum pad).</summary>
    public event Action<BrowserItem>? ItemDropped;

    // Accent overlay shown while a browser item is dragged over the panel.
    private readonly Border _dropGlow = new()
    {
        IsVisible = false, IsHitTestVisible = false,
        BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(7),
        BorderBrush = KnoxPalette.AccentBright,
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0xF0, 0xC0, 0x60)),
        Margin = new Thickness(2),
    };

    public DeviceChainView(IAudioEngine engine, IFactoryPresets factory, IPluginCatalog? catalog = null)
    {
        _engine = engine;
        _factory = factory;
        _catalog = catalog;
        Focusable = true;   // so Ctrl+C/X/V + Delete on a selected device reach OnKeyDown
        var scroller = new ScrollViewer
        {
            Content = _row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Content = new Panel { Children = { scroller, _dropGlow } };

        DragDrop.SetAllowDrop(this, true);
        scroller.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(scroller).Properties.IsRightButtonPressed)
            {
                if (e.Source is ScrollViewer or StackPanel or Panel)
                {
                    ShowAddDeviceMenu(this);
                    e.Handled = true;
                }
            }
        };
        DragDrop.AddDragOverHandler(this, (_, e) =>
        {
            bool ok = BrowserView.IsAcceptableDrag(e);
            e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            _dropGlow.IsVisible = ok;
        });
        DragDrop.AddDragLeaveHandler(this, (_, _) => _dropGlow.IsVisible = false);
        DragDrop.AddDropHandler(this, (_, e) =>
        {
            _dropGlow.IsVisible = false;
            var items = BrowserView.DroppedItems(e);
            if (items.Count == 0) return;
            foreach (var item in items) ItemDropped?.Invoke(item);
            e.Handled = true;
        });
    }

    public void Show(int trackId) { _trackId = trackId; _rackSelChain = 0; _selDeviceIndex = -1; _cardExtra.Clear(); Rebuild(); }
    public void Refresh() { if (_trackId > 0) Rebuild(); }
    public int TrackId => _trackId;

    private bool IsInstrumentTrack(int trackId)
    {
        int n = _engine.TrackCount;
        for (int i = 0; i < n; i++)
            if (_engine.TryGetTrackInfo(i, out var ti) && ti.Id == trackId) return ti.IsInstrument;
        return false;
    }

    private bool IsReturnTrack(int trackId) => _engine.TrackReturnIndex(trackId) >= 0;

    private bool IsGroupTrack(int trackId)
    {
        int n = _engine.TrackCount;
        for (int i = 0; i < n; i++)
            if (_engine.TryGetTrackInfo(i, out var ti) && ti.Id == trackId) return ti.IsGroup;
        return false;
    }

    private void Rebuild()
    {
        _row.Children.Clear();
        _instLiveViz = null; _instFaders.Clear();   // rebuilt only when a built-in instrument card is shown
        _deviceLiveRefreshers.Clear();               // compressor GR meter/curve
        _rackParamRefreshers.Clear();                // rack chain param faders (macro live-follow)
        _engine.SetAuditionTrack(_trackId > 0 ? _trackId : -1);
        if (_trackId <= 0) { _row.Children.Add(Hint("Select a track")); return; }

        // MIDI effects sit before the instrument (they shape the note stream).
        int midiCount = _engine.TrackMidiEffectCount(_trackId);
        for (int i = 0; i < midiCount; i++) _row.Children.Add(MidiDeviceCard(i, midiCount));

        if (IsInstrumentTrack(_trackId)) _row.Children.Add(InstrumentCard());

        int count = _engine.TrackDeviceCount(_trackId);
        for (int i = 0; i < count; i++) _row.Children.Add(DeviceCard(i, count));

        int returns = _engine.ReturnTrackCount;
        if (returns > 0 && !IsReturnTrack(_trackId) && !IsGroupTrack(_trackId)) _row.Children.Add(SendsCard());

        _row.Children.Add(AddDeviceCard());
    }

    // ---- device card ------------------------------------------------------

    private Control DeviceCard(int index, int count)
    {
        int kind = _engine.TrackDeviceBuiltinKind(_trackId, index);
        bool bypassed = _engine.DeviceBypassed(_trackId, index);
        string name = _engine.DeviceName(_trackId, index);
        string tag = kind >= 0 ? "BUILT-IN" : "PLUGIN";

        Control body;
        double width;
        bool fullBleed = false;
        if (kind == 5)   // Audio Effect Rack — full-bleed body (fills the shell; no 8px inset
        {                // that would overflow its fixed width and skew hit-testing).
            body = new RackCardView(NewCardContext()).BuildEffectRackBody(index); width = 700; fullBleed = true;
        }
        else
        {
            var strategy = _cardFactory.Resolve(kind);
            body = strategy.Build(NewCardContext(), index);
            width = strategy.AutoWidth ? double.NaN : strategy.Width;   // NaN → card sizes to content
            fullBleed = strategy.FullBleed;
        }
        // Effects share the same shell as instruments (bypass + reorder + remove + preset,
        // and the rich chrome when wide enough). Non-full-bleed bodies get an 8px inset.
        var spec = new ShellSpec(
            Name: name, Subtitle: tag, DeviceIndex: index, Count: count, Bypassed: bypassed, Bypassable: true,
            CanMove: true, CanDelete: true, PresetKind: kind, IsInstrument: false, Width: width, Kind: ChainKind.Effect);
        return BuildCardShell(spec, fullBleed ? body : new Border { Padding = new Thickness(8), Child = body });
    }

    // A fresh seam over this view's engine/track + live-refresh registry + orchestration
    // callbacks, handed to a card strategy. Cheap; only built while (re)building the row.
    private DeviceCardContext NewCardContext()
        => new(_engine, _trackId, _deviceLiveRefreshers.Add, () => Changed?.Invoke(),
               (i, k, v, f) => _instFaders.Add((i, k, v, f)),
               viz => _instLiveViz = viz,
               i => PresetSaveRequested?.Invoke(i),
               () => _rackSelChain, v => _rackSelChain = v,
               _rackParamRefreshers.Add,
               () => { foreach (var r in _rackParamRefreshers) r(); },
               Rebuild,
               c => RackPresetSaveRequested?.Invoke(c),
               () => _dropGlow.IsVisible = false);

    // ---- instrument / sends / add ----------------------------------------

    private Control SendsCard()
    {
        var knobs = new WrapPanel();
        int shown = 0;
        for (int i = 0; i < _engine.TrackCount; i++)
        {
            if (!_engine.TryGetTrackInfo(i, out var ti) || !ti.IsReturn) continue;
            int bus = _engine.TrackReturnIndex(ti.Id);
            if (bus < 0) continue;
            char letter = (char)('A' + bus);

            var value = new TextBlock { Text = Pct(_engine.GetTrackSend(_trackId, bus)), FontSize = 8, Foreground = TextPrimary };
            value.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
            var knob = new Knob(_engine.GetTrackSend(_trackId, bus), 1.0) { Accent = true, Default = 0 };
            int b = bus;
            knob.ValueChanged += v => { _engine.SetTrackSend(_trackId, b, (float)v); value.Text = Pct((float)v); };
            knobs.Children.Add(KnobCell($"Send {letter}", knob, value));
            shown++;
        }
        Control body = shown > 0 ? knobs : new TextBlock { Text = "No return tracks", FontSize = 9, Foreground = TextTertiary, VerticalAlignment = VerticalAlignment.Center };
        return SimpleCard("Sends", shown > 2 ? 200 : 140, body);
    }

    private Control AddDeviceCard()
    {
        var content = new StackPanel
        {
            Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "+", FontSize = 16, Foreground = TextTertiary, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Add device", FontSize = 10, Foreground = TextTertiary, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "click / right-click\nor drop here", FontSize = 8, Foreground = TextDisabled, TextAlignment = TextAlignment.Center },
            },
        };
        // Avalonia Border can't dash its own stroke; overlay a dashed rectangle.
        var dash = new Rectangle
        {
            Stroke = BorderStrong, StrokeThickness = 1,
            StrokeDashArray = new AvaloniaList<double>(4, 3), RadiusX = 7, RadiusY = 7,
        };
        var host = new Grid { Children = { dash, content } };
        var card = new Border
        {
            Width = 140, Height = CardH, Child = host,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
        };
        card.PointerPressed += (_, e) =>
        {
            ShowAddDeviceMenu(card);
            e.Handled = true;
        };
        return card;
    }

    public void ShowAddDeviceMenu(Control anchor, int insertIndex = -1)
    {
        if (_trackId <= 0) return;
        var flyout = new MenuFlyout();

        // 1. Built-in Audio Effects submenu
        var fxMenu = new MenuItem { Header = "Audio Effects (Built-in)" };
        var builtinEffects = new (string name, int kind)[]
        {
            ("Knox EQ-8", 0),
            ("Knox EQ-3", 16),
            ("Knox Dynamic EQ-8", 13),
            ("Knox Compressor", 1),
            ("Knox Ceiling (Limiter)", 14),
            ("Knox Level (Auto Gain)", 18),
            ("Knox Reverb", 2),
            ("Knox Delay", 3),
            ("Knox Auto Filter", 7),
            ("Knox Orbit (Auto-Pan)", 9),
            ("Knox Auto Shift", 10),
            ("Knox Beat Repeat", 11),
            ("Knox Valve (Amp)", 6),
            ("Knox Crush", 12),
            ("Knox Vintage Tape", 8),
            ("Knox Forge (Saturator)", 17),
            ("Knox Strata (Looper)", 15),
            ("Knox Shutter (Gate)", 19),
            ("Knox Audio Effect Rack", 5),
            ("Knox Utility", 4),
        };
        foreach (var (name, kind) in builtinEffects)
        {
            int k = kind;
            var mi = new MenuItem { Header = name };
            mi.Click += (_, _) =>
            {
                int newIdx = _engine.AddBuiltinDevice(_trackId, k);
                if (insertIndex >= 0 && newIdx >= 0 && newIdx != insertIndex)
                {
                    _engine.MoveDevice(_trackId, newIdx, insertIndex);
                }
                Rebuild();
                Changed?.Invoke();
            };
            fxMenu.Items.Add(mi);
        }
        flyout.Items.Add(fxMenu);

        // 2. MIDI Effects (if instrument track)
        if (IsInstrumentTrack(_trackId))
        {
            var midiMenu = new MenuItem { Header = "MIDI Effects" };
            var builtinMidi = new (string name, int kind)[]
            {
                ("Knox Arp", 0),
                ("Knox Chord", 1),
                ("Knox Scale", 2),
                ("Knox Length", 3),
                ("Knox Velocity", 4),
                ("Knox Random", 5),
            };
            foreach (var (name, kind) in builtinMidi)
            {
                int k = kind;
                var mi = new MenuItem { Header = name };
                mi.Click += (_, _) =>
                {
                    _engine.AddMidiEffect(_trackId, k);
                    Rebuild();
                    Changed?.Invoke();
                };
                midiMenu.Items.Add(mi);
            }
            flyout.Items.Add(midiMenu);

            // 3. Instruments (Swap / Replace)
            var instMenu = new MenuItem { Header = "Instruments (Built-in)" };
            var builtinInst = new (string name, int kind)[]
            {
                ("Knox Volt", 6),
                ("Knox Operator", 9),
                ("Knox Synth", 0),
                ("Knox Physical", 2),
                ("Knox Aurora", 5),
                ("Knox Bass", 7),
                ("Knox Pendulum", 8),
                ("Knox Grain", 10),
                ("Knox Flux", 11),
                ("Knox Rhythm", 12),
                ("Knox Monolith", 13),
                ("Knox Sampler", 1),
                ("Knox Instrument Rack", 3),
                ("Knox Drum Rack", 4),
            };
            foreach (var (name, kind) in builtinInst)
            {
                int k = kind;
                var mi = new MenuItem { Header = name };
                mi.Click += (_, _) =>
                {
                    _engine.SetTrackBuiltinInstrument(_trackId, k);
                    Rebuild();
                    Changed?.Invoke();
                };
                instMenu.Items.Add(mi);
            }
            flyout.Items.Add(instMenu);
        }

        // 4. VST3 / AU Plug-ins
        if (_catalog is not null && _catalog.Count > 0)
        {
            var pluginFxMenu = new MenuItem { Header = "Plug-ins (VST3/AU)" };
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
                        int newIdx = _engine.AddTrackEffectPlugin(_trackId, catIdx);
                        if (insertIndex >= 0 && newIdx >= 0 && newIdx != insertIndex)
                        {
                            _engine.MoveDevice(_trackId, newIdx, insertIndex);
                        }
                        Rebuild();
                        Changed?.Invoke();
                    };
                    pluginFxMenu.Items.Add(mi);
                    fxCount++;
                }
            }
            if (fxCount > 0)
            {
                flyout.Items.Add(pluginFxMenu);
            }
        }

        // 5. Paste Device
        if (_clip != null)
        {
            flyout.Items.Add(new Separator());
            var pasteItem = new MenuItem { Header = "Paste Device" };
            pasteItem.Click += (_, _) => PasteDevice();
            flyout.Items.Add(pasteItem);
        }

        flyout.ShowAt(anchor, showAtPointer: true);
    }

}

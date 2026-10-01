// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Knox.Application;
using Knox.Infrastructure;
using Knox.Presentation;
using System.Runtime.InteropServices;

namespace Knox.App;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;
    private readonly HashSet<Key> _heldKeys = new();
    // Computer-keyboard play state: the exact pitch each held note key is sounding (so a
    // note releases at the right pitch even if the octave changed while held), plus the
    // Z/X octave shift and C/V velocity (typing keyboard).
    private readonly Dictionary<Key, int> _heldNotePitch = new();
    private bool _typingKeyboardActive = false;
    private int _typingOctaveIndex = 3; // 1 to 5; Default is 3 (Q = C4 = MIDI 60)
    private int _typingVelocity = 100;  // 1..127, applied to typed notes

    // Application ports (resolved from the DI container when the VM attaches).
    private IProjectStore _projects = default!;
    private IPresetStore _presets = default!;
    private IFactoryPresets _factory = default!;
    private IRecoveryStore _recovery = default!;
    private IAudioExporter _exporter = default!;
    private ISettingsService? _settings;

    private MeterBar? _masterMeter;
    private MidiLearnService? _learn;
    /// <summary>The MIDI-learn service, so secondary windows can host their own learn glass.</summary>
    internal MidiLearnService? LearnService => _learn;

    // Bottom detail panel (Devices chain / Clip piano roll / Mixer).
    private DeviceChainView? _deviceChain;
    private ClipEditorView? _clipEditor;
    private AudioClipEditorView? _audioEditor;
    private MixerView? _mixer;
    private int _editorTrackId = -1;
    private int _editorClipIndex = -1;
    private int _audioEditorTrackId = -1;
    private int _audioEditorClipIndex = -1;
    private PianoRollView? _editorRoll;

    // Remembered detail-panel height once the user drags the splitter, so reopening the
    // panel keeps their size instead of snapping back to the per-mode default. 0 = unset.
    private double _detailHeight;
    private double _lastSetDetailHeight;
    // True while the detail panel (Devices/Clip) was the last area the user interacted
    // with — Tab then toggles between the two tabs.
    private bool _detailWasLastFocused;

    private IAudioEngine Engine => _vm!.Engine;

    public MainWindow()
    {
        InitializeComponent();
        UpdateWindowTitle();
        if (OperatingSystem.IsLinux())
        {
            ExtendClientAreaToDecorationsHint = false;
        }
        else
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 32;
        }
        DataContextChanged += OnDataContextChanged;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Knox.App/Assets/knox.ico")));
        }

        L10n.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLocalization);
        ApplyLocalization();
    }

    // Custom frameless title bar (Phase 2): drag the window by its top bar; a
    // double-click toggles maximise/restore (standard title-bar behaviour).
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual visual)
        {
            for (Visual? cur = visual; cur != null && cur != TitleBar; cur = cur.GetVisualParent())
            {
                if (cur is Menu || cur is MenuItem || cur is Button || cur is ToggleButton || cur is ComboBox)
                    return;
            }
        }
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        BeginMoveDrag(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || _vm is not null) return;
        _vm = vm;

        _learn = App.Services.GetRequiredService<MidiLearnService>();
        LearnOverlay.Service = _learn;
        _learn.PendingChanged += () => LearnOverlay.Refresh();
        Browser.SetMidiLearn(_learn);
        MidiLearn.Bind(PlayBtn, MidiTarget.TransportPlay, "Play");
        MidiLearn.Bind(StopBtn, MidiTarget.TransportStop, "Stop");
        MidiLearn.Bind(RecordBtn, MidiTarget.TransportRecord, "Record");
        MidiLearn.Bind(LoopBtn, MidiTarget.TransportLoop, "Loop");
        MidiLearn.Bind(MetroBtn, MidiTarget.TransportMetronome, "Metronome");
        MidiLearn.Bind(UndoBtn, MidiTarget.ActionUndo, "Undo");
        MidiLearn.Bind(RedoBtn, MidiTarget.ActionRedo, "Redo");
        _learn.CustomActionTriggered += OnMidiCustomAction;

        _projects = App.Services.GetRequiredService<IProjectStore>();
        _presets = App.Services.GetRequiredService<IPresetStore>();
        _factory = App.Services.GetRequiredService<IFactoryPresets>();
        _recovery = App.Services.GetRequiredService<IRecoveryStore>();
        _exporter = App.Services.GetRequiredService<IAudioExporter>();

        Timeline.Engine = vm.Engine;
        Timeline.FreezeRole = FreezeRoleOf;   // live-freeze (v1.1) header badges
        Timeline.MidiClipActivated += OpenClipEditor;
        Timeline.AudioClipActivated += OpenAudioClipEditor;
        Timeline.ItemDropped += OnArrangementDrop;   // browser drag & drop (M7-5)
        Timeline.ConvertClipRequested += OnConvertClip;   // audio clip → MIDI (Convert / Slice)
        SnapChip.ContextRequested += (_, _) => ShowSnapMenu();

        _masterMeter = new MeterBar(horizontal: true);
        MasterMeterHost.Children.Add(_masterMeter);

        _settings = App.Services.GetService<ISettingsService>();
        if (_settings is not null)
        {
            _settings.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLcdSettings);
            ApplyLcdSettings();
            if (!string.IsNullOrEmpty(_settings.Current.ArrangementSnap))
            {
                for (int i = 0; i < SnapSteps.Length; i++)
                {
                    if (string.Equals(SnapSteps[i].label, _settings.Current.ArrangementSnap, StringComparison.OrdinalIgnoreCase))
                    {
                        _snapIndex = i;
                        break;
                    }
                }
            }

            var followMode = (PlayheadFollowMode)Math.Clamp(_settings.Current.PlayheadFollowMode, 0, 2);
            Timeline.FollowMode = followMode;
            Timeline.FollowPlayhead = _settings.Current.FollowPlayheadDefault;
            FollowBtn.IsChecked = Timeline.FollowPlayhead;
            UpdateFollowButtonState();

            _vm.Transport.CountInOn = _settings.Current.CountInOnRecord || _settings.Current.CountInOnPlayback;
            _vm.Transport.CountInOnRecord = _settings.Current.CountInOnRecord;
            _vm.Transport.CountInOnPlayback = _settings.Current.CountInOnPlayback;
            _vm.Transport.CountInBars = _settings.Current.CountInBars;
            UpdateCountInMenuState();
        }
        SetSnapIndex(_snapIndex, saveSetting: false);

        // Rotary Master Volume Knob with default 0.75 (0.0 dB / unity gain) and perceptual taper
        double initialPos = AudioMath.GainToVolumePos((double)vm.Transport.MasterVolume);
        var masterVol = new Knob(initialPos, 0.0, 1.0, defaultValue: 0.75)
        {
            Accent = true,
            Width = 32,
            Height = 32
        };
        void UpdateMasterTip(double pos)
        {
            double g = AudioMath.VolumePosToGain(pos);
            string dbStr = g <= 1e-4 ? "-∞ dB" : $"{AudioMath.LinToDb(g):+0.0;-0.0;0.0} dB";
            ToolTip.SetTip(masterVol, $"Master Volume: {dbStr} ({(int)Math.Round(pos * 100)}%)");
        }
        UpdateMasterTip(initialPos);
        masterVol.ValueChanged += pos =>
        {
            double g = AudioMath.VolumePosToGain(pos);
            vm.Transport.MasterVolume = g;
            UpdateMasterTip(pos);
        };
        MasterVolHost.Children.Add(masterVol);
        MidiLearn.Bind(masterVol, MidiTarget.MasterVolume, "Master Volume");

        // BPM as a drag/type field (4-decimal precision studio style)
        var bpmField = new DragNumber((double)vm.Transport.Bpm, 20, 300, 1.0, "0.0000", fontSize: 13, fgResource: "Brush.LcdPrimary") { UseCommaSeparator = true };
        bpmField.ValueChanged += v =>
        {
            var rounded = (decimal)v;
            if (vm.Transport.Bpm != rounded)
                vm.Transport.Bpm = rounded;
        };
        BpmHost.Children.Add(bpmField);

        vm.Transport.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Transport.Bpm))
            {
                if (!bpmField.IsDragging && Math.Abs(bpmField.Value - (double)vm.Transport.Bpm) > 1e-4)
                    bpmField.Value = (double)vm.Transport.Bpm;
            }
        };

        var cpuGreen = (IBrush?)this.FindResource("Brush.Success");
        var cpuAmber = (IBrush?)this.FindResource("Brush.Warning");
        var cpuRed = (IBrush?)this.FindResource("Brush.Danger");
        vm.PlayheadUpdated += beats =>
        {
            _learn?.Tick();          // apply incoming MIDI mappings / capture a learn message
            LearnOverlay.Refresh();  // keep highlights over rebuilt cards while armed
            Timeline.SetPlayhead(beats);
            _masterMeter?.Push(vm.Engine.MasterMeter());
            Timeline.UpdateMeters();                                   // per-track headers (M6-2)
            Timeline.RefreshAutomationLive();                          // show writes live (M9-C)
            if (_mixer is not null && (_mixer.IsVisible || _mixerWindow is { IsVisible: true })) _mixer.UpdateMeters(); // Mixer tab / popout strips
            if (DetailBody.Content is MixerView mx && mx.IsEffectivelyVisible) mx.UpdateMeters(); // 1f strips
            // Detail contents update wherever they're shown — docked panel or the floating
            // Devices/Clip window — so key off IsEffectivelyVisible, not the docked panel.
            if (_deviceChain is { IsEffectivelyVisible: true }) _deviceChain.RefreshSynthLive(); // synth graphs follow automation
            if (_audioEditor is { IsEffectivelyVisible: true })
                _audioEditor.OnPlayhead(beats, vm.Engine.IsPlaying);       // live clip playback cursor
            // Piano-roll playback cursor: map the global beat to clip-local time for the
            // open arrangement MIDI clip.
            if (_clipEditor is { IsEffectivelyVisible: true } && _editorRoll is not null
                && _editorTrackId > 0 && Engine.TryGetClipInfo(_editorTrackId, _editorClipIndex, out var pci) && pci.IsMidi)
                _editorRoll.SetPlayhead(beats - pci.StartBeat, vm.Engine.IsPlaying);

            double load = Math.Clamp(vm.Engine.CpuLoad, 0, 1);         // live DSP load (Phase 11)
            CpuFill.Width = load * 56;
            CpuFill.Background = load > 0.9 ? cpuRed : load > 0.7 ? cpuAmber : cpuGreen;
            CpuText.Text = ((int)Math.Round(load * 100)) + "%";
            UpdateLcdDisplay(beats);
            UpdateLcdStatusAndTelemetry();

            if (CountInBtnText is not null)
            {
                if (vm.Transport.IsCountingIn)
                {
                    CountInBtnText.Text = vm.Transport.CountInCurrentBeat > 0 ? $"{vm.Transport.CountInCurrentBeat}" : "123";
                }
                else if (CountInBtnText.Text != "123")
                {
                    CountInBtnText.Text = "123";
                }
            }

            if (_isPreviewPlaying)
            {
                if (Engine.IsPlaying) StopBrowserPreview();
                else if (!Engine.IsPreviewActive)
                {
                    _isPreviewPlaying = false;
                    _currentPreviewPath = null;
                    Browser.SetPreviewPlaying(false);
                }
            }
        };
        // Recording tick fires ~60 Hz: refresh clip data + lane drawing only, WITHOUT
        // rebuilding the header cards — otherwise the input combobox and any open track/
        // clip context menu are torn down every frame and can't be used while recording.
        vm.RecordingTick += () => { Timeline.Refresh(rebuildHeaders: false); ReloadEditorNotes(); };
        // Hit Record with nothing armed → auto-arm the selected/last track (M-fix).
        vm.Transport.RecordArmTarget = () => Timeline.RecordArmTarget();
        vm.Transport.TracksChanged += () => { Timeline.Refresh(); _mixer?.Refresh(); ReloadEditorNotes(); };
        Timeline.LoopChanged += () => vm.Transport.SyncLoop();   // ruler drag / "Loop selection" → transport bar
        Timeline.ClipsChanged += ReloadEditorNotes;

        Browser.SetViewModel(vm.Browser);
        Browser.ItemActivated += OnBrowserItemActivated;
        Browser.PreviewRequested += OnBrowserPreview;
        Browser.StopPreviewRequested += StopBrowserPreview;
        Browser.RevealRequested += OnBrowserReveal;
        Browser.DeleteProjectRequested += OnBrowserDeleteProject;
        Browser.EditTagsRequested += OnBrowserEditTags;

        _deviceChain = new DeviceChainView(vm.Engine, _factory, App.Services.GetService<IPluginCatalog>());
        _deviceChain.Changed += () => Timeline.Refresh();
        _deviceChain.PresetSaveRequested += OnSavePreset;
        _deviceChain.RackPresetSaveRequested += OnSaveRackChainPreset;
        _deviceChain.ItemDropped += OnDevicePanelDrop;   // browser drag onto the device panel
        Timeline.TrackSelected += OnTrackSelected;
        Timeline.StatusMessage += msg => { if (_vm is not null) _vm.StatusText = msg; };   // automation-follow hints etc.

        ToolPointerBtn.Click += (_, _) => SetArrangementTool(ArrangementTool.Pointer);
        ToolPencilBtn.Click += (_, _) => SetArrangementTool(ArrangementTool.Pencil);
        ToolCutBtn.Click += (_, _) => SetArrangementTool(ArrangementTool.Cut);
        ToolEraserBtn.Click += (_, _) => SetArrangementTool(ArrangementTool.Eraser);
        ToolStretchBtn.Click += (_, _) => SetArrangementTool(ArrangementTool.Stretch);

        // Mixer can be docked in Detail panel or opened in its own window (View → Mixer)
        _mixer = new MixerView(vm.Engine, App.Services.GetService<IPluginCatalog>()) { IsVisible = false };
        _mixer.TrackSelected += OnTrackSelected;
        _mixer.DeviceOpenRequested += (trackId, _) => ShowDevices(trackId);
        _mixer.PopoutRequested += () => ToggleMixerWindow();

        vm.AutosaveRequested += OnAutosaveTick;
        InitGamepad();   // poll pad buttons on each UI tick (live note source)
        Closing += OnMainWindowClosing;   // clean-shutdown marker (M7-7)
        Opened += OnOpenedRecoveryCheck;  // offer recovery snapshot (M7-7)

        // Losing window focus mid-drag (alt-tab) cancels the gesture with no model change
        // (req 2.11). We use Deactivated rather than PointerCaptureLost because the latter fires
        // on every normal button-up on macOS, which would abort commits.
        Deactivated += (_, _) => Timeline.CancelActiveGesture();
        Deactivated += (_, _) => ReleaseAllHeldTypingNotes();
        // macOS composes the global menu bar from the app menu (App.axaml) plus the
        // *active* window's NativeMenu (MainWindow.axaml). Launched via `dotnet run`
        // (not a .app bundle) the window doesn't reliably become key on show, so the
        // window menu — File/Edit/View/… — intermittently never installs and only the
        // "Nota" app menu appears. Force activation once shown so it always lands.
        Opened += (_, _) => Activate();

        // Remember the detail panel's height once the user drags the splitter — but only
        // on the Clip tab. The Devices tab keeps its natural card-fitting height, so its
        // drags aren't persisted (a programmatic set matches _lastSetDetailHeight and is
        // ignored; only a real drag differs).
        DetailPanel.SizeChanged += (_, e) =>
        {
            bool clipActive = DetailBody.Content is ClipEditorView or AudioClipEditorView;
            if (clipActive && DetailPanel.IsVisible && e.NewSize.Height > 120
                && Math.Abs(e.NewSize.Height - _lastSetDetailHeight) > 1.0)
                _detailHeight = e.NewSize.Height;
        };

        // Track whether the detail panel was the last area the user clicked into, so Tab
        // can toggle its Devices/Clip tabs (see OnKeyDown). Tunnel + handledEventsToo so it
        // sees every press regardless of what consumes it.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual v)
                _detailWasLastFocused = DetailPanel.IsVisible && v.GetSelfAndVisualAncestors().Contains(DetailPanel);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Global transport keys (Space = Play/Stop, Return = Stop) must win over whatever
        // control currently holds focus. A focused Slider/CheckBox/ComboBox/plugin knob
        // consumes Space/Enter in its own bubbling handler *before* the event reaches the
        // window, which made Play/Stop "randomly" toggle some other control instead. Handle
        // them in the tunnel phase so the transport always gets first dibs (text inputs are
        // exempted inside the handler so typing still works). See OnGlobalTransportKey.
        AddHandler(KeyDownEvent, OnGlobalTransportKey, RoutingStrategies.Tunnel);

        Closed += (_, _) => vm.Dispose();

        // TEMP visual-check hook (NOTA_DEBUG_SESSION) — Amplifier card on an audio track.
        if (Environment.GetEnvironmentVariable("NOTA_DEBUG_SESSION") is not null)
        {
            var dbg = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            dbg.Tick += (_, _) =>
            {
                dbg.Stop();
                int t = Engine.AddAudioTrack();
                int ad = Engine.AddBuiltinDevice(t, 6);
                Engine.DeviceSetParam(t, ad, 0, 3f);   // Rock model
                Engine.DeviceSetParam(t, ad, 1, 6f);   // Gain
                Engine.DeviceSetParam(t, ad, 12, 0.4f);// Gate on (visible)
                Timeline.Refresh();
                ShowDevices(t);
            };
            dbg.Start();
        }

        // TEMP (modular editor visual check): seed an instrument track with a MIDI FX
    }

    private void OnAddEmptyTrackClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        int trackId = Engine.AddAudioTrack();
        Timeline.Refresh();
        if (trackId > 0) Timeline.Select(trackId, -1);
        _vm.StatusText = $"Empty track {trackId} added";
    }

    private void OnAddInstrumentClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        int trackId = Engine.AddInstrumentTrack();
        Engine.AddMidiClip(trackId, 0.0, 4.0);
        Timeline.Refresh();
        if (trackId > 0) Timeline.Select(trackId, -1);
        _vm.StatusText = $"Instrument track {trackId} (synth)";
    }

    private void OnAddAudioClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        int trackId = Engine.AddAudioTrack();
        Timeline.Refresh();
        if (trackId > 0) Timeline.Select(trackId, -1);
        _vm.StatusText = $"Audio track {trackId} — arm it and hit Rec to record input";
    }

    private void OnAddReturnClicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        int trackId = Engine.AddReturnTrack();
        if (trackId <= 0) { _vm.StatusText = "All return buses are in use."; return; }
        Timeline.Refresh();
        if (DetailBody.Content == _deviceChain) _deviceChain?.Refresh(); // new bus -> Sends section
        int bus = Engine.TrackReturnIndex(trackId);
        _vm.StatusText = $"Return {bus + 1} added — add an effect, then dial up track sends";
    }

    // Toolbar Snap chip: cycle or select from musical grid options
    private static readonly (double beats, string label)[] SnapSteps =
    {
        (-1.0, "Adaptive (Dynamic)"),
        (4.0, "1 Bar (1/1)"),
        (2.0, "1/2 Bar (1/2)"),
        (1.0, "1 Beat (1/4)"),
        (0.5, "1/2 Beat (1/8)"),
        (1.0 / 3.0, "1/3 Beat (1/12T)"),
        (0.25, "1/16 Span (1/16)"),
        (1.0 / 6.0, "1/16 Triplet (1/24T)"),
        (0.125, "1/32 Span (1/32)"),
        (0.0625, "1/64 Span (1/64)"),
        (0.0, "None (Off)"),
    };
    private int _snapIndex = 0;   // Adaptive (Dynamic) by default
    private void OnCycleSnap(object? sender, RoutedEventArgs e)
    {
        _snapIndex = (_snapIndex + 1) % SnapSteps.Length;
        SetSnapIndex(_snapIndex);
    }

    private void SetSnapIndex(int index, bool saveSetting = true)
    {
        _snapIndex = Math.Clamp(index, 0, SnapSteps.Length - 1);
        var (beats, label) = SnapSteps[_snapIndex];
        Timeline.SnapBeats = beats;
        SnapLabel.Text = label;
        if (_editorRoll is not null)
            _editorRoll.Grid = beats;
        if (saveSetting && _settings is not null)
        {
            _settings.Current.ArrangementSnap = label;
            _settings.Save();
        }
    }

    private void ShowSnapMenu()
    {
        var flyout = new MenuFlyout();
        for (int i = 0; i < SnapSteps.Length; i++)
        {
            int idx = i;
            var mi = new MenuItem
            {
                Header = (idx == _snapIndex ? "✓  " : "    ") + SnapSteps[i].label,
            };
            mi.Click += (_, _) => SetSnapIndex(idx);
            flyout.Items.Add(mi);
        }
        flyout.ShowAt(SnapChip, showAtPointer: true);
    }

    // Session launch-quantize steps (beats, label). 0 = launch immediately (no quantize).
    private static readonly (double beats, string label)[] LaunchQSteps =
    {
        (0.0, "None"), (0.25, "1/16"), (0.5, "1/8"), (1.0, "1/4"), (2.0, "1/2"),
        (4.0, "1 Bar"), (8.0, "2 Bars"), (16.0, "4 Bars"),
    };
    // Rewind / Return to zero
    private void OnRewindToZero(object? sender, RoutedEventArgs e)
    {
        _vm?.Engine.Seek(0);
        Timeline?.SetPlayhead(0.0);
    }

    private void OnLcdPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ShowLcdContextMenu();
            e.Handled = true;
        }
    }

    private void OnTogglePositionDisplay(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ShowLcdContextMenu();
            e.Handled = true;
            return;
        }
        _vm?.Transport.ToggleTimeDisplay();
        e.Handled = true;
    }

    private void OnLocatorsClicked(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ShowLcdContextMenu();
            e.Handled = true;
            return;
        }
        if (_vm is not null)
        {
            _vm.Engine.Seek(_vm.Engine.LoopStart);
            e.Handled = true;
        }
    }

    private void OnLcdModeClicked(object? sender, RoutedEventArgs e)
    {
        ShowLcdContextMenu();
    }

    private void ShowLcdContextMenu()
    {
        var menu = new ContextMenu();

        // Theme Submenu
        var themeMenu = new MenuItem { Header = L10n.Tr("Lcd.Theme", "Color Theme") };
        var themes = new (string id, string name)[]
        {
            ("Cyan", L10n.Tr("Lcd.Theme.Cyan", "Studio Ice Blue (Hardware OLED)")),
            ("Amber", L10n.Tr("Lcd.Theme.Amber", "Warm Amber (Vintage OLED)")),
            ("Green", L10n.Tr("Lcd.Theme.Green", "Matrix Green (Classic LCD)")),
            ("White", L10n.Tr("Lcd.Theme.White", "Studio White (Clean OLED)")),
            ("Red", L10n.Tr("Lcd.Theme.Red", "Neon Red (Darkroom)")),
            ("Purple", L10n.Tr("Lcd.Theme.Purple", "Deep Violet (Synthwave)")),
            ("Gold", L10n.Tr("Lcd.Theme.Gold", "Champagne Gold (Prestige)"))
        };

        string currentTheme = _settings?.Current.LcdColorTheme ?? "Cyan";
        foreach (var (id, name) in themes)
        {
            var item = new MenuItem
            {
                Header = (id == currentTheme ? "✓ " : "   ") + name
            };
            item.Click += (_, _) =>
            {
                if (_settings is not null)
                {
                    _settings.Current.LcdColorTheme = id;
                    _settings.Save();
                    ThemeManager.ApplyLcdTheme(id);
                }
            };
            themeMenu.Items.Add(item);
        }
        menu.Items.Add(themeMenu);

        // Font Submenu
        var fontMenu = new MenuItem { Header = L10n.Tr("Lcd.Font", "Display Font") };
        var fonts = new (string id, string name)[]
        {
            ("Default", L10n.Tr("Lcd.Font.Default", "Default (Knox Standard)")),
            ("Consolas", "Consolas (High-Tech Matrix)"),
            ("Courier New", "Courier New (Classic Terminal)"),
            ("Segoe UI", "Segoe UI (Modern Sans)"),
            ("Lucida Console", "Lucida Console (Retro Studio)"),
            ("JetBrains Mono", "JetBrains Mono (Developer Studio)"),
            ("Trebuchet MS", "Trebuchet MS (Digital Bold)"),
            ("Arial", "Arial (Clean Compact)"),
            ("Roboto Mono", "Roboto Mono (Hardware OLED)")
        };

        string currentFont = _settings?.Current.LcdFontFamily ?? "Default";
        bool matchedPreset = false;
        foreach (var (id, name) in fonts)
        {
            bool isSel = string.Equals(id, currentFont, StringComparison.OrdinalIgnoreCase);
            if (isSel) matchedPreset = true;
            var item = new MenuItem
            {
                Header = (isSel ? "✓ " : "   ") + name
            };
            item.Click += (_, _) =>
            {
                if (_settings is not null)
                {
                    _settings.Current.LcdFontFamily = id;
                    _settings.Save();
                    ApplyLcdSettings();
                }
            };
            fontMenu.Items.Add(item);
        }

        fontMenu.Items.Add(new Separator());
        var customFontItem = new MenuItem
        {
            Header = (!matchedPreset && !string.IsNullOrWhiteSpace(currentFont) ? $"✓ {L10n.Tr("Lcd.Font.Custom", "Other…")} ({currentFont})" : L10n.Tr("Lcd.Font.Custom", "Other… (Custom Font / Özel Font)"))
        };
        customFontItem.Click += async (_, _) => await ShowCustomFontDialog();
        fontMenu.Items.Add(customFontItem);

        menu.Items.Add(fontMenu);

        menu.Items.Add(new Separator());
        var prefsItem = new MenuItem { Header = L10n.Tr("Menu.Settings", "Settings…") };
        prefsItem.Click += (_, _) => OnPreferences(null, null!);
        menu.Items.Add(prefsItem);

        menu.Open(LcdScreen);
    }

    private async System.Threading.Tasks.Task ShowCustomFontDialog()
    {
        var dlg = new CustomFontWindow(_settings?.Current.LcdFontFamily ?? "Default");
        await dlg.ShowDialog(this);
        if (!string.IsNullOrWhiteSpace(dlg.SelectedFont) && _settings is not null)
        {
            _settings.Current.LcdFontFamily = dlg.SelectedFont;
            _settings.Save();
            ApplyLcdSettings();
        }
    }

    private void ApplyLcdSettings()
    {
        if (_settings is null) return;
        var s = _settings.Current;
        ThemeManager.ApplyLcdTheme(s.LcdColorTheme);

        FontFamily? font = null;
        if (!string.IsNullOrWhiteSpace(s.LcdFontFamily) && !string.Equals(s.LcdFontFamily, "Default", StringComparison.OrdinalIgnoreCase))
        {
            try { font = new FontFamily(s.LcdFontFamily); }
            catch { font = FontFamily.Default; }
        }
        else
        {
            if (Avalonia.Application.Current?.TryFindResource("Font.Mono", out var res) == true && res is FontFamily fMono)
                font = fMono;
            else
                font = FontFamily.Default;
        }

        if (LcdSmpteText is not null) LcdSmpteText.FontFamily = font;
        if (LcdMusicalPosText is not null) LcdMusicalPosText.FontFamily = font;
        if (LcdLoopStartText is not null) LcdLoopStartText.FontFamily = font;
        if (LcdLoopEndText is not null) LcdLoopEndText.FontFamily = font;

        UpdateLcdStatusAndTelemetry();
    }

    private static void SetLcdSegment(TextBlock? tb, string barGhost, string barActive, string beat, string div, string tickGhost, string tickActive, IBrush activeBrush, IBrush ghostBrush)
    {
        if (tb is null) return;
        tb.Inlines?.Clear();
        if (tb.Inlines == null) tb.Inlines = new Avalonia.Controls.Documents.InlineCollection();

        // Bar (4 chars): ghost + active
        if (!string.IsNullOrEmpty(barGhost))
            tb.Inlines.Add(new Avalonia.Controls.Documents.Run(barGhost) { Foreground = ghostBrush });
        if (!string.IsNullOrEmpty(barActive))
            tb.Inlines.Add(new Avalonia.Controls.Documents.Run(barActive) { Foreground = activeBrush });

        tb.Inlines.Add(new Avalonia.Controls.Documents.Run("  ") { Foreground = ghostBrush });

        // Beat (1 char)
        tb.Inlines.Add(new Avalonia.Controls.Documents.Run(beat) { Foreground = activeBrush });

        tb.Inlines.Add(new Avalonia.Controls.Documents.Run("  ") { Foreground = ghostBrush });

        // Div (1 char)
        tb.Inlines.Add(new Avalonia.Controls.Documents.Run(div) { Foreground = activeBrush });

        tb.Inlines.Add(new Avalonia.Controls.Documents.Run("  ") { Foreground = ghostBrush });

        // Tick (3 chars): ghost + active
        if (!string.IsNullOrEmpty(tickGhost))
            tb.Inlines.Add(new Avalonia.Controls.Documents.Run(tickGhost) { Foreground = ghostBrush });
        if (!string.IsNullOrEmpty(tickActive))
            tb.Inlines.Add(new Avalonia.Controls.Documents.Run(tickActive) { Foreground = activeBrush });
    }

    private void UpdateLcdDisplay(double beats)
    {
        if (_vm is null) return;

        var activeBrush = this.FindResource("Brush.LcdPrimary") as IBrush ?? Brushes.White;
        var ghostBrush = this.FindResource("Brush.LcdGhost") as IBrush ?? Brushes.DarkSlateGray;

        double bpm = (double)_vm.Transport.Bpm;
        int beatsPerBar = _vm.Transport.TimeSigNumerator > 0 ? _vm.Transport.TimeSigNumerator : 4;

        // 1. Playhead Position (Left Section)
        double totalSec = bpm > 0 ? Math.Max(0, beats) * 60.0 / bpm : 0.0;
        int hrs = (int)(totalSec / 3600.0);
        int mins = (int)((totalSec % 3600.0) / 60.0);
        int secs = (int)(totalSec % 60.0);
        double remSec = totalSec - Math.Floor(totalSec);
        int frames = (int)(remSec * 25.0);
        int subframes = (int)((remSec * 25.0 - frames) * 100.0);
        if (LcdSmpteText is not null)
            LcdSmpteText.Text = $"{(hrs + 1):00}:{mins:00}:{secs:00}:{frames:00}.{subframes:00}";

        // Musical Position: 0008  3  2  151
        int bar = (int)(beats / beatsPerBar) + 1;
        int beat = (int)(beats % beatsPerBar) + 1;
        double fracBeat = beats - Math.Floor(beats);
        int div = (int)(fracBeat * 4.0) + 1;
        double fracDiv = (fracBeat * 4.0) - Math.Floor(fracBeat * 4.0);
        int tick = (int)(fracDiv * 240.0) + 1;

        string barFull = bar.ToString().PadLeft(4, '0');
        int barNonZero = 0;
        while (barNonZero < barFull.Length - 1 && barFull[barNonZero] == '0') barNonZero++;
        string barGhost = barNonZero > 0 ? barFull.Substring(0, barNonZero) : "";
        string barActive = barFull.Substring(barNonZero);

        string tickFull = tick.ToString().PadLeft(3, '0');
        int tickNonZero = 0;
        while (tickNonZero < tickFull.Length - 1 && tickFull[tickNonZero] == '0') tickNonZero++;
        string tickGhost = tickNonZero > 0 ? tickFull.Substring(0, tickNonZero) : "";
        string tickActive = tickFull.Substring(tickNonZero);

        SetLcdSegment(LcdMusicalPosText, barGhost, barActive, beat.ToString(), div.ToString(), tickGhost, tickActive, activeBrush, ghostBrush);

        // 2. Loop Locators (Middle Section)
        double loopS = _vm.Engine.LoopStart;
        double loopE = _vm.Engine.LoopEnd;

        // Loop Start: e.g. 0004  1  1  001
        int lsBar = (int)(loopS / beatsPerBar) + 1;
        int lsBeat = (int)(loopS % beatsPerBar) + 1;
        double lsFracBeat = loopS - Math.Floor(loopS);
        int lsDiv = (int)(lsFracBeat * 4.0) + 1;
        double lsFracDiv = (lsFracBeat * 4.0) - Math.Floor(lsFracBeat * 4.0);
        int lsTick = (int)(lsFracDiv * 240.0) + 1;

        string lsBarFull = lsBar.ToString().PadLeft(4, '0');
        int lsBarNz = 0;
        while (lsBarNz < lsBarFull.Length - 1 && lsBarFull[lsBarNz] == '0') lsBarNz++;
        string lsBarGhost = lsBarNz > 0 ? lsBarFull.Substring(0, lsBarNz) : "";
        string lsBarActive = lsBarFull.Substring(lsBarNz);

        string lsTickFull = lsTick.ToString().PadLeft(3, '0');
        int lsTickNz = 0;
        while (lsTickNz < lsTickFull.Length - 1 && lsTickFull[lsTickNz] == '0') lsTickNz++;
        string lsTickGhost = lsTickNz > 0 ? lsTickFull.Substring(0, lsTickNz) : "";
        string lsTickActive = lsTickFull.Substring(lsTickNz);

        SetLcdSegment(LcdLoopStartText, lsBarGhost, lsBarActive, lsBeat.ToString(), lsDiv.ToString(), lsTickGhost, lsTickActive, activeBrush, ghostBrush);

        // Loop End: e.g. 0021  1  1  001
        int leBar = (int)(loopE / beatsPerBar) + 1;
        int leBeat = (int)(loopE % beatsPerBar) + 1;
        double leFracBeat = loopE - Math.Floor(loopE);
        int leDiv = (int)(leFracBeat * 4.0) + 1;
        double leFracDiv = (leFracBeat * 4.0) - Math.Floor(leFracBeat * 4.0);
        int leTick = (int)(leFracDiv * 240.0) + 1;

        string leBarFull = leBar.ToString().PadLeft(4, '0');
        int leBarNz = 0;
        while (leBarNz < leBarFull.Length - 1 && leBarFull[leBarNz] == '0') leBarNz++;
        string leBarGhost = leBarNz > 0 ? leBarFull.Substring(0, leBarNz) : "";
        string leBarActive = leBarFull.Substring(leBarNz);

        string leTickFull = leTick.ToString().PadLeft(3, '0');
        int leTickNz = 0;
        while (leTickNz < leTickFull.Length - 1 && leTickFull[leTickNz] == '0') leTickNz++;
        string leTickGhost = leTickNz > 0 ? leTickFull.Substring(0, leTickNz) : "";
        string leTickActive = leTickFull.Substring(leTickNz);

        SetLcdSegment(LcdLoopEndText, leBarGhost, leBarActive, leBeat.ToString(), leDiv.ToString(), leTickGhost, leTickActive, activeBrush, ghostBrush);
    }

    private void UpdateLcdStatusAndTelemetry()
    {
    }

    // Follow: keep the arrangement scrolling with the playhead during playback according to FollowMode.
    private void OnToggleFollow(object? sender, RoutedEventArgs e)
    {
        Timeline.FollowPlayhead = (sender as ToggleButton)?.IsChecked == true;
        if (Timeline.FollowPlayhead) Timeline.RecenterOnPlayhead();   // jump to the cursor now
        if (_settings is not null)
        {
            _settings.Current.FollowPlayheadDefault = Timeline.FollowPlayhead;
            _settings.Save();
        }
        UpdateFollowButtonState();
    }

    private void OnSelectFollowModePaged(object? sender, RoutedEventArgs e)
    {
        SetFollowMode(PlayheadFollowMode.Paged);
    }

    private void OnSelectFollowModeLeft(object? sender, RoutedEventArgs e)
    {
        SetFollowMode(PlayheadFollowMode.ContinuousLeft);
    }

    private void OnSelectFollowModeCenter(object? sender, RoutedEventArgs e)
    {
        SetFollowMode(PlayheadFollowMode.ContinuousCenter);
    }

    private void SetFollowMode(PlayheadFollowMode mode)
    {
        Timeline.FollowMode = mode;
        Timeline.FollowPlayhead = true;
        FollowBtn.IsChecked = true;
        if (_settings is not null)
        {
            _settings.Current.PlayheadFollowMode = (int)mode;
            _settings.Current.FollowPlayheadDefault = true;
            _settings.Save();
        }
        Timeline.RecenterOnPlayhead();
        UpdateFollowButtonState();
    }

    private void UpdateFollowButtonState()
    {
        string modeLabel = Timeline.FollowMode switch
        {
            PlayheadFollowMode.ContinuousLeft => "Continuous from Start",
            PlayheadFollowMode.ContinuousCenter => "Continuous Centered",
            _ => "Page Jump",
        };
        string state = Timeline.FollowPlayhead ? "On" : "Off";
        ToolTip.SetTip(FollowBtn, $"Follow Playhead: {state} ({modeLabel})\nRight-click to change mode");

        if (FollowModePagedItem != null) FollowModePagedItem.Header = (Timeline.FollowMode == PlayheadFollowMode.Paged ? "✓ " : "   ") + "Page Jump";
        if (FollowModeLeftItem != null) FollowModeLeftItem.Header = (Timeline.FollowMode == PlayheadFollowMode.ContinuousLeft ? "✓ " : "   ") + "Continuous from Start";
        if (FollowModeCenterItem != null) FollowModeCenterItem.Header = (Timeline.FollowMode == PlayheadFollowMode.ContinuousCenter ? "✓ " : "   ") + "Continuous Centered";
    }

    // MIDI Learn: arm/disarm the overlay and reveal the mappings tab so the user
    // can see what they're binding.
    private void OnToggleMidiLearn(object? sender, RoutedEventArgs e)
    {
        if (_learn is null) return;
        bool on = MidiLearnBtn.IsChecked == true;
        _learn.Armed = on;
        if (on) Browser.ShowMidiMap();
    }

    private void OnToggleAutomation(object? sender, RoutedEventArgs e)
    {
        bool on = AutomationToggle.IsChecked == true;
        Timeline.AutomationMode = on;
        AutoModeBar.IsVisible = on;   // record-mode selector is only relevant in automation mode
        if (!on) SetAutoMode(AutomationWriteMode.Read, AutoModeRead); // no stray recording while hidden
    }

    // Automation record mode (M9-C): mutually-exclusive segmented Read/Touch/Latch/Write.
    private void SetAutoMode(AutomationWriteMode mode, ToggleButton active)
    {
        AutoModeRead.IsChecked = active == AutoModeRead;
        AutoModeTouch.IsChecked = active == AutoModeTouch;
        AutoModeLatch.IsChecked = active == AutoModeLatch;
        AutoModeWrite.IsChecked = active == AutoModeWrite;
        _vm?.Engine.SetAutomationWriteMode(mode);
        if (Timeline is not null) Timeline.AutomationWriteMode = mode;
    }
    private void OnAutoModeRead(object? sender, RoutedEventArgs e) => SetAutoMode(AutomationWriteMode.Read, AutoModeRead);
    private void OnAutoModeTouch(object? sender, RoutedEventArgs e) => SetAutoMode(AutomationWriteMode.Touch, AutoModeTouch);
    private void OnAutoModeLatch(object? sender, RoutedEventArgs e) => SetAutoMode(AutomationWriteMode.Latch, AutoModeLatch);
    private void OnAutoModeWrite(object? sender, RoutedEventArgs e) => SetAutoMode(AutomationWriteMode.Write, AutoModeWrite);

    internal void SetArrangementTool(ArrangementTool tool)
    {
        Timeline.CurrentTool = tool;
        ToolPointerBtn.IsChecked = tool == ArrangementTool.Pointer;
        ToolPencilBtn.IsChecked = tool == ArrangementTool.Pencil;
        ToolCutBtn.IsChecked = tool == ArrangementTool.Cut;
        ToolEraserBtn.IsChecked = tool == ArrangementTool.Eraser;
        ToolStretchBtn.IsChecked = tool == ArrangementTool.Stretch;
        if (_vm is not null)
            _vm.StatusText = $"Tool: {tool}";
    }

    private void ApplyLocalization()
    {
        UpdateWindowTitle();

        // Menu Bar
        if (MenuFile is not null) MenuFile.Header = L10n.Tr("Menu.File", "File");
        if (MenuNew is not null) MenuNew.Header = L10n.Tr("Menu.New", "New");
        if (MenuOpen is not null) MenuOpen.Header = L10n.Tr("Menu.Open", "Open…");
        if (MenuSave is not null) MenuSave.Header = L10n.Tr("Menu.Save", "Save");
        if (MenuSaveAs is not null) MenuSaveAs.Header = L10n.Tr("Menu.SaveAs", "Save As…");
        if (MenuImport is not null) MenuImport.Header = L10n.Tr("Menu.Import", "Import audio…");
        if (MenuExport is not null) MenuExport.Header = L10n.Tr("Menu.Export", "Export audio…");
        if (MenuSettings is not null) MenuSettings.Header = L10n.Tr("Menu.Settings", "Settings…");

        if (MenuEdit is not null) MenuEdit.Header = L10n.Tr("Menu.Edit", "Edit");
        if (MenuUndo is not null) MenuUndo.Header = L10n.Tr("Menu.Undo", "Undo");
        if (MenuRedo is not null) MenuRedo.Header = L10n.Tr("Menu.Redo", "Redo");
        if (MenuDuplicate is not null) MenuDuplicate.Header = L10n.Tr("Menu.DuplicateClip", "Duplicate clip");
        if (MenuSplit is not null) MenuSplit.Header = L10n.Tr("Menu.Split", "Split at playhead");
        if (MenuLockEnvelopes is not null) MenuLockEnvelopes.Header = L10n.Tr("Menu.LockEnvelopes", "Lock Envelopes");
        if (MenuCut is not null) MenuCut.Header = L10n.Tr("Menu.Cut", "Cut");
        if (MenuCopy is not null) MenuCopy.Header = L10n.Tr("Menu.Copy", "Copy");
        if (MenuPaste is not null) MenuPaste.Header = L10n.Tr("Menu.Paste", "Paste");
        if (MenuDeleteSel is not null) MenuDeleteSel.Header = L10n.Tr("Menu.Delete", "Delete");

        if (MenuView is not null) MenuView.Header = L10n.Tr("Menu.View", "View");
        if (MenuMixer is not null) MenuMixer.Header = L10n.Tr("Menu.Mixer", "Mixer");
        if (MenuToggleBrowser is not null) MenuToggleBrowser.Header = L10n.Tr("Menu.ToggleBrowser", "Toggle browser");
        if (MenuToggleClip is not null) MenuToggleClip.Header = L10n.Tr("Menu.ToggleDetail", "Toggle detail panel");

        if (MenuWindow is not null) MenuWindow.Header = L10n.Tr("Menu.Window", "Window");

        if (MenuHelp is not null) MenuHelp.Header = L10n.Tr("Menu.Help", "Help");
        if (MenuWhatsNew is not null) MenuWhatsNew.Header = L10n.Tr("Menu.WhatsNew", "What's New");
        if (MenuAbout is not null) MenuAbout.Header = L10n.Tr("Menu.About", "About Knox");

        // Transport tooltips
        if (RewindBtn is not null) ToolTip.SetTip(RewindBtn, L10n.Tr("Transport.RewindTip", "Return to Zero (Return)"));
        if (StopBtn is not null) ToolTip.SetTip(StopBtn, L10n.Tr("Transport.StopTip", "Stop (Space / Return)"));
        if (PlayBtn is not null) ToolTip.SetTip(PlayBtn, L10n.Tr("Transport.PlayTip", "Play (Space)"));
        if (RecordBtn is not null) ToolTip.SetTip(RecordBtn, L10n.Tr("Transport.RecordTip", "Record (R)"));
        if (FollowBtn is not null) ToolTip.SetTip(FollowBtn, L10n.Tr("Transport.FollowTip", "Follow playhead"));
        if (CountInBtn is not null) ToolTip.SetTip(CountInBtn, L10n.Tr("Transport.CountInTip", "Count-in (1,2,3,4) — Right-click for options"));
        if (CountInHeaderItem is not null) CountInHeaderItem.Header = L10n.Tr("Transport.CountInOptions", "Count-in Options");
        UpdateCountInMenuState();

        // Toolbar
        if (SnapChipText is not null) SnapChipText.Text = L10n.Tr("Tool.Snap", "Snap");
        if (SnapChip is not null) ToolTip.SetTip(SnapChip, L10n.Tr("Tool.SnapTip", "Clip snap grid"));
        if (AutomationToggle is not null)
        {
            AutomationToggle.Content = L10n.Tr("Tool.Automation", "Automation");
            ToolTip.SetTip(AutomationToggle, L10n.Tr("Tool.AutomationTip", "Show parameter-automation lanes"));
        }
        if (AutoModeRead is not null) AutoModeRead.Content = L10n.Tr("Tool.AutoRead", "Read");
        if (AutoModeTouch is not null) AutoModeTouch.Content = L10n.Tr("Tool.AutoTouch", "Touch");
        if (AutoModeLatch is not null) AutoModeLatch.Content = L10n.Tr("Tool.AutoLatch", "Latch");
        if (AutoModeWrite is not null) AutoModeWrite.Content = L10n.Tr("Tool.AutoWrite", "Write");

        // Detail Panel
        if (DetailMixerBtn is not null) DetailMixerBtn.Content = L10n.Tr("Detail.Mixer", "Mixer");
        if (DetailDevicesBtn is not null) DetailDevicesBtn.Content = L10n.Tr("Detail.Devices", "Devices");
        if (DetailClipBtn is not null)
        {
            DetailClipBtn.Content = L10n.Tr("Detail.Clip", "Clip");
            ToolTip.SetTip(DetailClipBtn, L10n.Tr("Detail.ClipTip", "Select a clip to edit"));
        }
        if (FreezeLabel is not null) FreezeLabel.Text = L10n.Tr("Detail.Freeze", "Freeze");
        if (FreezeBtn is not null) ToolTip.SetTip(FreezeBtn, L10n.Tr("Detail.FreezeTip", "Freeze track"));
        if (LiveFreezeLabel is not null) LiveFreezeLabel.Text = L10n.Tr("Detail.LiveFreeze", "Live Freeze");
        if (LiveFreezeBtn is not null) ToolTip.SetTip(LiveFreezeBtn, L10n.Tr("Detail.LiveFreezeTip", "Live Freeze"));
    }

    #region Typing Keyboard to Piano

    public bool TypingKeyboardActive
    {
        get => _typingKeyboardActive;
        set
        {
            _typingKeyboardActive = value;
            if (TypingKeyboardToggle != null) TypingKeyboardToggle.IsChecked = value;
            KnoxEngine.SetTypingActive(value);
            if (!value) ReleaseAllHeldTypingNotes();
        }
    }

    public int TypingOctaveIndex
    {
        get => _typingOctaveIndex;
        set => SetTypingOctave(value);
    }

    public void SetTypingOctave(int octave)
    {
        octave = Math.Clamp(octave, 1, 5);
        if (_typingOctaveIndex == octave) return;
        ReleaseAllHeldTypingNotes();
        _typingOctaveIndex = octave;
        KnoxEngine.SetTypingOctave(octave);
        UpdateOctaveMenuCheckmarks();
        if (_vm != null)
        {
            _vm.StatusText = $"Typing Keyboard: Octave {_typingOctaveIndex} (Q = C{_typingOctaveIndex + 1})";
        }
    }

    private void UpdateOctaveMenuCheckmarks()
    {
        if (Octave1Item != null) Octave1Item.Header = _typingOctaveIndex == 1 ? "✓ Octave 1 (C2)" : "   Octave 1 (C2)";
        if (Octave2Item != null) Octave2Item.Header = _typingOctaveIndex == 2 ? "✓ Octave 2 (C3)" : "   Octave 2 (C3)";
        if (Octave3Item != null) Octave3Item.Header = _typingOctaveIndex == 3 ? "✓ Octave 3 (C4) — Default" : "   Octave 3 (C4) — Default";
        if (Octave4Item != null) Octave4Item.Header = _typingOctaveIndex == 4 ? "✓ Octave 4 (C5)" : "   Octave 4 (C5)";
        if (Octave5Item != null) Octave5Item.Header = _typingOctaveIndex == 5 ? "✓ Octave 5 (C6)" : "   Octave 5 (C6)";
    }

    private void ReleaseAllHeldTypingNotes()
    {
        if (_heldNotePitch.Count > 0)
        {
            foreach (var pitch in _heldNotePitch.Values)
            {
                Engine.NoteOff(pitch);
            }
            _heldNotePitch.Clear();
        }
    }

    private void OnToggleTypingKeyboard(object? sender, RoutedEventArgs e)
    {
        _typingKeyboardActive = TypingKeyboardToggle?.IsChecked == true;
        KnoxEngine.SetTypingActive(_typingKeyboardActive);
        if (!_typingKeyboardActive)
        {
            ReleaseAllHeldTypingNotes();
            if (_vm != null) _vm.StatusText = "Typing Keyboard: Disabled";
        }
        else
        {
            if (_vm != null) _vm.StatusText = $"Typing Keyboard: Enabled (Octave {_typingOctaveIndex}, Q = C{_typingOctaveIndex + 1})";
        }
    }

    private void OnSelectOctave1(object? sender, RoutedEventArgs e) => SetTypingOctave(1);
    private void OnSelectOctave2(object? sender, RoutedEventArgs e) => SetTypingOctave(2);
    private void OnSelectOctave3(object? sender, RoutedEventArgs e) => SetTypingOctave(3);
    private void OnSelectOctave4(object? sender, RoutedEventArgs e) => SetTypingOctave(4);
    private void OnSelectOctave5(object? sender, RoutedEventArgs e) => SetTypingOctave(5);

    private void OnMenuOctaveDown(object? sender, RoutedEventArgs e) => SetTypingOctave(_typingOctaveIndex - 1);
    private void OnMenuOctaveUp(object? sender, RoutedEventArgs e) => SetTypingOctave(_typingOctaveIndex + 1);

    #endregion

    #region Count-In (1,2,3)

    private void OnToggleCountIn(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Transport.CountInOn = CountInBtn.IsChecked == true;
        if (_settings is not null)
        {
            _settings.Current.CountInOnRecord = _vm.Transport.CountInOn;
            _settings.Save();
        }
        UpdateCountInMenuState();
    }

    private void OnToggleCountInRecord(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Transport.CountInOnRecord = !_vm.Transport.CountInOnRecord;
        if (_settings is not null)
        {
            _settings.Current.CountInOnRecord = _vm.Transport.CountInOnRecord;
            _settings.Save();
        }
        UpdateCountInMenuState();
    }

    private void OnToggleCountInPlayback(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Transport.CountInOnPlayback = !_vm.Transport.CountInOnPlayback;
        if (_settings is not null)
        {
            _settings.Current.CountInOnPlayback = _vm.Transport.CountInOnPlayback;
            _settings.Save();
        }
        UpdateCountInMenuState();
    }

    private void OnSelectCountIn1Bar(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Transport.CountInBars = 1;
        if (_settings is not null)
        {
            _settings.Current.CountInBars = 1;
            _settings.Save();
        }
        UpdateCountInMenuState();
    }

    private void OnSelectCountIn2Bars(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Transport.CountInBars = 2;
        if (_settings is not null)
        {
            _settings.Current.CountInBars = 2;
            _settings.Save();
        }
        UpdateCountInMenuState();
    }

    private void UpdateCountInMenuState()
    {
        if (_vm is null) return;
        if (CountInBtn is not null)
            CountInBtn.IsChecked = _vm.Transport.CountInOn && (_vm.Transport.CountInOnRecord || _vm.Transport.CountInOnPlayback);

        if (CountInRecordItem is not null)
            CountInRecordItem.Header = (_vm.Transport.CountInOnRecord ? "✓ " : "   ") + L10n.Tr("Transport.CountInRecord", "Count-in on Record");

        if (CountInPlaybackItem is not null)
            CountInPlaybackItem.Header = (_vm.Transport.CountInOnPlayback ? "✓ " : "   ") + L10n.Tr("Transport.CountInPlayback", "Count-in on Playback");

        if (CountIn1BarItem is not null)
            CountIn1BarItem.Header = (_vm.Transport.CountInBars == 1 ? "✓ " : "   ") + L10n.Tr("Transport.CountIn1Bar", "1 Bar (4 Beats)");

        if (CountIn2BarsItem is not null)
            CountIn2BarsItem.Header = (_vm.Transport.CountInBars == 2 ? "✓ " : "   ") + L10n.Tr("Transport.CountIn2Bars", "2 Bars (8 Beats)");
    }

    #endregion

    #region MIDI Custom Actions & Velocity Curve

    private void OnMidiCustomAction(MidiTargetKind kind)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null) return;
            switch (kind)
            {
                case MidiTargetKind.TransportMetronome:
                    _vm.Transport.MetronomeOn = !_vm.Transport.MetronomeOn;
                    break;
                case MidiTargetKind.TransportLoop:
                    _vm.Transport.LoopOn = !_vm.Transport.LoopOn;
                    break;
                case MidiTargetKind.TransportRewind:
                    _vm.Transport.StopCommand.Execute(null);
                    Timeline.SeekTo(0);
                    Timeline.ScrollByBeats(-Timeline.ScrollBeats);
                    break;
                case MidiTargetKind.TransportFastForward:
                    Timeline.SeekTo(Timeline.PlayheadBeats + 4.0);
                    break;
                case MidiTargetKind.TransportTapTempo:
                    break;
                case MidiTargetKind.ActionUndo:
                    OnMenuUndo(this, EventArgs.Empty);
                    break;
                case MidiTargetKind.ActionRedo:
                    OnMenuRedo(this, EventArgs.Empty);
                    break;
                case MidiTargetKind.ActionPrevTrack:
                    SelectRelativeTrack(-1);
                    break;
                case MidiTargetKind.ActionNextTrack:
                    SelectRelativeTrack(1);
                    break;
                case MidiTargetKind.ActionDuplicate:
                    OnMenuDuplicate(this, EventArgs.Empty);
                    break;
                case MidiTargetKind.ActionSplit:
                    OnMenuSplit(this, EventArgs.Empty);
                    break;
                case MidiTargetKind.ActionQuantize:
                    _editorRoll?.Quantize(1.0);
                    break;
            }
        });
    }

    private void SelectRelativeTrack(int delta)
    {
        int count = Engine.TrackCount;
        if (count <= 0) return;
        int currentId = Timeline.SelectedTrackId;
        int currentIdx = -1;
        for (int i = 0; i < count; i++)
        {
            if (Engine.TryGetTrackInfo(i, out var ti) && ti.Id == currentId)
            {
                currentIdx = i;
                break;
            }
        }
        int nextIdx = Math.Clamp(currentIdx + delta, 0, count - 1);
        if (Engine.TryGetTrackInfo(nextIdx, out var nextTi))
        {
            Timeline.SelectTrack(nextTi.Id);
            if (_deviceChain?.IsVisible == true)
                ShowDevices(nextTi.Id);
            string tName = Engine.GetTrackName(nextTi.Id);
            _vm!.StatusText = $"Selected Track: {tName}";
        }
    }

    public float TransformVelocity(float vel)
    {
        var s = _settings?.Current;
        if (s is null) return vel;
        if (!s.MidiVelocitySensitivity || s.MidiVelocityCurve == "Fixed" || (s.MidiLinkNoteOnVelocity != null && s.MidiLinkNoteOnVelocity.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase)))
        {
            return Math.Clamp(s.MidiFixedVelocityValue / 127.0f, 0.01f, 1.0f);
        }
        if (string.Equals(s.MidiLinkNoteOnVelocity, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return 1.0f;
        }
        return s.MidiVelocityCurve switch
        {
            "Soft" => MathF.Pow(Math.Clamp(vel, 0.01f, 1.0f), 0.7f),
            "Hard" => MathF.Pow(Math.Clamp(vel, 0.01f, 1.0f), 1.4f),
            "Compressed" => Math.Clamp(0.3f + 0.7f * vel, 0.01f, 1.0f),
            _ => Math.Clamp(vel, 0.01f, 1.0f),
        };
    }

    #endregion
}


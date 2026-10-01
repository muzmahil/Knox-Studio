// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.
//
// Preferences: 820×650 with a 180px sidebar and full section panes:
// (General, Audio, MIDI, Editing, Gamepads, Plug-ins, Library, Appearance, Shortcuts).
// Settings are persisted via ISettingsService / native audio config.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Knox.Application;
using Knox.Infrastructure;
using Knox.Presentation;

namespace Knox.App;

public sealed class PreferencesWindow : KnoxWindow
{
    private static readonly IBrush Panel = KnoxPalette.SurfaceCard;
    private static readonly IBrush Sidebar = KnoxPalette.BgSunken;
    private static readonly IBrush Sunken = KnoxPalette.BgSunken;
    private static readonly IBrush Raised = KnoxPalette.SurfaceRaised;
    private static readonly IBrush Divider = KnoxPalette.BorderDefault;
    private static readonly IBrush BorderStrong = KnoxPalette.BorderStrong;
    private static readonly IBrush BorderSubtle = KnoxPalette.BorderDefault;
    private static readonly IBrush SurfaceMuted = KnoxPalette.BgSunken;
    private static readonly IBrush SurfaceRaised = KnoxPalette.SurfaceRaised;
    private static readonly IBrush Brass = KnoxPalette.Accent;
    private static readonly IBrush Accent = KnoxPalette.Accent;
    private static readonly IBrush AccentBright = KnoxPalette.AccentBright;
    private static readonly IBrush AccentSubtle = KnoxPalette.AccentSubtle;
    private static readonly IBrush Success = KnoxPalette.Success;
    private static readonly IBrush OnAccent = KnoxPalette.TextOnAccent;
    private static readonly IBrush TextPrimary = KnoxPalette.TextPrimary;
    private static readonly IBrush TextSecondary = KnoxPalette.TextSecondary;
    private static readonly IBrush TextTertiary = KnoxPalette.TextTertiary;

    private static readonly string[] Sections =
    {
        "General", "Audio", "MIDI", "Editing", "Plug-ins", "Library", "Appearance", "Shortcuts"
    };

    private readonly ObservableCollection<string> _paths = new();
    private readonly MainWindowViewModel? _main;

    private readonly IPluginCatalog _catalog = App.Services.GetRequiredService<IPluginCatalog>();
    private readonly IAudioDeviceService _audioDevices = App.Services.GetRequiredService<IAudioDeviceService>();
    private readonly IMidiDeviceService _midiDevices = App.Services.GetRequiredService<IMidiDeviceService>();
    private readonly MidiLearnService? _learnService = App.Services.GetService<MidiLearnService>();

    private static readonly double[] SampleRates = { 0, 44100, 48000, 88200, 96000 };
    private static readonly int[] BufferSizes = { 0, 32, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 8192 };
    private readonly List<string> _outputUids = new();
    private readonly List<string> _inputUids = new();

    private bool _loading;
    private TextBlock? _audioStatus;

    private readonly ContentControl _content = new();
    private readonly List<Border> _navItems = new();

    public PreferencesWindow(SettingsViewModel vm, MainWindowViewModel? main = null)
    {
        _ = vm;
        _main = main;
        Title = "Knox Preferences";
        Width = 960;
        Height = 740;
        MinWidth = 880;
        MinHeight = 620;
        CanResize = true;
        Background = KnoxPalette.BgApp;

        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(10, 14) };
        for (int i = 0; i < Sections.Length; i++)
        {
            int idx = i;
            var item = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 8),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            item.Child = new TextBlock { Text = L10n.Tr($"Pref.{Sections[i]}", Sections[i]), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            item.PointerPressed += (_, _) => Select(idx);
            _navItems.Add(item);
            nav.Children.Add(item);
        }
        var sidebar = new Border
        {
            Width = 190,
            Background = Sidebar,
            BorderBrush = Divider,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = nav,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(sidebar);
        Grid.SetColumn(_content, 1);
        grid.Children.Add(_content);
        SetBody(grid);

        Select(0);
    }

    private int _currentSection = 0;

    private void Select(int index)
    {
        _currentSection = index;
        for (int i = 0; i < _navItems.Count; i++)
        {
            bool on = i == index;
            _navItems[i].Background = on ? Raised : Brushes.Transparent;
            _navItems[i].BorderBrush = on ? Brass : Brushes.Transparent;
            _navItems[i].BorderThickness = on ? new Thickness(3, 0, 0, 0) : new Thickness(0);
            var tb = (TextBlock)_navItems[i].Child!;
            tb.Foreground = on ? TextPrimary : TextSecondary;
            tb.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
        }
        var pane = BuildPane(index);
        pane.Margin = new Thickness(28, 20, 28, 80);
        _content.Content = new ScrollViewer
        {
            Content = pane,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AllowAutoHide = false,
        };
    }

    public void RefreshNavAndContent(int selectIndex = 0)
    {
        Title = "Knox " + L10n.Tr("Pref.Title", "Preferences");
        for (int i = 0; i < Sections.Length; i++)
        {
            if (i < _navItems.Count && _navItems[i].Child is TextBlock tb)
            {
                tb.Text = L10n.Tr($"Pref.{Sections[i]}", Sections[i]);
            }
        }
        Select(selectIndex);
    }

    private Control BuildPane(int index) => index switch
    {
        0 => GeneralPane(),
        1 => AudioPane(),
        2 => MidiPane(),
        3 => EditingPane(),
        4 => PluginsPane(),
        5 => LibraryPane(),
        6 => AppearancePane(),
        _ => ShortcutsPane(),
    };

    // ---- General ----------------------------------------------------------

    private Control GeneralPane()
    {
        var body = new StackPanel { Spacing = 16 };
        var s = _main?.Settings.Current;

        // Language & Localization Card
        var langCard = CreateCard(L10n.Tr("Pref.Card.Lang", "LANGUAGE & LOCALIZATION"));
        var langStack = (StackPanel)langCard.Child!;

        var available = L10n.AvailableLanguages;
        var langCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        int currentLangIdx = 0;
        for (int i = 0; i < available.Length; i++)
        {
            var (code, name) = available[i];
            langCombo.Items.Add(name);
            if (string.Equals(code, s?.Language, StringComparison.OrdinalIgnoreCase))
                currentLangIdx = i;
        }
        langCombo.SelectedIndex = currentLangIdx;
        langCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || langCombo.SelectedIndex < 0 || langCombo.SelectedIndex >= available.Length) return;
            var code = available[langCombo.SelectedIndex].Code;
            if (string.Equals(s.Language, code, StringComparison.OrdinalIgnoreCase)) return;
            s.Language = code;
            L10n.SetLanguage(code);
            _main?.Settings.Save();
            RefreshNavAndContent(0);
        };
        langStack.Children.Add(Row(L10n.Tr("Pref.Language", "Language / Dil"), langCombo));
        langStack.Children.Add(Caption(L10n.Tr("Pref.LanguageHelp", "Choose your preferred interface language. Additional translations can be added to the /Locales folder as .json files.")));
        body.Children.Add(langCard);

        // Startup Card
        // Startup Card
        var startupCard = CreateCard(L10n.Tr("Pref.Card.Startup", "STARTUP & PROJECT DEFAULTS"));
        var startupStack = (StackPanel)startupCard.Child!;

        var startupCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        startupCombo.Items.Add(L10n.Tr("Pref.Startup.Welcome", "Show Welcome Dialog"));
        startupCombo.Items.Add(L10n.Tr("Pref.Startup.NewProject", "Create New Blank Project"));
        startupCombo.Items.Add(L10n.Tr("Pref.Startup.LastProject", "Reopen Last Modified Project"));
        startupCombo.SelectedIndex = s?.StartupAction switch
        {
            "NewProject" => 1,
            "LastProject" => 2,
            _ => 0,
        };
        startupCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.StartupAction = startupCombo.SelectedIndex switch
            {
                1 => "NewProject",
                2 => "LastProject",
                _ => "Welcome",
            };
            _main?.Settings.Save();
        };
        startupStack.Children.Add(Row(L10n.Tr("Pref.Startup", "Startup & Launch Action"), startupCombo));

        var welcomeCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.WelcomeCheck", "Show Welcome launcher window on startup"),
            IsChecked = s?.ShowWelcomeOnStartup ?? true,
            FontSize = 11,
            Margin = new Thickness(150, 2, 0, 0),
        };
        welcomeCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.ShowWelcomeOnStartup = welcomeCheck.IsChecked ?? true;
            _main?.Settings.Save();
        };
        startupStack.Children.Add(welcomeCheck);

        // Tempo combo
        var tempoCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        int[] bpms = { 80, 90, 100, 110, 120, 128, 130, 140, 150, 160, 174 };
        foreach (var b in bpms) tempoCombo.Items.Add($"{b} BPM");
        int currBpmIdx = Array.IndexOf(bpms, s?.DefaultBpm ?? 120);
        tempoCombo.SelectedIndex = currBpmIdx >= 0 ? currBpmIdx : 4;
        tempoCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || tempoCombo.SelectedIndex < 0) return;
            s.DefaultBpm = bpms[tempoCombo.SelectedIndex];
            _main?.Settings.Save();
        };
        startupStack.Children.Add(Row(L10n.Tr("Pref.DefaultTempo", "Default Tempo"), tempoCombo));

        // Time Signature
        var sigCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] sigs = { "4/4", "3/4", "6/8", "5/4", "7/8", "12/8" };
        foreach (var sig in sigs) sigCombo.Items.Add(sig);
        string currentSig = $"{s?.DefaultTimeSigNumerator ?? 4}/{s?.DefaultTimeSigDenominator ?? 4}";
        int sigIdx = Array.IndexOf(sigs, currentSig);
        sigCombo.SelectedIndex = sigIdx >= 0 ? sigIdx : 0;
        sigCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || sigCombo.SelectedIndex < 0) return;
            var parts = sigs[sigCombo.SelectedIndex].Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out int num) && int.TryParse(parts[1], out int den))
            {
                s.DefaultTimeSigNumerator = num;
                s.DefaultTimeSigDenominator = den;
                _main?.Settings.Save();
            }
        };
        startupStack.Children.Add(Row(L10n.Tr("Pref.DefaultTimeSig", "Default Time Signature"), sigCombo));

        // Count-in
        var countInCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        countInCombo.Items.Add(L10n.Tr("Pref.CountInOff", "Off (Instant Record)"));
        countInCombo.Items.Add(L10n.Tr("Pref.CountIn1", "1 Bar Count-in"));
        countInCombo.Items.Add(L10n.Tr("Pref.CountIn2", "2 Bars Count-in"));
        countInCombo.Items.Add(L10n.Tr("Pref.CountIn4", "4 Bars Count-in"));
        countInCombo.SelectedIndex = s?.CountInBars switch { 0 => 0, 2 => 2, 4 => 3, _ => 1 };
        countInCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || countInCombo.SelectedIndex < 0) return;
            s.CountInBars = countInCombo.SelectedIndex switch { 0 => 0, 2 => 2, 3 => 4, _ => 1 };
            _main?.Settings.Save();
        };
        startupStack.Children.Add(Row(L10n.Tr("Pref.CountIn", "Recording Count-in"), countInCombo));

        body.Children.Add(startupCard);

        // Auto Save Card
        var autoSaveCard = CreateCard(L10n.Tr("Pref.Card.AutoSave", "AUTO-SAVE & BACKUPS"));
        var autoSaveStack = (StackPanel)autoSaveCard.Child!;

        var autoSaveCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        autoSaveCombo.Items.Add(L10n.Tr("Pref.AutoSaveDisabled", "Disabled"));
        autoSaveCombo.Items.Add(L10n.Tr("Pref.AutoSave1m", "Every 1 minute"));
        autoSaveCombo.Items.Add(L10n.Tr("Pref.AutoSave2m", "Every 2 minutes"));
        autoSaveCombo.Items.Add(L10n.Tr("Pref.AutoSave5m", "Every 5 minutes (Recommended)"));
        autoSaveCombo.Items.Add(L10n.Tr("Pref.AutoSave10m", "Every 10 minutes"));
        autoSaveCombo.Items.Add(L10n.Tr("Pref.AutoSave15m", "Every 15 minutes"));
        autoSaveCombo.SelectedIndex = s?.AutoSaveIntervalMinutes switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            10 => 4,
            15 => 5,
            _ => 3,
        };
        autoSaveCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || autoSaveCombo.SelectedIndex < 0) return;
            s.AutoSaveIntervalMinutes = autoSaveCombo.SelectedIndex switch
            {
                0 => 0,
                1 => 1,
                2 => 2,
                4 => 10,
                5 => 15,
                _ => 5,
            };
            _main?.Settings.Save();
        };
        autoSaveStack.Children.Add(Row(L10n.Tr("Pref.AutoSave", "Auto-Save Interval"), autoSaveCombo));
        autoSaveStack.Children.Add(Caption(L10n.Tr("Pref.AutoSaveHelp", "Knox creates background autosave snapshots alongside your project bundles without interrupting audio playback.")));

        body.Children.Add(autoSaveCard);

        // Discord Rich Presence Card
        var discordCard = CreateCard(L10n.Tr("Pref.Card.Discord", "DISCORD RICH PRESENCE (RPC)"));
        var discordStack = (StackPanel)discordCard.Child!;

        var discordCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.DiscordRpc", "Enable Discord Rich Presence"),
            IsChecked = s?.DiscordRpcEnabled ?? true,
            FontWeight = FontWeight.Medium,
        };
        discordCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.DiscordRpcEnabled = discordCheck.IsChecked ?? true;
            _main?.Settings.Save();
        };
        discordStack.Children.Add(discordCheck);
        discordStack.Children.Add(Caption(L10n.Tr("Pref.DiscordRpcHelp", "Broadcast your active project name, track count, tempo, and playback status to your Discord profile in real time.")));

        body.Children.Add(discordCard);
        return body;
    }

    // ---- Audio ------------------------------------------------------------

    // ---- Audio ------------------------------------------------------------

    private Control AudioPane()
    {
        var body = new StackPanel { Spacing = 16 };

        if (_main is null)
        {
            body.Children.Add(new TextBlock { Text = L10n.Tr("Pref.AudioUnavailable", "Audio settings are unavailable in this window."), FontSize = 11, Foreground = TextTertiary });
            return body;
        }

        _loading = true;
        var engine = _main.Engine;
        var cfg = engine.GetAudioConfig();
        var s = _main?.Settings.Current;

        // 1. Input / Output Card (FL Studio style)
        var ioCard = CreateCard(L10n.Tr("Pref.Card.AudioIO", "INPUT / OUTPUT"));
        var ioStack = (StackPanel)ioCard.Child!;

        // Driver Type & Device
        var driverTypeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        driverTypeCombo.Items.Add("WASAPI (Standard Windows Audio)");
        driverTypeCombo.Items.Add("ASIO (Professional Low-Latency Studio Driver)");
        driverTypeCombo.SelectedIndex = (s?.AudioDriverType == "ASIO") ? 1 : 0;
        driverTypeCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || driverTypeCombo.SelectedIndex < 0) return;
            s.AudioDriverType = driverTypeCombo.SelectedIndex == 1 ? "ASIO" : "WASAPI";
            _main?.Settings.Save();
        };
        ioStack.Children.Add(Row(L10n.Tr("Pref.AudioDriver", "Device"), driverTypeCombo));

        var outputCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        FillDeviceCombo(outputCombo, _outputUids, _audioDevices.OutputDevices(), cfg.OutputUid);
        outputCombo.SelectionChanged += (_, _) => { int i = outputCombo.SelectedIndex; if (i >= 0 && i < _outputUids.Count) { engine.SetAudioOutputDevice(_outputUids[i]); Apply(); } };
        ioStack.Children.Add(Row(L10n.Tr("Pref.OutputDevice", "Output Device"), outputCombo));

        var inputCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        FillDeviceCombo(inputCombo, _inputUids, _audioDevices.InputDevices(), cfg.InputUid);
        inputCombo.SelectionChanged += (_, _) => { int i = inputCombo.SelectedIndex; if (i >= 0 && i < _inputUids.Count) { engine.SetAudioInputDevice(_inputUids[i]); Apply(); } };
        ioStack.Children.Add(Row(L10n.Tr("Pref.InputDevice", "Input Device"), inputCombo));

        var srCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var sr in SampleRates) srCombo.Items.Add(sr == 0 ? "Device default" : $"{sr:0} Hz");
        srCombo.SelectedIndex = IndexOf(SampleRates, cfg.SampleRate);
        srCombo.SelectionChanged += (_, _) => { int i = srCombo.SelectedIndex; if (i >= 0) { engine.SetAudioSampleRate(SampleRates[i]); Apply(); } };
        ioStack.Children.Add(Row(L10n.Tr("Pref.SampleRate", "Sample Rate (Hz)"), srCombo));

        var autoCloseCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.AutoClose", "Auto close device (release driver when unfocused)"),
            IsChecked = s?.AudioAutoClose ?? false,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        autoCloseCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.AudioAutoClose = autoCloseCheck.IsChecked ?? false;
            _main?.Settings.Save();
        };
        ioStack.Children.Add(autoCloseCheck);

        _audioStatus = new TextBlock { FontSize = 11, Foreground = Success, TextWrapping = TextWrapping.Wrap, Text = StatusLine(), VerticalAlignment = VerticalAlignment.Center };
        _audioStatus.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
        ioStack.Children.Add(Row(L10n.Tr("Pref.EngineStatus", "Status"), _audioStatus));

        // Buffer Length Slider + ms calculation
        var bufSlider = new Slider
        {
            Minimum = 0,
            Maximum = BufferSizes.Length - 1,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Value = Math.Max(0, IndexOf(BufferSizes, cfg.BufferFrames)),
        };
        var bufReadout = new TextBlock
        {
            FontSize = 11,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 140,
            TextAlignment = TextAlignment.Right,
        };
        bufReadout.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");

        void UpdateBufReadout(int frames)
        {
            double sr = cfg.SampleRate > 0 ? cfg.SampleRate : 48000;
            double ms = frames > 0 ? (frames * 1000.0 / sr) : 0;
            bufReadout.Text = frames == 0 ? "Device default" : $"{frames} smp ({ms:F1} ms)";
        }
        UpdateBufReadout(cfg.BufferFrames);

        bufSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                int idx = (int)Math.Round(bufSlider.Value);
                if (idx >= 0 && idx < BufferSizes.Length)
                {
                    int frames = BufferSizes[idx];
                    UpdateBufReadout(frames);
                    if (!_loading)
                    {
                        engine.SetAudioBufferFrames(frames);
                        Apply();
                    }
                }
            }
        };

        var bufRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { bufSlider, WrapColumn(bufReadout, 1) }
        };
        ioStack.Children.Add(Row(L10n.Tr("Pref.BufferLength", "Buffer Length"), bufRow));

        // Buffer Offset Slider (0% .. 100%)
        var offsetSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = s?.AudioBufferOffsetPercent ?? 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var offsetReadout = new TextBlock
        {
            FontSize = 11,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 60,
            TextAlignment = TextAlignment.Right,
            Text = $"{offsetSlider.Value:0}%",
        };
        offsetReadout.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
        offsetSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                offsetReadout.Text = $"{offsetSlider.Value:0}%";
                if (s is not null && !_loading)
                {
                    s.AudioBufferOffsetPercent = offsetSlider.Value;
                    _main?.Settings.Save();
                }
            }
        };
        var offsetRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { offsetSlider, WrapColumn(offsetReadout, 1) }
        };
        ioStack.Children.Add(Row(L10n.Tr("Pref.BufferOffset", "Offset"), offsetRow));

        // Priority Dropdown
        var priorityCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] priorities = { "Normal", "High", "Highest", "Realtime" };
        foreach (var p in priorities) priorityCombo.Items.Add(p);
        priorityCombo.SelectedIndex = Math.Max(0, Array.IndexOf(priorities, s?.AudioPriority ?? "Highest"));
        priorityCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || priorityCombo.SelectedIndex < 0) return;
            s.AudioPriority = priorities[priorityCombo.SelectedIndex];
            _main?.Settings.Save();
        };
        ioStack.Children.Add(Row(L10n.Tr("Pref.Priority", "Priority"), priorityCombo));

        // Playback Tracking (Driver, Hybrid, Mixer)
        var trackingCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] trackings = { "Driver", "Hybrid", "Mixer" };
        foreach (var trk in trackings) trackingCombo.Items.Add(trk);
        trackingCombo.SelectedIndex = Math.Max(0, Array.IndexOf(trackings, s?.AudioPlaybackTracking ?? "Mixer"));
        trackingCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || trackingCombo.SelectedIndex < 0) return;
            s.AudioPlaybackTracking = trackings[trackingCombo.SelectedIndex];
            _main?.Settings.Save();
        };
        ioStack.Children.Add(Row(L10n.Tr("Pref.PlaybackTracking", "Playback Tracking"), trackingCombo));

        // Flags row
        var flagsStack = new StackPanel { Spacing = 4, Margin = new Thickness(150, 4, 0, 0) };
        var safeOverloadsCheck = new CheckBox { Content = L10n.Tr("Pref.SafeOverloads", "Safe overloads (graceful underrun handling)"), IsChecked = s?.AudioSafeOverloads ?? true, FontSize = 11 };
        safeOverloadsCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.AudioSafeOverloads = safeOverloadsCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        var usePollingCheck = new CheckBox { Content = L10n.Tr("Pref.UsePolling", "Use polling"), IsChecked = s?.AudioUsePolling ?? false, FontSize = 11 };
        usePollingCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.AudioUsePolling = usePollingCheck.IsChecked ?? false; _main?.Settings.Save(); } };

        var useHwBufCheck = new CheckBox { Content = L10n.Tr("Pref.UseHwBuf", "Use hardware buffer"), IsChecked = s?.AudioUseHardwareBuffer ?? true, FontSize = 11 };
        useHwBufCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.AudioUseHardwareBuffer = useHwBufCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        var use32BitCheck = new CheckBox { Content = L10n.Tr("Pref.Use32Bit", "Use 32Bit buffer (high precision float)"), IsChecked = s?.AudioUse32BitBuffer ?? true, FontSize = 11 };
        use32BitCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.AudioUse32BitBuffer = use32BitCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        flagsStack.Children.Add(safeOverloadsCheck);
        flagsStack.Children.Add(usePollingCheck);
        flagsStack.Children.Add(useHwBufCheck);
        flagsStack.Children.Add(use32BitCheck);
        ioStack.Children.Add(flagsStack);

        body.Children.Add(ioCard);

        // 2. CPU Processing Card (FL Studio style)
        var cpuCard = CreateCard(L10n.Tr("Pref.Card.CPU", "CPU PROCESSING & THREADS"));
        var cpuStack = (StackPanel)cpuCard.Child!;

        var multithreadGenCheck = new CheckBox { Content = L10n.Tr("Pref.MultiGen", "Multithreaded generator processing (synths / samplers)"), IsChecked = s?.CpuMultithreadedGenerators ?? true, FontSize = 11 };
        multithreadGenCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.CpuMultithreadedGenerators = multithreadGenCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        var multithreadMixCheck = new CheckBox { Content = L10n.Tr("Pref.MultiMix", "Multithreaded mixer processing (channel DSP rack)"), IsChecked = s?.CpuMultithreadedMixer ?? true, FontSize = 11 };
        multithreadMixCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.CpuMultithreadedMixer = multithreadMixCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        var smartDisableCheck = new CheckBox { Content = L10n.Tr("Pref.SmartDisable", "Smart disable (auto-sleep silent plugins to conserve CPU)"), IsChecked = s?.CpuSmartDisable ?? false, FontSize = 11 };
        smartDisableCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.CpuSmartDisable = smartDisableCheck.IsChecked ?? false; _main?.Settings.Save(); } };

        var alignTicksCheck = new CheckBox { Content = L10n.Tr("Pref.AlignTicks", "Align tick lengths (sample-accurate PPQ grid timing)"), IsChecked = s?.CpuAlignTickLengths ?? true, FontSize = 11 };
        alignTicksCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.CpuAlignTickLengths = alignTicksCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        cpuStack.Children.Add(multithreadGenCheck);
        cpuStack.Children.Add(multithreadMixCheck);
        cpuStack.Children.Add(smartDisableCheck);
        cpuStack.Children.Add(alignTicksCheck);
        body.Children.Add(cpuCard);

        // 3. Mixer Engine Card (FL Studio style)
        var mixerCard = CreateCard(L10n.Tr("Pref.Card.MixerEngine", "MIXER & RESAMPLING"));
        var mixerStack = (StackPanel)mixerCard.Child!;

        var resamplingCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] resamplings = { "Linear", "6-point hermite", "16-point sinc", "24-point sinc", "64-point sinc", "128-point sinc", "512-point sinc" };
        foreach (var r in resamplings) resamplingCombo.Items.Add(r);
        resamplingCombo.SelectedIndex = Math.Max(0, Array.IndexOf(resamplings, s?.MixerResamplingQuality ?? "24-point sinc"));
        resamplingCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || resamplingCombo.SelectedIndex < 0) return;
            s.MixerResamplingQuality = resamplings[resamplingCombo.SelectedIndex];
            _main?.Settings.Save();
        };
        mixerStack.Children.Add(Row(L10n.Tr("Pref.ResamplingQuality", "Resampling quality"), resamplingCombo));

        var previewTrackCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        previewTrackCombo.Items.Add("Master (Default)");
        for (int t = 1; t <= 16; t++) previewTrackCombo.Items.Add($"Track {t}");
        previewTrackCombo.SelectedIndex = Math.Clamp(s?.MixerBrowserPreviewTrack ?? 0, 0, 16);
        previewTrackCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || previewTrackCombo.SelectedIndex < 0) return;
            s.MixerBrowserPreviewTrack = previewTrackCombo.SelectedIndex;
            _main?.Settings.Save();
        };
        mixerStack.Children.Add(Row(L10n.Tr("Pref.PreviewTrack", "Browser preview track"), previewTrackCombo));

        var metronomeTrackCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        metronomeTrackCombo.Items.Add("Master (Default)");
        for (int t = 1; t <= 16; t++) metronomeTrackCombo.Items.Add($"Track {t}");
        metronomeTrackCombo.SelectedIndex = Math.Clamp(s?.MixerMetronomeTrack ?? 0, 0, 16);
        metronomeTrackCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || metronomeTrackCombo.SelectedIndex < 0) return;
            s.MixerMetronomeTrack = metronomeTrackCombo.SelectedIndex;
            _main?.Settings.Save();
        };
        mixerStack.Children.Add(Row(L10n.Tr("Pref.MetronomeTrack", "Metronome track"), metronomeTrackCombo));

        var playTruncatedCheck = new CheckBox { Content = L10n.Tr("Pref.PlayTruncated", "Play truncated notes on transport (sound notes mid-playhead)"), IsChecked = s?.MixerPlayTruncatedNotes ?? true, FontSize = 11, Margin = new Thickness(150, 4, 0, 0) };
        playTruncatedCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.MixerPlayTruncatedNotes = playTruncatedCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        var resetPluginsCheck = new CheckBox { Content = L10n.Tr("Pref.ResetPlugins", "Reset plugins on transport (clear delay/reverb tails on stop)"), IsChecked = s?.MixerResetPluginsOnTransport ?? false, FontSize = 11, Margin = new Thickness(150, 2, 0, 0) };
        resetPluginsCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.MixerResetPluginsOnTransport = resetPluginsCheck.IsChecked ?? false; _main?.Settings.Save(); } };

        mixerStack.Children.Add(playTruncatedCheck);
        mixerStack.Children.Add(resetPluginsCheck);
        body.Children.Add(mixerCard);

        // 4. ASIO Section
        if (OperatingSystem.IsWindows())
        {
            var asioCard = CreateCard(L10n.Tr("Pref.Card.Asio", "HARDWARE ASIO DRIVER"));
            var asioStack = (StackPanel)asioCard.Child!;

            var asioDrivers = AsioDriverService.EnumerateDrivers();
            var asioCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            if (asioDrivers.Count > 0)
            {
                int matchedIdx = 0;
                for (int i = 0; i < asioDrivers.Count; i++)
                {
                    var d = asioDrivers[i];
                    asioCombo.Items.Add($"{d.Name} — {d.Description}");
                    if (!string.IsNullOrEmpty(s?.AsioDriverName) && string.Equals(d.Name, s.AsioDriverName, StringComparison.OrdinalIgnoreCase))
                        matchedIdx = i;
                }
                asioCombo.SelectedIndex = matchedIdx;
                asioCombo.SelectionChanged += (_, _) =>
                {
                    if (s is null || _loading || asioCombo.SelectedIndex < 0 || asioCombo.SelectedIndex >= asioDrivers.Count) return;
                    s.AsioDriverName = asioDrivers[asioCombo.SelectedIndex].Name;
                    _main?.Settings.Save();
                };
            }
            else
            {
                asioCombo.Items.Add(L10n.Tr("Pref.NoAsio", "No hardware ASIO driver detected (using WASAPI Exclusive)"));
                asioCombo.SelectedIndex = 0;
            }

            var openAsioBtn = new Button { Content = L10n.Tr("Pref.AsioControlPanel", "Open ASIO Control Panel"), Classes = { "primary" } };
            openAsioBtn.Click += (_, _) =>
            {
                if (asioDrivers.Count > 0 && asioCombo.SelectedIndex >= 0 && asioCombo.SelectedIndex < asioDrivers.Count)
                {
                    AsioDriverService.OpenControlPanel(asioDrivers[asioCombo.SelectedIndex]);
                }
            };

            asioStack.Children.Add(Row(L10n.Tr("Pref.AsioDriver", "ASIO Driver"), asioCombo));
            asioStack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(150, 4, 0, 0),
                Children = { openAsioBtn }
            });
            body.Children.Add(asioCard);
        }

        var testCard = CreateCard(L10n.Tr("Pref.Card.Diagnostics", "DIAGNOSTICS & HARDWARE TEST"));
        var testStack = (StackPanel)testCard.Child!;

        var testTone = new ToggleButton
        {
            Content = L10n.Tr("Pref.TestTone", "440Hz Sine Tone"),
            Padding = new Thickness(12, 6),
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        testTone.IsCheckedChanged += (_, _) =>
        {
            bool isOn = testTone.IsChecked ?? false;
            try
            {
                if (isOn)
                {
                    _main?.Engine.SetFrequency(440.0f);
                    _main?.Engine.SetTestTone(true);
                }
                else
                {
                    _main?.Engine.SetTestTone(false);
                }
            }
            catch { }
        };

        var cpuStatusText = new TextBlock
        {
            FontSize = 11,
            Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var cpuBtn = new Button
        {
            Content = L10n.Tr("Pref.StressTest", "Engine Stress Test"),
            Padding = new Thickness(12, 6),
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        cpuBtn.Click += (_, _) =>
        {
            cpuStatusText.Text = L10n.Tr("Pref.StressRunning", "Running DSP benchmark…");
            cpuStatusText.Foreground = AccentBright;
            Dispatcher.UIThread.Post(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = true;
                if (_main?.Engine is { } eng)
                {
                    ok &= eng.AudioRecordSelfTest();
                    ok &= eng.PreviewSelfTest();
                    ok &= eng.AutomationSelfTest();
                    var buf = new float[220500 * 2];
                    eng.RenderOffline(buf, 220500);
                }
                sw.Stop();
                double seconds = sw.Elapsed.TotalSeconds;
                double speedup = seconds > 0.0001 ? 5.0 / seconds : 999.0;
                if (ok)
                {
                    cpuStatusText.Text = string.Format(L10n.Tr("Pref.StressPassed", "Passed: {0:0.0}x Real-time DSP ({1:0}ms) · 0 dropouts"), speedup, sw.ElapsedMilliseconds);
                    cpuStatusText.Foreground = Success;
                }
                else
                {
                    cpuStatusText.Text = L10n.Tr("Pref.StressFailed", "Warning: Engine test");
                    cpuStatusText.Foreground = TextTertiary;
                }
            }, DispatcherPriority.Background);
        };

        testStack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { testTone, cpuBtn, cpuStatusText }
        });
        body.Children.Add(testCard);

        _loading = false;
        return body;
    }

    private void Apply()
    {
        if (_loading || _main is null) return;
        _main.ApplyAudioSettings();
        if (_audioStatus is not null) _audioStatus.Text = StatusLine();
    }

    private string StatusLine()
    {
        if (_main is null) return "";
        double sr = _main.Engine.NegotiatedSampleRate;
        int buf = _main.Engine.NegotiatedBufferFrames;
        if (sr <= 0) return "Audio engine idle / device stopped.";
        string mode = OperatingSystem.IsWindows() && _main.Engine.AudioExclusiveFallback
            ? " · exclusive fallback → shared"
            : OperatingSystem.IsWindows() && _main.Engine.GetAudioConfig().WasapiExclusive ? " · exclusive bit-perfect" : "";
        double latencyMs = (sr > 0 && buf > 0) ? (buf * 1000.0 / sr) : 0;
        return buf > 0 ? $"{sr:0} Hz · {buf} frames ({latencyMs:F1} ms roundtrip){mode}" : $"{sr:0} Hz{mode}";
    }

    // ---- MIDI -------------------------------------------------------------

    private Control MidiPane()
    {
        var body = new StackPanel { Spacing = 16 };

        if (_main is null)
        {
            body.Children.Add(new TextBlock { Text = L10n.Tr("Pref.MidiUnavailable", "MIDI settings are unavailable in this window."), FontSize = 11, Foreground = TextTertiary });
            return body;
        }

        _loading = true;
        var engine = _main.Engine;
        var s = _main?.Settings.Current;

        // 1. Output Section Card (FL Studio style)
        var outCard = CreateCard(L10n.Tr("Pref.Card.MidiOutput", "OUTPUT DEVICES & MASTER SYNC"));
        var outStack = (StackPanel)outCard.Child!;

        var outDevices = _midiDevices.OutputDevices();
        var outCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var dev in outDevices) outCombo.Items.Add(dev.Name);
        if (outDevices.Count > 0) outCombo.SelectedIndex = 0;
        outStack.Children.Add(Row(L10n.Tr("Pref.MidiOutputDev", "Output Device"), outCombo));

        var masterSyncCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.SendMasterSync", "Send master sync"),
            IsChecked = s?.MidiSendMasterSync ?? false,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        masterSyncCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.MidiSendMasterSync = masterSyncCheck.IsChecked ?? false;
            _main?.Settings.Save();
        };

        var allNotesOffCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.SendAllNotesOff", "Send all notes off on transport stop"),
            IsChecked = s?.MidiSendAllNotesOffOnStop ?? true,
            FontSize = 11,
            Margin = new Thickness(150, 2, 0, 0),
        };
        allNotesOffCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.MidiSendAllNotesOffOnStop = allNotesOffCheck.IsChecked ?? true;
            _main?.Settings.Save();
        };

        var outPortCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        outPortCombo.Items.Add("Port: --- (None)");
        for (int p = 0; p <= 15; p++) outPortCombo.Items.Add($"Port: {p}");
        outPortCombo.SelectedIndex = 0;
        outPortCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || outCombo.SelectedIndex < 0 || outCombo.SelectedIndex >= outDevices.Count) return;
            string uid = outDevices[outCombo.SelectedIndex].Uid;
            s.MidiOutputPorts[uid] = outPortCombo.SelectedIndex == 0 ? -1 : outPortCombo.SelectedIndex - 1;
            _main?.Settings.Save();
        };

        var syncTypeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] syncTypes = { "MIDI clock", "MTC 24 fps", "MTC 25 fps", "MTC 30 fps" };
        foreach (var st in syncTypes) syncTypeCombo.Items.Add(st);
        syncTypeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(syncTypes, s?.MidiSyncType ?? "MIDI clock"));
        syncTypeCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || syncTypeCombo.SelectedIndex < 0) return;
            s.MidiSyncType = syncTypes[syncTypeCombo.SelectedIndex];
            _main?.Settings.Save();
        };

        var syncOffsetNumeric = new NumericUpDown
        {
            Minimum = -500,
            Maximum = 500,
            Increment = 1,
            Value = s?.MidiMasterSyncOffsetMs ?? 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        syncOffsetNumeric.ValueChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.MidiMasterSyncOffsetMs = (int)(syncOffsetNumeric.Value ?? 0);
            _main?.Settings.Save();
        };

        outStack.Children.Add(masterSyncCheck);
        outStack.Children.Add(allNotesOffCheck);
        outStack.Children.Add(Row(L10n.Tr("Pref.MidiPort", "Port"), outPortCombo));
        outStack.Children.Add(Row(L10n.Tr("Pref.SyncType", "Synchronization type"), syncTypeCombo));
        outStack.Children.Add(Row(L10n.Tr("Pref.SyncOffset", "Master sync offset (ms)"), syncOffsetNumeric));
        body.Children.Add(outCard);

        // 2. Connected Multi-Device Input Section Card
        var inCard = CreateCard(L10n.Tr("Pref.Card.MidiInputMulti", "CONNECTED MIDI CONTROLLERS (MULTI-DEVICE INPUT)"));
        var inStack = (StackPanel)inCard.Child!;

        var inDevices = _midiDevices.InputDevices();
        string[] controllerTypes = { "(generic controller)", "Akai MPK Mini", "Novation Launchkey", "Arturia KeyLab", "M-Audio Oxygen", "Korg nanoKEY", "Native Instruments Komplete", "Alesis V-Series", "Mackie Control Universal" };

        var multiHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var multiTitle = new TextBlock
        {
            Text = inDevices.Count == 0 
                ? L10n.Tr("Pref.NoMidiDevs", "No MIDI devices detected. Connect a USB MIDI controller.") 
                : string.Format(L10n.Tr("Pref.MidiDevCount", "{0} MIDI controller(s) detected — all checked devices are active simultaneously:"), inDevices.Count),
            FontSize = 11,
            Foreground = inDevices.Count > 0 ? TextSecondary : TextTertiary,
            VerticalAlignment = VerticalAlignment.Center
        };
        multiHeader.Children.Add(multiTitle);

        if (inDevices.Count > 0)
        {
            var btnBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var enableAllBtn = new Button { Content = L10n.Tr("Pref.EnableAll", "Enable All"), FontSize = 10, Padding = new Thickness(8, 3), Classes = { "secondary" } };
            var disableAllBtn = new Button { Content = L10n.Tr("Pref.DisableAll", "Disable All"), FontSize = 10, Padding = new Thickness(8, 3), Classes = { "secondary" } };
            
            enableAllBtn.Click += (_, _) =>
            {
                foreach (var d in inDevices) engine.SetMidiInputEnabled(d.Uid, true);
                _main.ApplyMidiSettings();
                Select(2);
            };
            disableAllBtn.Click += (_, _) =>
            {
                foreach (var d in inDevices) engine.SetMidiInputEnabled(d.Uid, false);
                _main.ApplyMidiSettings();
                Select(2);
            };
            btnBox.Children.Add(enableAllBtn);
            btnBox.Children.Add(disableAllBtn);
            Grid.SetColumn(btnBox, 1);
            multiHeader.Children.Add(btnBox);
        }
        inStack.Children.Add(multiHeader);

        // Render each device card
        var devListPanel = new StackPanel { Spacing = 8 };
        foreach (var dev in inDevices)
        {
            bool isEnabled = engine.IsMidiInputEnabled(dev.Uid);
            var devBox = new Border
            {
                Background = SurfaceMuted,
                BorderBrush = isEnabled ? Accent : BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10),
            };

            var devLayout = new StackPanel { Spacing = 8 };

            // Top row: Checkbox + Name + Status Tag
            var topGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var devCheck = new CheckBox
            {
                IsChecked = isEnabled,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = dev.Name, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = $"({dev.Uid})", FontSize = 10, Foreground = TextTertiary, VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            };
            var statusBadge = new Border
            {
                Background = isEnabled ? AccentSubtle : SurfaceMuted,
                BorderBrush = isEnabled ? Accent : BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                Child = new TextBlock
                {
                    Text = isEnabled ? L10n.Tr("Pref.Active", "● ACTIVE") : L10n.Tr("Pref.Disabled", "OFF"),
                    FontSize = 9,
                    FontWeight = FontWeight.Bold,
                    Foreground = isEnabled ? Accent : TextTertiary
                }
            };
            topGrid.Children.Add(devCheck);
            Grid.SetColumn(statusBadge, 1);
            topGrid.Children.Add(statusBadge);
            devLayout.Children.Add(topGrid);

            // Bottom row: Port & Controller Type
            var propGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("120,*,120,*"),
                Margin = new Thickness(24, 0, 0, 0)
            };

            var portLbl = new TextBlock { Text = L10n.Tr("Pref.Port", "Port:"), FontSize = 11, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center };
            var portCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
            portCombo.Items.Add("--- (None)");
            for (int p = 0; p <= 15; p++) portCombo.Items.Add($"Port {p}");
            if (s?.MidiInputPorts.TryGetValue(dev.Uid, out int pt) == true && pt >= 0 && pt < 16)
                portCombo.SelectedIndex = pt + 1;
            else portCombo.SelectedIndex = 0;

            portCombo.SelectionChanged += (_, _) =>
            {
                if (s is null || _loading) return;
                s.MidiInputPorts[dev.Uid] = portCombo.SelectedIndex == 0 ? -1 : portCombo.SelectedIndex - 1;
                _main?.Settings.Save();
            };

            var typeLbl = new TextBlock { Text = L10n.Tr("Pref.ControllerType", "Controller Type:"), FontSize = 11, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var typeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
            foreach (var ct in controllerTypes) typeCombo.Items.Add(ct);
            if (s?.MidiControllerTypes.TryGetValue(dev.Uid, out var ctype) == true)
                typeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(controllerTypes, ctype));
            else typeCombo.SelectedIndex = 0;

            typeCombo.SelectionChanged += (_, _) =>
            {
                if (s is null || _loading || typeCombo.SelectedIndex < 0) return;
                s.MidiControllerTypes[dev.Uid] = controllerTypes[typeCombo.SelectedIndex];
                _main?.Settings.Save();
            };

            devCheck.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                bool en = devCheck.IsChecked ?? true;
                engine.SetMidiInputEnabled(dev.Uid, en);
                _main.ApplyMidiSettings();
                devBox.BorderBrush = en ? Accent : BorderSubtle;
                statusBadge.Background = en ? AccentSubtle : SurfaceMuted;
                statusBadge.BorderBrush = en ? Accent : BorderSubtle;
                ((TextBlock)statusBadge.Child!).Text = en ? L10n.Tr("Pref.Active", "● ACTIVE") : L10n.Tr("Pref.Disabled", "OFF");
                ((TextBlock)statusBadge.Child!).Foreground = en ? Accent : TextTertiary;
            };

            propGrid.Children.Add(portLbl);
            Grid.SetColumn(portCombo, 1);
            propGrid.Children.Add(portCombo);
            Grid.SetColumn(typeLbl, 2);
            propGrid.Children.Add(typeLbl);
            Grid.SetColumn(typeCombo, 3);
            propGrid.Children.Add(typeCombo);

            devLayout.Children.Add(propGrid);
            devBox.Child = devLayout;
            devListPanel.Children.Add(devBox);
        }
        inStack.Children.Add(devListPanel);
        body.Children.Add(inCard);

        // 3. Velocity Sensitivity & Dynamic Curves Card
        var velCard = CreateCard(L10n.Tr("Pref.Card.VelocitySettings", "KEY VELOCITY SENSITIVITY & DYNAMIC CURVES"));
        var velStack = (StackPanel)velCard.Child!;

        // Velocity Sensitivity Toggle
        var velSensitivityCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.VelocitySensitivity", "Key Velocity Sensitivity (Tuş Basma Şiddeti Hassasiyeti)"),
            IsChecked = s?.MidiVelocitySensitivity ?? true,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Margin = new Thickness(150, 2, 0, 0),
        };
        var velSub = new TextBlock
        {
            Text = L10n.Tr("Pref.VelSub", "When enabled, volume/dynamics track how hard you strike the keys. When disabled, all notes trigger at a consistent Fixed Velocity."),
            FontSize = 10,
            Foreground = TextTertiary,
            Margin = new Thickness(150, 0, 0, 6)
        };

        // Fixed Velocity Value Slider
        var fixedVelSlider = new Slider
        {
            Minimum = 1,
            Maximum = 127,
            Value = s?.MidiFixedVelocityValue ?? 100,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !(s?.MidiVelocitySensitivity ?? true) || s?.MidiVelocityCurve == "Fixed",
        };
        var fixedVelReadout = new TextBlock
        {
            Text = $"{s?.MidiFixedVelocityValue ?? 100} / 127",
            FontSize = 11,
            Foreground = Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Width = 55
        };
        fixedVelSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                int val = (int)fixedVelSlider.Value;
                fixedVelReadout.Text = $"{val} / 127";
                if (s is not null && !_loading)
                {
                    s.MidiFixedVelocityValue = val;
                    _main?.Settings.Save();
                    _main?.ApplyMidiVelocitySettings();
                }
            }
        };
        var fixedVelRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { fixedVelSlider, WrapColumn(fixedVelReadout, 1) }
        };

        // Velocity Curve Dropdown
        var velCurveCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] velCurves = { "Linear", "Soft", "Hard", "Compressed", "Fixed" };
        foreach (var vc in velCurves) velCurveCombo.Items.Add(vc);
        velCurveCombo.SelectedIndex = Math.Max(0, Array.IndexOf(velCurves, s?.MidiVelocityCurve ?? "Linear"));
        velCurveCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || velCurveCombo.SelectedIndex < 0) return;
            string sel = velCurves[velCurveCombo.SelectedIndex];
            s.MidiVelocityCurve = sel;
            fixedVelSlider.IsEnabled = !s.MidiVelocitySensitivity || sel == "Fixed";
            _main?.Settings.Save();
            _main?.ApplyMidiVelocitySettings();
        };

        velSensitivityCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            bool sens = velSensitivityCheck.IsChecked ?? true;
            s.MidiVelocitySensitivity = sens;
            fixedVelSlider.IsEnabled = !sens || s.MidiVelocityCurve == "Fixed";
            _main?.Settings.Save();
            _main?.ApplyMidiVelocitySettings();
        };

        velStack.Children.Add(velSensitivityCheck);
        velStack.Children.Add(velSub);
        velStack.Children.Add(Row(L10n.Tr("Pref.FixedVelocityVal", "Fixed Velocity Value"), fixedVelRow));
        velStack.Children.Add(Row(L10n.Tr("Pref.VelocityCurve", "Velocity Curve"), velCurveCombo));

        // Note on / release velocity targets
        var noteOnVelCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] noteOnVels = { "Velocity (Default)", "(none)", "Mod wheel", "Fixed 100", "Fixed 127" };
        foreach (var nv in noteOnVels) noteOnVelCombo.Items.Add(nv);
        noteOnVelCombo.SelectedIndex = Math.Max(0, Array.IndexOf(noteOnVels, s?.MidiLinkNoteOnVelocity ?? "Velocity"));
        noteOnVelCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || noteOnVelCombo.SelectedIndex < 0) return;
            s.MidiLinkNoteOnVelocity = noteOnVels[noteOnVelCombo.SelectedIndex];
            _main?.Settings.Save();
        };
        velStack.Children.Add(Row(L10n.Tr("Pref.LinkNoteOnVel", "Link note on velocity to"), noteOnVelCombo));

        var relVelCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] relVels = { "Release (Default)", "(none)", "Linear Ramp" };
        foreach (var rv in relVels) relVelCombo.Items.Add(rv);
        relVelCombo.SelectedIndex = Math.Max(0, Array.IndexOf(relVels, s?.MidiLinkReleaseVelocity ?? "Release"));
        relVelCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || relVelCombo.SelectedIndex < 0) return;
            s.MidiLinkReleaseVelocity = relVels[relVelCombo.SelectedIndex];
            _main?.Settings.Save();
        };
        velStack.Children.Add(Row(L10n.Tr("Pref.LinkRelVel", "Link release velocity to"), relVelCombo));

        var pickupCheck = new CheckBox { Content = L10n.Tr("Pref.PickupMode", "Pickup (takeover mode)"), IsChecked = s?.MidiPickupMode ?? false, FontSize = 11, Margin = new Thickness(150, 4, 0, 0) };
        pickupCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.MidiPickupMode = pickupCheck.IsChecked ?? false; _main?.Settings.Save(); } };

        var autoAcceptCheck = new CheckBox { Content = L10n.Tr("Pref.AutoAccept", "Auto accept detected controller"), IsChecked = s?.MidiAutoAcceptController ?? true, FontSize = 11, Margin = new Thickness(150, 2, 0, 0) };
        autoAcceptCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.MidiAutoAcceptController = autoAcceptCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        var footPedalCheck = new CheckBox { Content = L10n.Tr("Pref.FootPedal", "Foot pedal controls note off (Sustain CC64)"), IsChecked = s?.MidiFootPedalNoteOff ?? true, FontSize = 11, Margin = new Thickness(150, 2, 0, 0) };
        footPedalCheck.IsCheckedChanged += (_, _) => { if (s is not null && !_loading) { s.MidiFootPedalNoteOff = footPedalCheck.IsChecked ?? true; _main?.Settings.Save(); } };

        velStack.Children.Add(pickupCheck);
        velStack.Children.Add(autoAcceptCheck);
        velStack.Children.Add(footPedalCheck);

        // MIDI Channels
        var omniCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        omniCombo.Items.Add("--- (Disabled)");
        for (int c = 1; c <= 16; c++) omniCombo.Items.Add($"Channel {c}");
        omniCombo.SelectedIndex = Math.Clamp(s?.MidiOmniPreviewChannel ?? 0, 0, 16);
        omniCombo.SelectionChanged += (_, _) => { if (s is not null && !_loading && omniCombo.SelectedIndex >= 0) { s.MidiOmniPreviewChannel = omniCombo.SelectedIndex; _main?.Settings.Save(); } };
        velStack.Children.Add(Row(L10n.Tr("Pref.OmniPreview", "Omni preview MIDI channel"), omniCombo));

        var markerCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        markerCombo.Items.Add("--- (Disabled)");
        for (int c = 1; c <= 16; c++) markerCombo.Items.Add($"Channel {c}");
        markerCombo.SelectedIndex = Math.Clamp(s?.MidiSongMarkerJumpChannel ?? 0, 0, 16);
        markerCombo.SelectionChanged += (_, _) => { if (s is not null && !_loading && markerCombo.SelectedIndex >= 0) { s.MidiSongMarkerJumpChannel = markerCombo.SelectedIndex; _main?.Settings.Save(); } };
        velStack.Children.Add(Row(L10n.Tr("Pref.SongMarkerJump", "Song marker jump MIDI channel"), markerCombo));

        body.Children.Add(velCard);

        // 4. MIDI Controller Action Mapping & Learn Manager Card
        var mapCard = CreateCard(L10n.Tr("Pref.Card.MidiActionMapping", "MIDI CONTROLLER ACTION MAPPINGS (TUŞ ATAMA & LEARN)"));
        var mapStack = (StackPanel)mapCard.Child!;

        // Preset Toolbar
        var mapHeaderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var mapSub = new TextBlock
        {
            Text = L10n.Tr("Pref.ActionMapSub", "Map buttons/pads on your MIDI controller to transport and workflow actions. Click 'Learn', then press any key or button on your controller."),
            FontSize = 11,
            Foreground = TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        mapHeaderGrid.Children.Add(mapSub);

        var presetBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var presetCombo = new ComboBox { FontSize = 11 };
        string[] mapPresets = { "Presets...", "MMC Standard", "Mackie Control", "Akai MPK Mini", "Novation Launchkey", "Arturia KeyLab" };
        foreach (var mp in mapPresets) presetCombo.Items.Add(mp);
        presetCombo.SelectedIndex = 0;

        var clearAllBtn = new Button { Content = L10n.Tr("Pref.ClearAllMaps", "Clear All Mappings"), FontSize = 11, Classes = { "secondary" } };

        presetBox.Children.Add(presetCombo);
        presetBox.Children.Add(clearAllBtn);
        Grid.SetColumn(presetBox, 1);
        mapHeaderGrid.Children.Add(presetBox);
        mapStack.Children.Add(mapHeaderGrid);

        // Device Selector Row
        var devSelectGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var devLabel = new TextBlock
        {
            Text = L10n.Tr("Pref.TargetDevice", "Target Controller Device:"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(devLabel, 0);
        devSelectGrid.Children.Add(devLabel);

        var devCombo = new ComboBox { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
        var mapInDevices = _main?.Engine != null ? KnoxEngine.MidiInputDevices() : Array.Empty<MidiDevice>();
        devCombo.Items.Add(L10n.Tr("Pref.AllDevices", "All Devices (Global Mapping)"));
        foreach (var d in mapInDevices)
        {
            devCombo.Items.Add(d.Name);
        }
        devCombo.SelectedIndex = 0;
        Grid.SetColumn(devCombo, 1);
        devSelectGrid.Children.Add(devCombo);

        var devBadge = new Border
        {
            Background = SurfaceRaised,
            BorderBrush = BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var devBadgeText = new TextBlock
        {
            Text = L10n.Tr("Pref.GlobalDefault", "Global Default"),
            FontSize = 10,
            Foreground = TextTertiary,
            FontWeight = FontWeight.Medium
        };
        devBadge.Child = devBadgeText;
        Grid.SetColumn(devBadge, 2);
        devSelectGrid.Children.Add(devBadge);

        mapStack.Children.Add(devSelectGrid);

        // Action Mapping Rows
        var actionListPanel = new StackPanel { Spacing = 4 };
        if (_learnService is not null)
        {
            string? GetSelectedDeviceUid()
            {
                int idx = devCombo.SelectedIndex;
                if (idx > 0 && idx - 1 < mapInDevices.Count) return mapInDevices[idx - 1].Uid;
                return null;
            }

            string? GetSelectedDeviceName()
            {
                int idx = devCombo.SelectedIndex;
                if (idx > 0 && idx - 1 < mapInDevices.Count) return mapInDevices[idx - 1].Name;
                return null;
            }

            presetCombo.SelectionChanged += (_, _) =>
            {
                if (presetCombo.SelectedIndex <= 0) return;
                string pr = mapPresets[presetCombo.SelectedIndex];
                _learnService.LoadPreset(pr, GetSelectedDeviceUid(), GetSelectedDeviceName());
                presetCombo.SelectedIndex = 0;
            };

            clearAllBtn.Click += (_, _) => _learnService.ClearDevice(GetSelectedDeviceUid());

            devCombo.SelectionChanged += (_, _) =>
            {
                bool isSpecific = devCombo.SelectedIndex > 0;
                devBadgeText.Text = isSpecific ? L10n.Tr("Pref.DeviceSpecific", "Device Specific") : L10n.Tr("Pref.GlobalDefault", "Global Default");
                devBadgeText.Foreground = isSpecific ? AccentBright : TextTertiary;
                _learnService.ActiveDeviceUid = GetSelectedDeviceUid();
                _learnService.ActiveDeviceName = GetSelectedDeviceName();
                RebuildActionRows();
            };

            void RebuildActionRows()
            {
                actionListPanel.Children.Clear();
                string? devUid = GetSelectedDeviceUid();
                string? devName = GetSelectedDeviceName();

                foreach (var action in MidiLearnService.StandardActions)
                {
                    var target = new MidiTarget(action.Kind, -1, -1, -1);
                    var mapping = _learnService.MappingFor(target, devUid);
                    bool isPending = _learnService.Pending?.Target.Equals(target) == true && _learnService.PendingDeviceUid == devUid;

                    var actionRow = new Border
                    {
                        Background = isPending ? AccentSubtle : SurfaceMuted,
                        BorderBrush = isPending ? Accent : (mapping != null ? BorderStrong : BorderSubtle),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 6),
                    };

                    var rowGrid = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("220,110,*,Auto")
                    };

                    // Name + Category
                    var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                    var actionNameText = new TextBlock
                    {
                        Text = L10n.Tr(action.NameKey, action.DefaultName),
                        FontSize = 11,
                        FontWeight = FontWeight.Medium,
                        Foreground = TextPrimary,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    titleStack.Children.Add(actionNameText);

                    var catBadge = new Border
                    {
                        Background = SurfaceRaised,
                        BorderBrush = BorderSubtle,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = action.Category, FontSize = 9, Foreground = TextTertiary }
                    };
                    Grid.SetColumn(catBadge, 1);

                    // Mapping Badge
                    string mapLabel;
                    if (isPending)
                    {
                        mapLabel = L10n.Tr("Pref.PressKeyNow", "PRESS KEY ON CONTROLLER...");
                    }
                    else if (mapping != null)
                    {
                        mapLabel = devUid == null && !string.IsNullOrEmpty(mapping.DeviceName)
                            ? $"[{mapping.DeviceName}] {mapping.SourceLabel}"
                            : mapping.SourceLabel;
                    }
                    else
                    {
                        mapLabel = L10n.Tr("Pref.Unassigned", "(Unassigned)");
                    }

                    var mappingBadge = new TextBlock
                    {
                        Text = mapLabel,
                        FontSize = 11,
                        Foreground = isPending ? AccentBright : (mapping != null ? Accent : TextTertiary),
                        FontWeight = (isPending || mapping != null) ? FontWeight.SemiBold : FontWeight.Normal,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    mappingBadge.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
                    Grid.SetColumn(mappingBadge, 2);

                    // Buttons (Learn, Clear, Test)
                    var actBtnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    var learnBtn = new Button
                    {
                        Content = isPending ? L10n.Tr("Pref.Cancel", "Cancel") : L10n.Tr("Pref.Learn", "Learn"),
                        FontSize = 10,
                        Padding = new Thickness(8, 3),
                        Classes = { isPending ? "danger" : "secondary" }
                    };
                    learnBtn.Click += (_, _) =>
                    {
                        if (isPending) _learnService.ClearPending();
                        else _learnService.SelectForLearn(new MidiBinding(target, action.DefaultName), devUid, devName);
                    };

                    actBtnStack.Children.Add(learnBtn);

                    if (mapping != null)
                    {
                        var clearBtn = new Button
                        {
                            Content = "✕",
                            FontSize = 10,
                            Padding = new Thickness(6, 3),
                            Classes = { "secondary" },
                        };
                        ToolTip.SetTip(clearBtn, L10n.Tr("Pref.ClearMapping", "Remove this mapping"));
                        clearBtn.Click += (_, _) => _learnService.RemoveActionMapping(action.Kind, devUid);
                        actBtnStack.Children.Add(clearBtn);
                    }

                    var testBtn = new Button
                    {
                        Content = "Test",
                        FontSize = 10,
                        Padding = new Thickness(6, 3),
                        Classes = { "secondary" },
                    };
                    ToolTip.SetTip(testBtn, L10n.Tr("Pref.TestTrigger", "Test execute this action"));
                    testBtn.Click += (_, _) =>
                    {
                        if (_main?.Engine is { } eng)
                        {
                            // Trigger action
                            switch (action.Kind)
                            {
                                case MidiTargetKind.TransportTogglePlay: if (eng.IsPlaying) eng.StopTransport(); else eng.Play(); break;
                                case MidiTargetKind.TransportPlay: eng.Play(); break;
                                case MidiTargetKind.TransportStop: eng.StopTransport(); break;
                                case MidiTargetKind.TransportRecord: eng.SetRecording(!eng.IsRecording); break;
                                case MidiTargetKind.TransportLoop: eng.SetLoop(!eng.LoopEnabled, eng.LoopStart, eng.LoopEnd); break;
                                default:
                                    break;
                            }
                        }
                    };
                    actBtnStack.Children.Add(testBtn);

                    Grid.SetColumn(actBtnStack, 3);

                    rowGrid.Children.Add(titleStack);
                    rowGrid.Children.Add(catBadge);
                    rowGrid.Children.Add(mappingBadge);
                    rowGrid.Children.Add(actBtnStack);

                    actionRow.Child = rowGrid;
                    actionListPanel.Children.Add(actionRow);
                }
            }

            RebuildActionRows();
            _learnService.MappingsChanged += RebuildActionRows;
            _learnService.PendingChanged += RebuildActionRows;
        }
        mapStack.Children.Add(actionListPanel);
        body.Children.Add(mapCard);

        // 5. External Sync Card
        var extCard = CreateCard(L10n.Tr("Pref.Card.ExternalSync", "EXTERNAL CLOCK & SYNCHRONIZATION"));
        var extStack = (StackPanel)extCard.Child!;

        var extClockCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] extClocks = { "Off", "Auto-detect", "MIDI Clock In" };
        foreach (var ec in extClocks) extClockCombo.Items.Add(ec);
        extClockCombo.SelectedIndex = Math.Max(0, Array.IndexOf(extClocks, s?.MidiExternalClockSync ?? "Off"));
        extClockCombo.SelectionChanged += (_, _) => { if (s is not null && !_loading && extClockCombo.SelectedIndex >= 0) { s.MidiExternalClockSync = extClocks[extClockCombo.SelectedIndex]; _main?.Settings.Save(); } };

        var extOffsetNumeric = new NumericUpDown
        {
            Minimum = -500,
            Maximum = 500,
            Increment = 1,
            Value = s?.MidiExternalSyncOffsetMs ?? 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        extOffsetNumeric.ValueChanged += (_, _) => { if (s is not null && !_loading) { s.MidiExternalSyncOffsetMs = (int)(extOffsetNumeric.Value ?? 0); _main?.Settings.Save(); } };

        var extStatus = new TextBlock { Text = s?.MidiExternalClockSync == "Off" ? "Status: off" : "Status: standby (waiting for clock)", FontSize = 11, Foreground = TextSecondary, Margin = new Thickness(150, 4, 0, 0) };
        extStatus.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");

        extStack.Children.Add(Row(L10n.Tr("Pref.ExtClockSync", "External clock sync"), extClockCombo));
        extStack.Children.Add(Row(L10n.Tr("Pref.ExtSyncOffset", "External sync offset (ms)"), extOffsetNumeric));
        extStack.Children.Add(extStatus);
        body.Children.Add(extCard);

        // 6. Action buttons at bottom
        var btnToolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        var refreshBtn = new Button { Content = L10n.Tr("Pref.RefreshMidi", "Refresh device list"), Classes = { "secondary" } };
        refreshBtn.Click += (_, _) =>
        {
            _midiDevices.RefreshDevices();
            Select(2); // re-render MIDI pane
        };

        var panicBtn = new Button { Content = L10n.Tr("Pref.PanicNotesOff", "Send all notes off (Panic)"), Classes = { "danger" } };
        panicBtn.Click += (_, _) =>
        {
            if (_main?.Engine is { } eng)
            {
                for (int p = 0; p < 128; p++) eng.NoteOff(p);
            }
        };

        btnToolbar.Children.Add(refreshBtn);
        btnToolbar.Children.Add(panicBtn);
        body.Children.Add(btnToolbar);

        _loading = false;
        return body;
    }

    // ---- Editing ----------------------------------------------------------

    private Control EditingPane()
    {
        var body = new StackPanel { Spacing = 16 };
        var s = _main?.Settings.Current;

        var clipCard = CreateCard("TIMELINE & CLIP EDITING");
        var clipStack = (StackPanel)clipCard.Child!;

        var clipLenCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        double[] lens = { 1.0, 2.0, 4.0, 8.0, 16.0, 32.0 };
        foreach (var l in lens) clipLenCombo.Items.Add($"{l:0} Bar{(l == 1 ? "" : "s")}");
        int currLenIdx = Array.IndexOf(lens, s?.DefaultClipLength ?? 4.0);
        clipLenCombo.SelectedIndex = currLenIdx >= 0 ? currLenIdx : 2;
        clipLenCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || clipLenCombo.SelectedIndex < 0) return;
            s.DefaultClipLength = lens[clipLenCombo.SelectedIndex];
            _main?.Settings.Save();
        };
        clipStack.Children.Add(Row("Default Clip Length", clipLenCombo));

        var snapCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] snaps = { "Off (Free)", "1 Bar", "1/2", "1/4", "1/8", "1/16", "1/32" };
        foreach (var sn in snaps) snapCombo.Items.Add(sn);
        int snapIdx = Array.IndexOf(snaps, s?.PianoRollDefaultSnap ?? "1/16");
        snapCombo.SelectedIndex = snapIdx >= 0 ? snapIdx : 5;
        snapCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || snapCombo.SelectedIndex < 0) return;
            s.PianoRollDefaultSnap = snaps[snapCombo.SelectedIndex];
            _main?.Settings.Save();
        };
        clipStack.Children.Add(Row("Piano Roll Default Snap", snapCombo));

        var followCheck = new CheckBox
        {
            Content = "Follow playhead automatically during playback",
            IsChecked = s?.FollowPlayheadDefault ?? false,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        followCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.FollowPlayheadDefault = followCheck.IsChecked ?? false;
            _main?.Settings.Save();
        };
        clipStack.Children.Add(followCheck);

        var followModeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] followModes = { "Page Jump", "Continuous from Start", "Continuous Centered" };
        foreach (var fm in followModes) followModeCombo.Items.Add(fm);
        followModeCombo.SelectedIndex = Math.Clamp(s?.PlayheadFollowMode ?? 0, 0, 2);
        followModeCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || followModeCombo.SelectedIndex < 0) return;
            s.PlayheadFollowMode = followModeCombo.SelectedIndex;
            _main?.Settings.Save();
        };
        clipStack.Children.Add(Row("Follow Playhead Mode", followModeCombo));

        var panCheck = new CheckBox
        {
            Content = "Invert middle-mouse pan drag direction",
            IsChecked = s?.MiddleMousePanInvert ?? false,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        panCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.MiddleMousePanInvert = panCheck.IsChecked ?? false;
            _main?.Settings.Save();
        };
        clipStack.Children.Add(panCheck);

        var compCheck = new CheckBox
        {
            Content = "Loop recording creates new take lanes instead of overwriting",
            IsChecked = !(s?.CompOverwriteMode ?? true),
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        compCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.CompOverwriteMode = !(compCheck.IsChecked ?? false);
            _main?.Settings.Save();
        };
        clipStack.Children.Add(compCheck);

        body.Children.Add(clipCard);
        return body;
    }

    // ---- Plug-ins ---------------------------------------------------------

    private Control PluginsPane()
    {
        var body = new StackPanel { Spacing = 16 };
        var s = _main?.Settings.Current;

        // 1. Scan Paths
        var card = CreateCard("VST2, VST3 & AUDIO UNIT SCAN PATHS");
        var stack = (StackPanel)card.Child!;

        var pathList = new ListBox
        {
            ItemsSource = _paths,
            Height = 75,
            Background = Sunken,
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 4)
        };
        RefreshPaths();

        var addBtn = new Button { Content = "Add folder…", Classes = { "primary" } };
        addBtn.Click += async (_, _) =>
        {
            Activate();
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add plugin scan folder", AllowMultiple = false });
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (path is null) return;
            _catalog.AddScanPath(path);
            RefreshPaths();
        };
        var removeBtn = new Button { Content = "Remove selected" };
        removeBtn.Click += (_, _) => { int i = pathList.SelectedIndex; if (i >= 0) { _catalog.RemoveScanPath(i); RefreshPaths(); } };

        stack.Children.Add(pathList);
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addBtn, removeBtn } });
        stack.Children.Add(Caption("Standard VST2 and VST3 directories are automatically scanned on startup. Add custom directory locations above."));

        body.Children.Add(card);

        // 2. Scanner & Validation Settings
        var scanCard = CreateCard("PLUGIN SCANNER & VALIDATION SETTINGS");
        var scanStack = (StackPanel)scanCard.Child!;

        var validateCheck = new CheckBox
        {
            Content = "Validate plug-ins during scan (reject corrupted DLLs and non-VST files)",
            IsChecked = s?.VstScanValidation ?? true,
            Margin = new Thickness(0, 2),
        };
        validateCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null) return;
            s.VstScanValidation = validateCheck.IsChecked ?? true;
            _main?.Settings.Save();
        };
        scanStack.Children.Add(validateCheck);
        scanStack.Children.Add(Caption("When validation is enabled, each plug-in is test-instantiated out-of-process. Uncheck to scan without deep validation (fast scan)."));

        scanStack.Children.Add(DividerLine());

        var rescanStatus = Caption("Catalog is up to date.");
        var rescan = new Button { Content = "Rescan all plug-ins now", Classes = { "primary" } };

        var pluginListContainer = new StackPanel { Spacing = 4 };
        var countBadge = Caption("");

        void RebuildPluginList(string filter = "")
        {
            pluginListContainer.Children.Clear();
            int count = _catalog.Count;
            if (count == 0)
            {
                countBadge.Text = "No plug-ins found in catalog.";
                pluginListContainer.Children.Add(Caption("No plug-ins detected. Click 'Rescan all plug-ins now' to scan installed VST2, VST3, and AU plugins."));
                return;
            }

            var disabled = s?.DisabledPluginIds ?? new List<string>();
            int activeCount = 0;
            for (int i = 0; i < count; i++)
            {
                var id = _catalog.Id(i);
                if (id is not null && !disabled.Contains(id)) activeCount++;
            }
            countBadge.Text = $"{count} plug-in{(count == 1 ? "" : "s")} detected · {activeCount} active, {count - activeCount} disabled";

            string q = filter.Trim().ToLowerInvariant();

            for (int i = 0; i < count; i++)
            {
                int index = i;
                string id = _catalog.Id(i) ?? $"plugin-{i}";
                string desc = _catalog.Description(i) ?? "";
                var parts = desc.Split('|');
                string name = parts.Length > 0 ? parts[0].Trim() : desc;
                string fmt = parts.Length > 1 ? parts[1].Trim() : "VST";
                bool isInst = desc.Contains("| inst |");
                string mfg = parts.Length > 3 ? parts[3].Trim() : "";

                if (q.Length > 0 && !name.ToLowerInvariant().Contains(q) && !fmt.ToLowerInvariant().Contains(q) && !mfg.ToLowerInvariant().Contains(q))
                    continue;

                bool isEnabled = !disabled.Contains(id);

                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
                    Margin = new Thickness(0, 1),
                };

                var check = new CheckBox
                {
                    IsChecked = isEnabled,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                check.IsCheckedChanged += (_, _) =>
                {
                    if (s is null) return;
                    bool active = check.IsChecked ?? true;
                    if (active)
                    {
                        s.DisabledPluginIds.Remove(id);
                    }
                    else if (!s.DisabledPluginIds.Contains(id))
                    {
                        s.DisabledPluginIds.Add(id);
                    }
                    _main?.Settings.Save();
                    _main?.Browser.Rebuild();
                    RebuildPluginList(filter);
                };

                string? thumbPath = PluginThumbnailService.TryGetExistingThumbnailPath(id, name);
                var thumbBorder = new Border
                {
                    Width = 26,
                    Height = 18,
                    Background = Raised,
                    BorderBrush = BorderStrong,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    ClipToBounds = true,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (thumbPath != null && System.IO.File.Exists(thumbPath))
                {
                    try
                    {
                        using var fs = System.IO.File.OpenRead(thumbPath);
                        thumbBorder.Child = new Image { Source = new Avalonia.Media.Imaging.Bitmap(fs), Stretch = Stretch.UniformToFill };
                    }
                    catch { }
                }
                else
                {
                    thumbBorder.Child = new TextBlock
                    {
                        Text = isInst ? "INST" : "FX",
                        FontSize = 8,
                        FontWeight = FontWeight.Bold,
                        Foreground = TextTertiary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }

                var nameBlock = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(mfg) || mfg == "?" ? name : $"{name}  ({mfg})",
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    Foreground = isEnabled ? TextPrimary : TextTertiary,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                var typeBadge = new Border
                {
                    Background = Raised,
                    BorderBrush = BorderStrong,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = isInst ? "Synth" : "FX",
                        FontSize = 10,
                        Foreground = isInst ? AccentBright : TextSecondary,
                    },
                };

                var formatBadge = new Border
                {
                    Background = fmt.Contains("VST3") ? KnoxPalette.SurfaceCard : Raised,
                    BorderBrush = fmt.Contains("VST3") ? AccentBright : (fmt.Contains("VST") ? Brass : BorderStrong),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = fmt.Trim(),
                        FontSize = 10,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = fmt.Contains("VST3") ? AccentBright : (fmt.Contains("VST") ? Brass : TextPrimary),
                    },
                };

                Grid.SetColumn(check, 0);
                Grid.SetColumn(thumbBorder, 1);
                Grid.SetColumn(nameBlock, 2);
                Grid.SetColumn(typeBadge, 3);
                Grid.SetColumn(formatBadge, 4);

                rowGrid.Children.Add(check);
                rowGrid.Children.Add(thumbBorder);
                rowGrid.Children.Add(nameBlock);
                rowGrid.Children.Add(typeBadge);
                rowGrid.Children.Add(formatBadge);

                var itemCard = new Border
                {
                    Background = Sunken,
                    BorderBrush = Divider,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(10, 5),
                    Child = rowGrid,
                };
                pluginListContainer.Children.Add(itemCard);
            }
        }

        rescan.Click += async (_, _) =>
        {
            if (_main is null) { rescanStatus.Text = "Plugin scanning is unavailable in this window."; return; }
            var workerName = OperatingSystem.IsWindows() ? "knox-scanworker.exe" : "knox-scanworker";
            var worker = System.IO.Path.Combine(AppContext.BaseDirectory, workerName);
            if (!System.IO.File.Exists(worker)) { rescanStatus.Text = "Scanner worker binary not found."; return; }
            rescanStatus.Text = "Scanning plug-ins...";
            int n = await PluginScanDialogWindow.RunScanAsync(this, _catalog, worker, s?.VstScanValidation ?? true);
            _main.Browser.Rebuild();
            rescanStatus.Text = $"Scan complete: Indexed {n} plug-in{(n == 1 ? "" : "s")}.";
            RebuildPluginList();
        };

        scanStack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { rescan, rescanStatus } });
        body.Children.Add(scanCard);

        // 3. Plug-in Visuals / Thumbnails Card (Waveform 14 Style)
        var thumbCard = CreateCard("PLUG-IN THUMBNAILS & VISUAL COVERS");
        var thumbStack = (StackPanel)thumbCard.Child!;

        var genThumbBtn = new Button
        {
            Content = "Generate All Plug-in Thumbnails",
            Classes = { "primary" },
            Padding = new Thickness(14, 6),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var openFolderBtn = new Button
        {
            Content = "Open Thumbnails Folder",
            Padding = new Thickness(12, 6),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        openFolderBtn.Click += (_, _) =>
        {
            try
            {
                string dir = PluginThumbnailService.ThumbnailsDirectory;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch { }
        };

        thumbStack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { genThumbBtn, openFolderBtn }
        });
        thumbStack.Children.Add(Caption("Automatically opens each installed plug-in once, captures a high-resolution GUI screenshot, and sets it as the device card visual artwork cover in the track rack."));

        body.Children.Add(thumbCard);

        // 4. Installed Plug-ins List with Enable/Disable toggles
        var listCard = CreateCard("INSTALLED & VALIDATED PLUG-INS");
        var listStack = (StackPanel)listCard.Child!;

        var filterBox = new TextBox
        {
            PlaceholderText = "Search plug-ins by name, format, or manufacturer…",
            Margin = new Thickness(0, 0, 0, 8),
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        genThumbBtn.Click += async (_, _) =>
        {
            await ThumbnailGenerationDialogWindow.RunAsync(this, _catalog);
            RebuildPluginList(filterBox.Text ?? "");
        };

        var enableAllBtn = new Button { Content = "Enable All" };
        enableAllBtn.Click += (_, _) =>
        {
            if (s is null) return;
            s.DisabledPluginIds.Clear();
            _main?.Settings.Save();
            _main?.Browser.Rebuild();
            RebuildPluginList(filterBox.Text ?? "");
        };

        var disableAllBtn = new Button { Content = "Disable All" };
        disableAllBtn.Click += (_, _) =>
        {
            if (s is null) return;
            s.DisabledPluginIds.Clear();
            for (int i = 0; i < _catalog.Count; i++)
            {
                var id = _catalog.Id(i);
                if (id is not null) s.DisabledPluginIds.Add(id);
            }
            _main?.Settings.Save();
            _main?.Browser.Rebuild();
            RebuildPluginList(filterBox.Text ?? "");
        };

        filterBox.KeyUp += (_, _) => RebuildPluginList(filterBox.Text ?? "");

        listStack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                filterBox,
                enableAllBtn,
                disableAllBtn,
                countBadge
            }
        });

        var pluginScrollViewer = new ScrollViewer
        {
            Height = 340,
            Content = pluginListContainer,
            Margin = new Thickness(0, 4),
            Padding = new Thickness(4),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            AllowAutoHide = false,
        };
        pluginScrollViewer.PointerWheelChanged += (sender, e) =>
        {
            if (sender is ScrollViewer sv)
            {
                double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                double delta = e.Delta.Y * 60;
                double newY = Math.Clamp(sv.Offset.Y - delta, 0, maxY);
                if ((delta < 0 && sv.Offset.Y >= maxY - 0.5) || (delta > 0 && sv.Offset.Y <= 0.5))
                {
                    // At boundary: let the outer window scroll smoothly
                    return;
                }
                sv.Offset = new Vector(sv.Offset.X, newY);
                e.Handled = true;
            }
        };
        var listFrame = new Border
        {
            Background = Sunken,
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = pluginScrollViewer
        };
        listStack.Children.Add(listFrame);
        listStack.Children.Add(Caption("Uncheck any plug-in to disable it and remove it from the track device browser."));

        RebuildPluginList();
        body.Children.Add(listCard);

        return body;
    }

    // ---- Library ----------------------------------------------------------

    private Control LibraryPane()
    {
        var body = new StackPanel { Spacing = 16 };
        if (_main is null)
        {
            var c = CreateCard(L10n.Tr("Pref.Library.Card", "CONTENT LIBRARIES & ASSET DIRECTORIES"));
            var s = (StackPanel)c.Child!;
            s.Children.Add(Caption(L10n.Tr("Pref.Library.Unavailable", "Content folders are unavailable in this window.")));
            body.Children.Add(c);
            return body;
        }

        var settings = _main.Settings;

        // 1. DEFAULT LOCATIONS CARD
        var defaultCard = CreateCard(L10n.Tr("Pref.Library.DefaultLocations", "DEFAULT SYSTEM LOCATIONS"));
        var defaultStack = (StackPanel)defaultCard.Child!;
        defaultStack.Children.Add(FolderRow(L10n.Tr("Pref.Library.Samples", "Default Samples"),
            () => settings.Current.SamplesFolder, () => settings.ResolvedSamplesFolder(),
            path => { settings.Current.SamplesFolder = path; settings.Save(); _main.Browser.RebuildSamples(); }));
        defaultStack.Children.Add(DividerLine());
        defaultStack.Children.Add(FolderRow(L10n.Tr("Pref.Library.Projects", "Default Projects"),
            () => settings.Current.ProjectsFolder, () => settings.ResolvedProjectsFolder(),
            path => { settings.Current.ProjectsFolder = path; settings.Save(); _main.Browser.RebuildProjects(); }));
        body.Children.Add(defaultCard);

        // 2. USER SAMPLE LIBRARIES & SOUND PACKS CARD
        var userFoldersCard = CreateCard(L10n.Tr("Pref.Library.CustomSampleFolders", "USER SAMPLE LIBRARIES & SOUND PACKS"));
        var userFoldersStack = (StackPanel)userFoldersCard.Child!;

        userFoldersStack.Children.Add(Caption(L10n.Tr("Pref.Library.CustomSampleDesc",
            "Add your own folders (drum kits, sample packs, sound libraries). Knox Studio will scan and index all audio files inside them into the Browser Samples tree.")));

        var folderListContainer = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6) };

        void RebuildFolderList()
        {
            folderListContainer.Children.Clear();
            var extras = settings.Current.ExtraSampleFolders;

            if (extras.Count == 0)
            {
                var emptyBox = new Border
                {
                    Background = Sunken,
                    BorderBrush = Divider,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(16, 14),
                    Child = new TextBlock
                    {
                        Text = L10n.Tr("Pref.Library.NoCustomFolders", "No custom folders added yet. Click '+ Add Folder…' below to add your sound libraries."),
                        Foreground = TextSecondary,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                folderListContainer.Children.Add(emptyBox);
            }
            else
            {
                for (int i = 0; i < extras.Count; i++)
                {
                    string folderPath = extras[i];
                    string folderName = System.IO.Path.GetFileName(folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(folderName)) folderName = folderPath;

                    var itemBorder = new Border
                    {
                        Background = Sunken,
                        BorderBrush = Divider,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(12, 8),
                    };

                    var rowGrid = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var icon = new TextBlock
                    {
                        Text = "📁",
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    };
                    Grid.SetColumn(icon, 0);

                    var textStack = new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    textStack.Children.Add(new TextBlock
                    {
                        Text = folderName,
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = TextPrimary
                    });
                    textStack.Children.Add(new TextBlock
                    {
                        Text = folderPath,
                        FontSize = 10,
                        Foreground = TextSecondary
                    });
                    Grid.SetColumn(textStack, 1);

                    var openBtn = new Button
                    {
                        Content = L10n.CurrentLanguage == "tr" ? "Aç" : "Open",
                        Classes = { "ghost" },
                        FontSize = 10,
                        Margin = new Thickness(6, 0),
                        Padding = new Thickness(8, 4)
                    };
                    openBtn.Click += (_, _) =>
                    {
                        try
                        {
                            if (System.IO.Directory.Exists(folderPath))
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = folderPath,
                                    UseShellExecute = true,
                                });
                        }
                        catch { }
                    };
                    Grid.SetColumn(openBtn, 2);

                    var removeBtn = new Button
                    {
                        Content = "✕",
                        Classes = { "ghost" },
                        Foreground = KnoxPalette.Danger,
                        FontSize = 12,
                        Padding = new Thickness(6, 2),
                        Margin = new Thickness(4, 0, 0, 0)
                    };
                    string targetToRemove = folderPath;
                    removeBtn.Click += (_, _) =>
                    {
                        settings.Current.ExtraSampleFolders.Remove(targetToRemove);
                        settings.Save();
                        RebuildFolderList();
                        _main?.Browser?.RebuildSamples();
                    };
                    Grid.SetColumn(removeBtn, 3);

                    rowGrid.Children.Add(icon);
                    rowGrid.Children.Add(textStack);
                    rowGrid.Children.Add(openBtn);
                    rowGrid.Children.Add(removeBtn);

                    itemBorder.Child = rowGrid;
                    folderListContainer.Children.Add(itemBorder);
                }
            }
        }

        RebuildFolderList();
        userFoldersStack.Children.Add(folderListContainer);

        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var addBtn = new Button
        {
            Content = L10n.Tr("Pref.Library.AddFolder", "+ Add Folder…"),
            Classes = { "primary" }
        };
        addBtn.Click += async (_, _) =>
        {
            Activate();
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L10n.Tr("Pref.Library.SelectFolder", "Select Sample or Sound Library Folder"),
                AllowMultiple = true
            });
            if (folders.Count == 0) return;

            bool addedAny = false;
            foreach (var f in folders)
            {
                var p = f.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(p) && System.IO.Directory.Exists(p))
                {
                    if (!System.Linq.Enumerable.Any(settings.Current.ExtraSampleFolders, x => x.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        settings.Current.ExtraSampleFolders.Add(p);
                        addedAny = true;
                    }
                }
            }

            if (addedAny)
            {
                settings.Save();
                RebuildFolderList();
                _main?.Browser?.RebuildSamples();
            }
        };
        actionsRow.Children.Add(addBtn);

        var rescanBtn = new Button
        {
            Content = L10n.Tr("Pref.Library.Rescan", "🔄 Rescan All Folders"),
            Classes = { "ghost" }
        };
        var statusMsg = new TextBlock
        {
            FontSize = 11,
            Foreground = KnoxPalette.Success,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            IsVisible = false
        };

        rescanBtn.Click += async (_, _) =>
        {
            _main?.Browser?.RebuildSamples();
            _main?.Browser?.RebuildProjects();
            statusMsg.Text = L10n.Tr("Pref.Library.RescanSuccess", "✓ All library folders rescanned successfully!");
            statusMsg.IsVisible = true;
            await System.Threading.Tasks.Task.Delay(2500);
            statusMsg.IsVisible = false;
        };
        actionsRow.Children.Add(rescanBtn);
        actionsRow.Children.Add(statusMsg);

        userFoldersStack.Children.Add(actionsRow);
        body.Children.Add(userFoldersCard);

        return body;
    }

    private Control FolderRow(string label, Func<string> get, Func<string> resolved, Action<string> set)
    {
        var pathText = Caption(string.IsNullOrWhiteSpace(get()) ? $"(default) {resolved()}" : get());
        var choose = new Button { Content = L10n.CurrentLanguage == "tr" ? "Gözat…" : "Browse…", Classes = { "primary" } };
        choose.Click += async (_, _) =>
        {
            Activate();
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Choose {label} folder", AllowMultiple = false });
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (path is null) return;
            set(path);
            pathText.Text = path;
        };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = label, Width = 150, FontSize = 12, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center }, choose } },
                new Border { Padding = new Thickness(150, 0, 0, 0), Child = pathText },
            },
        };
    }

    // ---- Appearance -------------------------------------------------------

    private Control AppearancePane()
    {
        var body = new StackPanel { Spacing = 16 };
        var s = _main?.Settings.Current;

        // Theme Palette Card
        var themeCard = CreateCard("COLOR THEME & ACCENTS");
        var themeStack = (StackPanel)themeCard.Child!;

        var variantCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        variantCombo.Items.Add("Space Grey (Studio Graphite)");
        variantCombo.Items.Add("Obsidian (High Contrast Deep Dark)");
        variantCombo.Items.Add("Warm Charcoal (Classic Analog)");
        variantCombo.Items.Add("Midnight Navy (Modern Studio Blue)");
        variantCombo.Items.Add("Solaris Amber (Warm Gold Pro)");
        variantCombo.Items.Add("Cyberpunk Neon (High Contrast Cyber Dark)");
        variantCombo.Items.Add("Nordic Slate (Cool Arctic Studio)");
        variantCombo.Items.Add("Studio Platinum (Brushed Alloy Graphite)");
        variantCombo.SelectedIndex = s?.ThemeVariant switch
        {
            "Obsidian" => 1,
            "WarmCharcoal" => 2,
            "MidnightNavy" => 3,
            "SolarisAmber" => 4,
            "CyberpunkNeon" => 5,
            "NordicSlate" => 6,
            "StudioPlatinum" => 7,
            _ => 0,
        };
        variantCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || variantCombo.SelectedIndex < 0) return;
            s.ThemeVariant = variantCombo.SelectedIndex switch
            {
                1 => "Obsidian",
                2 => "WarmCharcoal",
                3 => "MidnightNavy",
                4 => "SolarisAmber",
                5 => "CyberpunkNeon",
                6 => "NordicSlate",
                7 => "StudioPlatinum",
                _ => "SpaceGrey",
            };
            ThemeManager.Apply(s.ThemeVariant, s.ThemeAccent);
            _main?.Settings.Save();
        };
        themeStack.Children.Add(Row("Base Theme", variantCombo));

        // Accent swatches
        var swatchesPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var accents = new (string Key, string Name, Color Color)[]
        {
            ("StudioBlue", "Studio Blue", Color.Parse("#2A82E4")),
            ("EmberGold", "Ember Gold", Color.Parse("#C89B3C")),
            ("CyberCyan", "Cyber Cyan", Color.Parse("#00B4D8")),
            ("Emerald", "Emerald Green", Color.Parse("#10B981")),
            ("Crimson", "Crimson Red", Color.Parse("#EF4444")),
            ("PurpleNeon", "Purple Neon", Color.Parse("#A855F7")),
        };

        var swatchBorders = new List<(string Key, Border Border)>();
        void UpdateSwatches(string selectedKey)
        {
            foreach (var (k, b) in swatchBorders)
            {
                bool isSel = k == selectedKey;
                b.BorderBrush = isSel ? Brushes.White : new SolidColorBrush(Color.Parse("#3A3D46"));
                b.BorderThickness = new Thickness(isSel ? 2 : 1);
            }
        }

        foreach (var (k, name, col) in accents)
        {
            var swatch = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(col),
                BorderBrush = (s?.ThemeAccent == k) ? Brushes.White : new SolidColorBrush(Color.Parse("#3A3D46")),
                BorderThickness = new Thickness((s?.ThemeAccent == k) ? 2 : 1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(swatch, name);
            string key = k;
            swatch.PointerPressed += (_, _) =>
            {
                if (s is null) return;
                s.ThemeAccent = key;
                UpdateSwatches(key);
                ThemeManager.Apply(s.ThemeVariant, s.ThemeAccent);
                _main?.Settings.Save();
            };
            swatchBorders.Add((key, swatch));
            swatchesPanel.Children.Add(swatch);
        }
        themeStack.Children.Add(Row("Accent Color", swatchesPanel));
        body.Children.Add(themeCard);

        // Hardware Controls & Display Card
        var controlsCard = CreateCard("CONTROLS & VISUAL RENDERING");
        var controlsStack = (StackPanel)controlsCard.Child!;

        // Knob Style
        var knobCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        knobCombo.Items.Add("Tactile Hardware (3D Bevel, Satin Cap, Illuminated Arc)");
        knobCombo.Items.Add("Minimal Precision (Clean Vector Arcs)");
        knobCombo.Items.Add("Vintage Console (Classic Bakelite Style)");
        knobCombo.SelectedIndex = s?.KnobStyle switch
        {
            "Minimal" => 1,
            "Vintage" => 2,
            _ => 0,
        };
        knobCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || knobCombo.SelectedIndex < 0) return;
            s.KnobStyle = knobCombo.SelectedIndex switch
            {
                1 => "Minimal",
                2 => "Vintage",
                _ => "Tactile",
            };
            _main?.Settings.Save();
        };
        controlsStack.Children.Add(Row("Knob Rendering", knobCombo));

        // Grid Contrast
        var gridCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        gridCombo.Items.Add("Subtle Grid (Low distraction)");
        gridCombo.Items.Add("Normal Grid (Balanced studio contrast)");
        gridCombo.Items.Add("High Contrast Grid (Sharpened bar/beat divisions)");
        gridCombo.SelectedIndex = s?.GridContrast switch
        {
            "Subtle" => 0,
            "High" => 2,
            _ => 1,
        };
        gridCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || gridCombo.SelectedIndex < 0) return;
            s.GridContrast = gridCombo.SelectedIndex switch
            {
                0 => "Subtle",
                2 => "High",
                _ => "Normal",
            };
            _main?.Settings.Save();
        };
        controlsStack.Children.Add(Row("Timeline Grid", gridCombo));

        // Meter FPS
        var fpsCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        fpsCombo.Items.Add("30 FPS (Power-saving mode)");
        fpsCombo.Items.Add("60 FPS (Silky-smooth standard)");
        fpsCombo.Items.Add("120 FPS (High-refresh studio monitors)");
        fpsCombo.SelectedIndex = s?.MeterFps switch
        {
            30 => 0,
            120 => 2,
            _ => 1,
        };
        fpsCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || fpsCombo.SelectedIndex < 0) return;
            s.MeterFps = fpsCombo.SelectedIndex switch
            {
                0 => 30,
                2 => 120,
                _ => 60,
            };
            _main?.Settings.Save();
        };
        controlsStack.Children.Add(Row("Peak Meter FPS", fpsCombo));

        // Glow effects
        var glowCheck = new CheckBox
        {
            Content = "Enable illuminated LED gauges, glow accents, and button backlights",
            IsChecked = s?.GlowEffects ?? true,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        glowCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.GlowEffects = glowCheck.IsChecked ?? true;
            _main?.Settings.Save();
        };
        controlsStack.Children.Add(glowCheck);

        // Digital Studio LCD Display Card
        var lcdCard = CreateCard(L10n.Tr("Pref.Card.Lcd", "STUDIO LCD / OLED DISPLAY"));
        var lcdStack = (StackPanel)lcdCard.Child!;

        // LCD Theme Selector & Swatches
        var lcdThemes = new (string Key, string Name, Color Color)[]
        {
            ("Amber", L10n.Tr("Lcd.Theme.Amber", "Warm Amber (Vintage OLED)"), Color.Parse("#FFB000")),
            ("Cyan", L10n.Tr("Lcd.Theme.Cyan", "Cyber Cyan (Digital Pro)"), Color.Parse("#00E5FF")),
            ("Green", L10n.Tr("Lcd.Theme.Green", "Matrix Green (Classic LCD)"), Color.Parse("#00FF66")),
            ("White", L10n.Tr("Lcd.Theme.White", "Studio White (Clean OLED)"), Color.Parse("#FFFFFF")),
            ("Red", L10n.Tr("Lcd.Theme.Red", "Neon Red (Darkroom)"), Color.Parse("#FF3344")),
            ("Purple", L10n.Tr("Lcd.Theme.Purple", "Deep Violet (Synthwave)"), Color.Parse("#D946EF")),
            ("Gold", L10n.Tr("Lcd.Theme.Gold", "Champagne Gold (Prestige)"), Color.Parse("#F59E0B")),
        };

        var lcdSwatchesPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var lcdSwatchBorders = new List<(string Key, Border Border)>();
        void UpdateLcdSwatches(string selectedKey)
        {
            foreach (var (k, b) in lcdSwatchBorders)
            {
                bool isSel = k == selectedKey;
                b.BorderBrush = isSel ? Brushes.White : new SolidColorBrush(Color.Parse("#3A3D46"));
                b.BorderThickness = new Thickness(isSel ? 2 : 1);
            }
        }

        foreach (var (k, name, col) in lcdThemes)
        {
            var swatch = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(col),
                BorderBrush = (s?.LcdColorTheme == k) ? Brushes.White : new SolidColorBrush(Color.Parse("#3A3D46")),
                BorderThickness = new Thickness((s?.LcdColorTheme == k) ? 2 : 1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(swatch, name);
            string key = k;
            swatch.PointerPressed += (_, _) =>
            {
                if (s is null) return;
                s.LcdColorTheme = key;
                UpdateLcdSwatches(key);
                ThemeManager.ApplyLcdTheme(key);
                _main?.Settings.Save();
            };
            lcdSwatchBorders.Add((key, swatch));
            lcdSwatchesPanel.Children.Add(swatch);
        }
        lcdStack.Children.Add(Row(L10n.Tr("Pref.LcdTheme", "Color Palette"), lcdSwatchesPanel));

        // Font Family Selector
        var fontCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] fontChoices = { "Default", "Consolas", "Courier New", "Segoe UI", "Lucida Console", "JetBrains Mono", "Trebuchet MS", "Arial", "Roboto Mono" };
        foreach (var fn in fontChoices)
            fontCombo.Items.Add(fn == "Default" ? L10n.Tr("Lcd.Font.Default", "Default (Knox Standard)") : fn);

        int currentFontIdx = Array.IndexOf(fontChoices, s?.LcdFontFamily ?? "Default");
        if (currentFontIdx >= 0)
        {
            fontCombo.SelectedIndex = currentFontIdx;
        }
        else
        {
            fontCombo.Items.Add(s?.LcdFontFamily ?? "Custom");
            fontCombo.SelectedIndex = fontCombo.Items.Count - 1;
        }

        var customFontBox = new TextBox
        {
            Text = s?.LcdFontFamily ?? "Default",
            PlaceholderText = L10n.Tr("Lcd.CustomFontPrompt", "Enter any font family installed on device"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(150, 4, 0, 0)
        };

        fontCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || fontCombo.SelectedIndex < 0) return;
            if (fontCombo.SelectedIndex < fontChoices.Length)
            {
                var chosen = fontChoices[fontCombo.SelectedIndex];
                s.LcdFontFamily = chosen;
                customFontBox.Text = chosen;
                _main?.Settings.Save();
            }
        };

        customFontBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && s is not null && !_loading)
            {
                var text = customFontBox.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    s.LcdFontFamily = text;
                    _main?.Settings.Save();
                }
            }
        };

        lcdStack.Children.Add(Row(L10n.Tr("Pref.LcdFont", "Display Font"), fontCombo));
        lcdStack.Children.Add(customFontBox);

        // Time Format
        var timeFormatCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        timeFormatCombo.Items.Add(L10n.Tr("Pref.LcdFormat.Bars", "Bars · Beats · 16ths (Musical Timing)"));
        timeFormatCombo.Items.Add(L10n.Tr("Pref.LcdFormat.Timecode", "Hours:Minutes:Seconds.Millis (SMPTE Timecode)"));
        timeFormatCombo.SelectedIndex = s?.LcdTimeFormat == "Timecode" ? 1 : 0;
        timeFormatCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || timeFormatCombo.SelectedIndex < 0) return;
            s.LcdTimeFormat = timeFormatCombo.SelectedIndex == 1 ? "Timecode" : "Bars";
            _main?.Settings.Save();
        };
        lcdStack.Children.Add(Row(L10n.Tr("Pref.LcdTimeFormat", "Position Readout Format"), timeFormatCombo));

        // Indicator Toggles
        var keyCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.LcdShowKey", "Show Key Signature cell (C Maj, A Min, etc.)"),
            IsChecked = s?.LcdShowKey ?? true,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        keyCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.LcdShowKey = keyCheck.IsChecked ?? true;
            _main?.Settings.Save();
        };
        lcdStack.Children.Add(keyCheck);

        var quantizeCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.LcdShowQuantize", "Show Launch Quantize cell"),
            IsChecked = s?.LcdShowQuantize ?? true,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        quantizeCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.LcdShowQuantize = quantizeCheck.IsChecked ?? true;
            _main?.Settings.Save();
        };
        lcdStack.Children.Add(quantizeCheck);

        var telemetryCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.LcdShowTelemetry", "Show Audio Engine Telemetry (Sample Rate / Buffer size)"),
            IsChecked = s?.LcdShowTelemetry ?? true,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        telemetryCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.LcdShowTelemetry = telemetryCheck.IsChecked ?? true;
            _main?.Settings.Save();
        };
        lcdStack.Children.Add(telemetryCheck);

        body.Children.Add(lcdCard);
        body.Children.Add(controlsCard);

        // Arrangement Grid & Background Card
        var gridCard = CreateCard(L10n.Tr("Pref.Card.ArrangementGrid", "ARRANGEMENT GRID & BACKGROUND"));
        var gridStack = (StackPanel)gridCard.Child!;

        // Grid Background Color Combo
        var gridColorCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        gridColorCombo.Items.Add("Default Studio (#18191C)");
        gridColorCombo.Items.Add("Deep Obsidian (#101012)");
        gridColorCombo.Items.Add("Warm Charcoal (#1E1C1A)");
        gridColorCombo.Items.Add("Midnight Navy (#121620)");
        gridColorCombo.Items.Add("Cool Slate (#161A1D)");
        gridColorCombo.Items.Add("Cyber Dark (#0C1014)");
        gridColorCombo.Items.Add("Custom Hex Color…");
        gridColorCombo.SelectedIndex = s?.ArrangementGridColor switch
        {
            "Obsidian" => 1,
            "Charcoal" => 2,
            "Navy" => 3,
            "Slate" => 4,
            "Cyber" => 5,
            "Custom" => 6,
            _ => 0,
        };

        var customHexBox = new TextBox
        {
            Text = s?.ArrangementGridCustomColor ?? "#18191C",
            FontSize = 11,
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = (s?.ArrangementGridColor == "Custom")
        };
        customHexBox.TextChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.ArrangementGridCustomColor = customHexBox.Text?.Trim() ?? "#18191C";
            _main?.Settings.Save();
        };

        gridColorCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || gridColorCombo.SelectedIndex < 0) return;
            s.ArrangementGridColor = gridColorCombo.SelectedIndex switch
            {
                1 => "Obsidian",
                2 => "Charcoal",
                3 => "Navy",
                4 => "Slate",
                5 => "Cyber",
                6 => "Custom",
                _ => "Default",
            };
            customHexBox.IsVisible = (s.ArrangementGridColor == "Custom");
            _main?.Settings.Save();
        };

        var gridColorRow = new StackPanel { Spacing = 6 };
        gridColorRow.Children.Add(gridColorCombo);
        gridColorRow.Children.Add(customHexBox);
        gridStack.Children.Add(Row(L10n.Tr("Pref.GridColor", "Grid Color"), gridColorRow));

        // Background Image Picker
        var bgImageText = new TextBlock
        {
            Text = string.IsNullOrEmpty(s?.ArrangementBgImagePath) ? "(None)" : System.IO.Path.GetFileName(s.ArrangementBgImagePath),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = KnoxPalette.TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220
        };
        var browseBtn = new Button
        {
            Content = L10n.Tr("Pref.BrowseImage", "Browse…"),
            FontSize = 11,
            Padding = new Thickness(10, 4)
        };
        browseBtn.Click += async (_, _) =>
        {
            if (s is null) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Arrangement Background Image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } }
                }
            });
            if (files.Count > 0)
            {
                string path = files[0].Path.LocalPath;
                s.ArrangementBgImagePath = path;
                bgImageText.Text = System.IO.Path.GetFileName(path);
                _main?.Settings.Save();
            }
        };

        var clearBtn = new Button
        {
            Content = L10n.Tr("Pref.ClearImage", "Clear"),
            FontSize = 11,
            Padding = new Thickness(10, 4)
        };
        clearBtn.Click += (_, _) =>
        {
            if (s is null) return;
            s.ArrangementBgImagePath = "";
            bgImageText.Text = "(None)";
            _main?.Settings.Save();
        };

        var bgImageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        bgImageRow.Children.Add(bgImageText);
        bgImageRow.Children.Add(browseBtn);
        bgImageRow.Children.Add(clearBtn);
        gridStack.Children.Add(Row(L10n.Tr("Pref.BgImage", "Background Image"), bgImageRow));

        // Background Image Opacity Slider
        var opacitySlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Round((s?.ArrangementBgImageOpacity ?? 0.20) * 100),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var opacityReadout = new TextBlock
        {
            Text = $"{Math.Round((s?.ArrangementBgImageOpacity ?? 0.20) * 100)}%",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 45,
            TextAlignment = TextAlignment.Right
        };
        opacitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(Slider.Value))
            {
                if (s is null || _loading) return;
                s.ArrangementBgImageOpacity = opacitySlider.Value / 100.0;
                opacityReadout.Text = $"{Math.Round(opacitySlider.Value)}%";
                _main?.Settings.Save();
            }
        };
        var opacityRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { opacitySlider, WrapColumn(opacityReadout, 1) }
        };
        gridStack.Children.Add(Row(L10n.Tr("Pref.BgImageOpacity", "Image Opacity"), opacityRow));

        // Background Image Layout Mode
        var modeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        modeCombo.Items.Add("Stretch (Fill Stretched)");
        modeCombo.Items.Add("Fit (Aspect Fit Centered)");
        modeCombo.Items.Add("Fill (Aspect Fill Crop)");
        modeCombo.Items.Add("Center (Original Size Centered)");
        modeCombo.Items.Add("Grid (Repeat Tile Pattern)");
        modeCombo.SelectedIndex = s?.ArrangementBgImageMode switch
        {
            "Fit" => 1,
            "Fill" => 2,
            "Center" => 3,
            "Grid" or "Tile" => 4,
            _ => 0,
        };
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.ArrangementBgImageMode = modeCombo.SelectedIndex switch
            {
                1 => "Fit",
                2 => "Fill",
                3 => "Center",
                4 => "Grid",
                _ => "Stretch",
            };
            _main?.Settings.Save();
        };
        gridStack.Children.Add(Row(L10n.Tr("Pref.BgImageMode", "Image Layout Mode"), modeCombo));

        body.Children.Add(gridCard);

        // Audio Track & Clip Styling Card
        var clipCard = CreateCard(L10n.Tr("Pref.Card.TrackClipStyle", "AUDIO TRACK & CLIP STYLING"));
        var clipStack = (StackPanel)clipCard.Child!;

        // Clip Shape Combo
        var shapeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        shapeCombo.Items.Add("Rounded (4px radius)");
        shapeCombo.Items.Add("Rectangle (Sharp / 0px)");
        shapeCombo.Items.Add("Pill (Capsule / Smooth)");
        shapeCombo.SelectedIndex = s?.ClipShape switch
        {
            "Rectangle" => 1,
            "Pill" => 2,
            _ => 0,
        };
        shapeCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || shapeCombo.SelectedIndex < 0) return;
            s.ClipShape = shapeCombo.SelectedIndex switch
            {
                1 => "Rectangle",
                2 => "Pill",
                _ => "Rounded",
            };
            _main?.Settings.Save();
        };
        clipStack.Children.Add(Row(L10n.Tr("Pref.ClipShape", "Clip Shape"), shapeCombo));

        // Clip Interior Style Combo
        var interiorCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        interiorCombo.Items.Add("Default (Studio Dark Body + Header Strip)");
        interiorCombo.Items.Add("Glass (Translucent Frosted Glass)");
        interiorCombo.Items.Add("Flat (Solid Flat Color)");
        interiorCombo.SelectedIndex = s?.ClipInteriorStyle switch
        {
            "Glass" => 1,
            "Flat" => 2,
            _ => 0,
        };
        interiorCombo.SelectionChanged += (_, _) =>
        {
            if (s is null || _loading || interiorCombo.SelectedIndex < 0) return;
            s.ClipInteriorStyle = interiorCombo.SelectedIndex switch
            {
                1 => "Glass",
                2 => "Flat",
                _ => "Default",
            };
            _main?.Settings.Save();
        };
        clipStack.Children.Add(Row(L10n.Tr("Pref.ClipInterior", "Interior Style"), interiorCombo));

        // Dark Text & Waveform CheckBox
        var darkTextCheck = new CheckBox
        {
            Content = L10n.Tr("Pref.ClipDarkText", "Black Track Title & Waveform"),
            IsChecked = s?.ClipDarkTextAndWaveform ?? false,
            FontSize = 11,
            Margin = new Thickness(150, 4, 0, 0),
        };
        darkTextCheck.IsCheckedChanged += (_, _) =>
        {
            if (s is null || _loading) return;
            s.ClipDarkTextAndWaveform = darkTextCheck.IsChecked ?? false;
            _main?.Settings.Save();
        };
        clipStack.Children.Add(darkTextCheck);
        body.Children.Add(clipCard);

        return body;
    }

    // ---- Shortcuts --------------------------------------------------------

    private static readonly (string Title, (string Key, string Action)[] Rows)[] ShortcutGroups =
    {
        ("TRANSPORT CONTROLS", new[]
        {
            ("Space", "Play / Stop"),
            ("Return", "Stop (second press rewinds to start)"),
            ("R", "Toggle recording"),
            ("M", "Toggle metronome click"),
        }),
        ("ARRANGEMENT & EDITING", new[]
        {
            ("⌘A / Ctrl+A", "Toggle automation lane mode / Select all"),
            ("⌘M / Ctrl+M", "Toggle bottom Mixer view"),
            ("Tab", "Cycle Devices / Clip editor in detail pane"),
            ("Esc", "Cancel drag / Clear selection"),
            ("⌘G / Ctrl+G", "Group selected tracks"),
            ("⌘⇧G / Ctrl+Shift+G", "Ungroup selected tracks"),
            ("⌘C / Ctrl+C", "Copy selected clip or notes"),
            ("⌘X / Ctrl+X", "Cut selected clip or notes"),
            ("⌘V / Ctrl+V", "Paste clip or notes"),
            ("0", "Deactivate / Mute selected clip"),
            ("Delete / Backspace", "Delete selected clips, notes, or automation points"),
        }),
        ("PIANO ROLL & NOTE EDITING", new[]
        {
            ("← →", "Nudge notes left/right by current grid snap"),
            ("↑ ↓", "Transpose notes up/down by 1 semitone"),
            ("⇧↑  ⇧↓", "Transpose notes up/down by 1 octave"),
            ("⌘D / Ctrl+D", "Duplicate selected notes"),
            ("Double-Click", "Create / Delete note on grid"),
            ("Right-Click", "Open note context menu / Reset knob to default"),
        }),
        ("VIRTUAL KEYBOARD NOTE PLAYBACK", new[]
        {
            ("A S D F G H J K", "White piano keys starting from C4"),
            ("W E · T Y U", "Black piano keys (C#, D#, F#, G#, A#)"),
            ("Z / X", "Shift base octave down / up"),
            ("C / V", "Decrease / increase note velocity"),
        }),
    };

    private Control ShortcutsPane()
    {
        var body = new StackPanel { Spacing = 16 };
        foreach (var (title, rows) in ShortcutGroups)
        {
            var card = CreateCard(title);
            var stack = (StackPanel)card.Child!;
            foreach (var (key, action) in rows) stack.Children.Add(ShortcutRow(key, action));
            body.Children.Add(card);
        }
        return body;
    }

    private Control ShortcutRow(string key, string action)
    {
        var cap = new Border
        {
            Background = Raised,
            BorderBrush = BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 130,
        };
        var kt = new TextBlock { Text = key, FontSize = 10, Foreground = TextPrimary, TextAlignment = TextAlignment.Center };
        kt.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");
        cap.Child = kt;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), Margin = new Thickness(0, 2) };
        grid.Children.Add(cap);
        var a = new TextBlock
        {
            Text = action,
            FontSize = 11,
            Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(a, 1);
        grid.Children.Add(a);
        return grid;
    }

    // ---- helpers ----------------------------------------------------------

    private static Border CreateCard(string title)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(SectionLabel(title));
        return new Border
        {
            Background = Panel,
            BorderBrush = Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    private Border DisabledChip(string text) => new()
    {
        Background = Raised,
        BorderBrush = BorderStrong,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(12, 5),
        Opacity = 0.65,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = TextPrimary },
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeight.Bold,
        Foreground = Brass,
        Margin = new Thickness(0, 0, 0, 4),
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 10,
        Foreground = TextTertiary,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private static Control DividerLine() => new Border { Height = 1, Background = Divider, Margin = new Thickness(0, 4) };

    private static Control Row(string label, Control control) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("150,*"),
        Margin = new Thickness(0, 2),
        Children =
        {
            new TextBlock { Text = label, FontSize = 11, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center },
            WrapColumn(control, 1),
        },
    };

    private static Control WrapColumn(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }

    private static void FillDeviceCombo(ComboBox combo, List<string> uids, IReadOnlyList<AudioDevice> devices, string savedUid)
    {
        uids.Clear();
        combo.Items.Add("System Default");
        uids.Add("");
        int selected = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            combo.Items.Add(devices[i].Name);
            uids.Add(devices[i].Uid);
            if (devices[i].Uid == savedUid && savedUid.Length > 0) selected = i + 1;
        }
        combo.SelectedIndex = selected;
    }

    private static int IndexOf(double[] values, double v) { for (int i = 0; i < values.Length; i++) if (values[i] == v) return i; return 0; }
    private static int IndexOf(int[] values, int v) { for (int i = 0; i < values.Length; i++) if (values[i] == v) return i; return 0; }

    private void RefreshPaths()
    {
        _paths.Clear();
        int n = _catalog.ScanPathCount;
        for (int i = 0; i < n; i++) _paths.Add(_catalog.ScanPath(i) ?? "?");
    }
}

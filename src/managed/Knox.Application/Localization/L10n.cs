// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Knox.Application;

public static class L10n
{
    private static readonly Dictionary<string, string> Strings = new(StringComparer.OrdinalIgnoreCase);
    private static string _currentLanguage = "en";

    public static string CurrentLanguage => _currentLanguage;
    public static event Action? LanguageChanged;

    public static (string Code, string DisplayName)[] AvailableLanguages
    {
        get
        {
            var list = new List<(string Code, string DisplayName)>
            {
                ("en", "English (US)"),
                ("tr", "Türkçe (Turkish)"),
            };

            // Scan locale folders for custom user JSON language files
            var localesDir = GetLocalesDirectory();
            if (Directory.Exists(localesDir))
            {
                foreach (var file in Directory.GetFiles(localesDir, "*.json"))
                {
                    var code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (list.Any(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string displayName = code.ToUpperInvariant();
                    try
                    {
                        var json = File.ReadAllText(file);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("_DisplayName", out var nameProp))
                        {
                            var val = nameProp.GetString();
                            if (!string.IsNullOrWhiteSpace(val))
                                displayName = val;
                        }
                    }
                    catch
                    {
                        displayName = $"{code.ToUpperInvariant()} (Custom)";
                    }

                    list.Add((code, displayName));
                }
            }

            return list.ToArray();
        }
    }

    static L10n()
    {
        LoadDefaultEnglish();
    }

    public static string GetLocalesDirectory()
    {
        // 1. Check next to executable / base directory
        var baseDir = AppContext.BaseDirectory;
        var appLocales = Path.Combine(baseDir, "Locales");
        if (Directory.Exists(appLocales)) return appLocales;

        // 2. Check parent directory (for dev run / tests)
        var parentLocales = Path.Combine(baseDir, "..", "Locales");
        if (Directory.Exists(parentLocales)) return Path.GetFullPath(parentLocales);

        // 3. Check AppData folder
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Knox Studio", "Locales");
        if (!Directory.Exists(appData))
        {
            try { Directory.CreateDirectory(appData); } catch { }
        }
        return Directory.Exists(appLocales) ? appLocales : appData;
    }

    public static void EnsureDefaultLocaleFiles(string? directory = null)
    {
        var dir = directory ?? GetLocalesDirectory();
        try
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var trFile = Path.Combine(dir, "tr.json");
            if (!File.Exists(trFile))
            {
                var trDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                trDict["_DisplayName"] = "Türkçe (Turkish)";
                var prevStrings = new Dictionary<string, string>(Strings);
                LoadDefaultEnglish();
                foreach (var kv in Strings) trDict[kv.Key] = kv.Value;
                LoadDefaultTurkish();
                foreach (var kv in Strings) trDict[kv.Key] = kv.Value;
                Strings.Clear();
                foreach (var kv in prevStrings) Strings[kv.Key] = kv.Value;

                var opt = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(trFile, JsonSerializer.Serialize(trDict, opt));
            }

            var enFile = Path.Combine(dir, "en.json");
            if (!File.Exists(enFile))
            {
                var enDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                enDict["_DisplayName"] = "English (US)";
                var prevStrings = new Dictionary<string, string>(Strings);
                LoadDefaultEnglish();
                foreach (var kv in Strings) enDict[kv.Key] = kv.Value;
                Strings.Clear();
                foreach (var kv in prevStrings) Strings[kv.Key] = kv.Value;

                var opt = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(enFile, JsonSerializer.Serialize(enDict, opt));
            }
        }
        catch
        {
            // Ignore filesystem permissions in restricted environments
        }
    }

    public static void SetLanguage(string langCode, string? localesDirectory = null)
    {
        _currentLanguage = string.IsNullOrWhiteSpace(langCode) ? "en" : langCode.ToLowerInvariant();
        Strings.Clear();

        // Always seed with English defaults first so any missing translation key falls back
        LoadDefaultEnglish();

        if (_currentLanguage == "tr")
        {
            LoadDefaultTurkish();
        }

        // Check if there is a custom or override locale JSON file on disk
        var dir = localesDirectory ?? GetLocalesDirectory();
        EnsureDefaultLocaleFiles(dir);
        if (Directory.Exists(dir))
        {
            var file = Path.Combine(dir, $"{_currentLanguage}.json");
            if (File.Exists(file))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                        {
                            if (!kv.Key.StartsWith("_"))
                            {
                                Strings[kv.Key] = kv.Value;
                            }
                        }
                    }
                }
                catch
                {
                    // Fall back to embedded
                }
            }
        }

        LanguageChanged?.Invoke();
    }

    public static string Tr(string key, string? fallback = null)
    {
        if (Strings.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
        {
            return val;
        }
        return fallback ?? key;
    }

    public static string T(string key, string fallback) => Tr(key, fallback);

    public static Dictionary<string, string> GetCurrentDictionary()
    {
        return new Dictionary<string, string>(Strings, StringComparer.OrdinalIgnoreCase);
    }

    public static void LoadDefaultEnglish()
    {
        // General & Transport
        Strings["App.Title"] = "Knox";
        Strings["App.Untitled"] = "Knox — Untitled";
        Strings["Transport.Play"] = "Play";
        Strings["Transport.Stop"] = "Stop";
        Strings["Transport.Record"] = "Record";
        Strings["Transport.Loop"] = "Loop / Cycle";
        Strings["Transport.Metronome"] = "Metronome";
        Strings["Transport.Tempo"] = "TEMPO";
        Strings["Transport.TimeSig"] = "SIGNATURE";
        Strings["Transport.Key"] = "KEY";
        Strings["Transport.BarBeat"] = "BAR · BEAT";
        Strings["Transport.Quantize"] = "QUANTIZE";
        Strings["Transport.Follow"] = "Follow Playhead";
        Strings["Transport.TapTempo"] = "TAP";
        Strings["Transport.Master"] = "MASTER";
        Strings["Transport.Cpu"] = "CPU";
        Strings["Transport.Dsp"] = "DSP";
        Strings["Transport.Rewind"] = "Return to Zero";
        Strings["Transport.RewindTip"] = "Return to Zero (Return)";
        Strings["Transport.StopTip"] = "Stop (Space / Return)";
        Strings["Transport.PlayTip"] = "Play (Space)";
        Strings["Transport.RecordTip"] = "Record (R)";
        Strings["Transport.MetroTip"] = "Metronome (M)";
        Strings["Transport.FollowTip"] = "Follow playhead";
        Strings["Transport.LoopTip"] = "Cycle / Loop (Cmd+L)";
        Strings["Transport.CountIn"] = "Count-in (1, 2, 3)";
        Strings["Transport.CountInTip"] = "Count-in (1,2,3,4) — Right-click for options";
        Strings["Transport.CountInOptions"] = "Count-in Options";
        Strings["Transport.CountInRecord"] = "Count-in on Record";
        Strings["Transport.CountInPlayback"] = "Count-in on Playback";
        Strings["Transport.CountIn1Bar"] = "1 Bar (4 Beats)";
        Strings["Transport.CountIn2Bars"] = "2 Bars (8 Beats)";
        Strings["Transport.CountInBeat"] = "Count-in: {0} / {1}";
        Strings["Transport.LcdPosTip"] = "Click to switch between bars and time";
        Strings["Transport.LcdTempoTip"] = "Drag ↕ or double-click to set tempo";
        Strings["Transport.LcdTapTip"] = "Tap tempo to match beat";
        Strings["Transport.LcdSigTip"] = "Drag ↕ to set time signature";
        Strings["Transport.LcdKeyTip"] = "Project Key signature";
        Strings["Transport.LcdQTip"] = "Launch quantize (Session)";
        Strings["Transport.MixerToggleTip"] = "Toggle Mixer console (Cmd+M)";
        Strings["Transport.MidiLearnTip"] = "MIDI Learn — highlight mappable controls, click one, then move a knob on your controller";

        // Menu items
        Strings["Menu.File"] = "File";
        Strings["Menu.New"] = "New Project";
        Strings["Menu.Open"] = "Open Project…";
        Strings["Menu.Save"] = "Save";
        Strings["Menu.SaveAs"] = "Save As…";
        Strings["Menu.Import"] = "Import Audio…";
        Strings["Menu.Export"] = "Export Audio…";
        Strings["Menu.Preferences"] = "Preferences…";
        Strings["Menu.Settings"] = "Settings…";
        Strings["Menu.Exit"] = "Exit";

        Strings["Menu.Edit"] = "Edit";
        Strings["Menu.Undo"] = "Undo";
        Strings["Menu.Redo"] = "Redo";
        Strings["Menu.Cut"] = "Cut";
        Strings["Menu.Copy"] = "Copy";
        Strings["Menu.Paste"] = "Paste";
        Strings["Menu.Delete"] = "Delete";
        Strings["Menu.Duplicate"] = "Duplicate";
        Strings["Menu.DuplicateClip"] = "Duplicate Clip";
        Strings["Menu.Split"] = "Split at Playhead";
        Strings["Menu.SelectAll"] = "Select All";
        Strings["Menu.LockEnvelopes"] = "Lock Envelopes";

        Strings["Menu.Track"] = "Track";
        Strings["Menu.AddAudioTrack"] = "Add Audio Track";
        Strings["Menu.AddInstrumentTrack"] = "Add Instrument Track";
        Strings["Menu.AddReturnTrack"] = "Add Return Track";
        Strings["Menu.DuplicateTrack"] = "Duplicate Track";
        Strings["Menu.DeleteTrack"] = "Delete Track";
        Strings["Menu.FreezeTrack"] = "Freeze Track";
        Strings["Menu.FlattenTrack"] = "Flatten Track";
        Strings["Menu.GroupTracks"] = "Group Tracks";
        Strings["Menu.UngroupTracks"] = "Ungroup Tracks";

        Strings["Menu.View"] = "View";
        Strings["Menu.Arrangement"] = "Arrangement";
        Strings["Menu.Session"] = "Session";
        Strings["Menu.Modular"] = "Modular";
        Strings["Menu.Mixer"] = "Mixer";
        Strings["Menu.Browser"] = "Browser";
        Strings["Menu.Detail"] = "Device & Clip Detail";
        Strings["Menu.ToggleBrowser"] = "Toggle Browser";
        Strings["Menu.ToggleDetail"] = "Toggle Detail Panel";

        Strings["Menu.Transport"] = "Transport";
        Strings["Menu.PlayStop"] = "Play / Stop";
        Strings["Menu.Record"] = "Record";
        Strings["Menu.Loop"] = "Loop";
        Strings["Menu.Metronome"] = "Metronome";

        Strings["Menu.Help"] = "Help";
        Strings["Menu.WhatsNew"] = "What's New";
        Strings["Menu.About"] = "About Knox";
        Strings["Menu.Shortcuts"] = "Keyboard Shortcuts";

        // Tools & Toolbar
        Strings["Tool.Arrangement"] = "Arrangement";
        Strings["Tool.Session"] = "Session";
        Strings["Tool.Modular"] = "Modular";
        Strings["Tool.Pointer"] = "Pointer / Selection Tool (1)";
        Strings["Tool.Pencil"] = "Pencil / Draw Tool (2)";
        Strings["Tool.Scissors"] = "Scissors / Split Tool (3)";
        Strings["Tool.Eraser"] = "Eraser / Delete Tool (4)";
        Strings["Tool.Stretch"] = "Stretch / Time-Warp Tool (5)";
        Strings["Tool.Snap"] = "Snap";
        Strings["Tool.SnapTip"] = "Clip snap grid";
        Strings["Tool.Automation"] = "Automation";
        Strings["Tool.AutomationTip"] = "Show parameter-automation lanes";
        Strings["Tool.AutoRead"] = "Read";
        Strings["Tool.AutoTouch"] = "Touch";
        Strings["Tool.AutoLatch"] = "Latch";
        Strings["Tool.AutoWrite"] = "Write";

        // Detail panel
        Strings["Detail.Mixer"] = "Mixer";
        Strings["Detail.Devices"] = "Devices";
        Strings["Detail.Clip"] = "Clip";
        Strings["Detail.ClipTip"] = "Select a clip to edit";
        Strings["Detail.Freeze"] = "Freeze";
        Strings["Detail.FreezeTip"] = "Freeze track — bounce it to audio to free up CPU (right-click for Flatten)";
        Strings["Detail.LiveFreeze"] = "Live Freeze";
        Strings["Detail.LiveFreezeTip"] = "Live Freeze — bounce to a linked audio track below and sleep the source (Edit later to change it)";
        Strings["Detail.Edit"] = "Edit";
        Strings["Detail.EditTip"] = "Edit the frozen source — wakes it so you can change notes/devices (right-click: Unfreeze / Flatten)";
        Strings["Detail.Done"] = "Done";
        Strings["Detail.DoneTip"] = "Done — re-freeze the source and put it back to sleep";
        Strings["Detail.DiscardTip"] = "Discard — leave edit mode without re-freezing";
        Strings["Detail.PopOutTip"] = "Open Devices + Clip in a separate window";
        Strings["Detail.CloseTip"] = "Close detail panel";

        // Browser
        Strings["Browser.InstrTab"] = "Instr";
        Strings["Browser.FxTab"] = "FX";
        Strings["Browser.MidiTab"] = "MIDI";
        Strings["Browser.FilesTab"] = "Files";
        Strings["Browser.PresetTab"] = "Preset";
        Strings["Browser.ProjTab"] = "Proj";
        Strings["Browser.MapTab"] = "Map";
        Strings["Browser.SearchWatermark"] = "Search";
        Strings["Browser.Favorites"] = "Favorites";
        Strings["Browser.AutoAudition"] = "Auto";
        Strings["Browser.EmptyInstr"] = "No instruments found.\nDrag VST3/AU plug-ins here or scan in Settings.";
        Strings["Browser.EmptyFx"] = "No audio effects found.\nScan for plug-ins in Settings ▸ Plug-ins.";
        Strings["Browser.EmptyMidi"] = "No MIDI effects found.";
        Strings["Browser.EmptyFiles"] = "No audio files or samples found in sample directories.";
        Strings["Browser.EmptyPreset"] = "No user presets found.\nSave presets from device headers.";
        Strings["Browser.EmptyProj"] = "No projects found in project folder.";
        Strings["Browser.EmptyMap"] = "No MIDI mappings configured.";
        Strings["Browser.Reveal"] = "Reveal in File Manager";
        Strings["Browser.DeleteProj"] = "Delete Project…";
        Strings["Browser.EditTags"] = "Edit Tags…";
        Strings["Browser.AddFav"] = "Add to Favorites";
        Strings["Browser.RemoveFav"] = "Remove from Favorites";
        Strings["Browser.ItemsCount"] = "{0} items";

        // Mixer & Strips
        Strings["Mixer.Title"] = "Mixer Console";
        Strings["Mixer.Master"] = "Master";
        Strings["Mixer.StereoOut"] = "Stereo Out";
        Strings["Mixer.Sends"] = "Sends";
        Strings["Mixer.AudioFx"] = "Audio FX";
        Strings["Mixer.Io"] = "I/O";
        Strings["Mixer.Pan"] = "PAN";
        Strings["Mixer.MidiIn"] = "MIDI In";
        Strings["Mixer.Mute"] = "Mute";
        Strings["Mixer.Solo"] = "Solo";
        Strings["Mixer.Arm"] = "Record Arm";
        Strings["Mixer.PopOut"] = "Detach Window";
        Strings["Mixer.Show"] = "Show";

        // Track & Arrangement Context
        Strings["Track.Audio"] = "Audio Track";
        Strings["Track.Instrument"] = "Instrument Track";
        Strings["Track.Return"] = "Return Track";
        Strings["Track.Group"] = "Track Group";
        Strings["Track.Rename"] = "Rename Track…";
        Strings["Track.Color"] = "Change Color…";
        Strings["Track.GroupSelected"] = "Group Selected Tracks";
        Strings["Track.Ungroup"] = "Ungroup";

        // Piano Roll & Clip Editor
        Strings["Clip.Title"] = "Clip Editor";
        Strings["Clip.Length"] = "Length";
        Strings["Clip.Velocity"] = "Velocity";
        Strings["Clip.Quantize"] = "Quantize";
        Strings["Clip.Legato"] = "Legato";
        Strings["Clip.Transpose"] = "Transpose";
        Strings["Clip.Scale"] = "Scale";
        Strings["Clip.Snap"] = "Snap";
        Strings["Clip.Warp"] = "Warp";
        Strings["Clip.Gain"] = "Gain";
        Strings["Clip.Reverse"] = "Reverse";
        Strings["Clip.Fold"] = "Fold (Key only)";
        Strings["Clip.SliceToDrum"] = "Slice to Drum Rack";
        Strings["Clip.ConvertAudioToMidi"] = "Convert Audio to MIDI";
        Strings["Clip.Normalize"] = "Normalize";

        // Preferences Sections
        Strings["Pref.Title"] = "Preferences";
        Strings["Pref.General"] = "General";
        Strings["Pref.Audio"] = "Audio";
        Strings["Pref.MIDI"] = "MIDI";
        Strings["Pref.Editing"] = "Editing";
        Strings["Pref.Gamepads"] = "Gamepads";
        Strings["Pref.Plugins"] = "Plug-ins";
        Strings["Pref.Library"] = "Library";
        Strings["Pref.Appearance"] = "Appearance";
        Strings["Pref.Shortcuts"] = "Shortcuts";

        // Preferences Controls
        Strings["Pref.Card.Lang"] = "LANGUAGE & LOCALIZATION";
        Strings["Pref.Card.Startup"] = "STARTUP & PROJECT DEFAULTS";
        Strings["Pref.Card.AudioDevice"] = "AUDIO DEVICE & BUFFERING";
        Strings["Pref.Card.Asio"] = "HARDWARE ASIO DRIVER";
        Strings["Pref.Card.Diagnostics"] = "DIAGNOSTICS & HARDWARE TEST";
        Strings["Pref.Card.MidiInputs"] = "ACTIVE MIDI CONTROLLERS & INPUTS";
        Strings["Pref.Card.Timeline"] = "TIMELINE & CLIP EDITING";
        Strings["Pref.Card.Gamepad"] = "CONNECTED GAMEPADS / JOYSTICKS";
        Strings["Pref.Card.VstPaths"] = "VST3 / AUDIO UNIT LOCATIONS";
        Strings["Pref.Card.SampleDirs"] = "SAMPLE & PROJECT DIRECTORIES";
        Strings["Pref.Card.Theme"] = "THEME & VISUAL CUSTOMIZATION";
        Strings["Pref.Card.Keybindings"] = "KEYBOARD SHORTCUTS";

        Strings["Pref.Startup"] = "Startup & Launch Action";
        Strings["Pref.Startup.Welcome"] = "Show Welcome Dialog";
        Strings["Pref.Startup.NewProject"] = "Create New Blank Project";
        Strings["Pref.Startup.LastProject"] = "Reopen Last Modified Project";
        Strings["Pref.Language"] = "Language / Dil";
        Strings["Pref.LanguageHelp"] = "Choose your preferred interface language. Additional translations can be added to the /Locales folder as .json files.";
        Strings["Pref.DefaultTempo"] = "Default Tempo";
        Strings["Pref.DefaultTimeSig"] = "Default Time Signature";
        Strings["Pref.CountIn"] = "Recording Count-in";
        Strings["Pref.AutoSave"] = "Auto-Save Interval";
        Strings["Pref.AudioDriver"] = "Audio Driver Type";
        Strings["Pref.OutputDevice"] = "Output Device";
        Strings["Pref.InputDevice"] = "Input Device";
        Strings["Pref.SampleRate"] = "Sample Rate";
        Strings["Pref.BufferSize"] = "Buffer Size";
        Strings["Pref.WasapiExclusive"] = "WASAPI Exclusive Mode";
        Strings["Pref.AsioControlPanel"] = "Open ASIO Control Panel";
        Strings["Pref.EngineStatus"] = "Engine Status";
        Strings["Pref.TestTone"] = "440Hz Sine Tone";
        Strings["Pref.StressTest"] = "Engine Stress Test";
        Strings["Pref.StressRunning"] = "Running DSP benchmark…";
        Strings["Pref.StressPassed"] = "Passed: {0:0.0}x Real-time DSP ({1:0}ms) · 0 dropouts";
        Strings["Pref.StressFailed"] = "Warning: Engine test";
        Strings["Pref.TestCaption"] = "Generate a pure 440 Hz test tone or run an audio engine stress test to verify latency, driver stability, and DSP headroom.";
        Strings["Pref.Theme"] = "Base Theme";
        Strings["Pref.AccentColor"] = "Accent Color";
        Strings["Pref.KnobStyle"] = "Knob Rendering Style";
        Strings["Pref.GridContrast"] = "Timeline Grid Contrast";
        Strings["Pref.GlowEffects"] = "Illuminated LED Gauges & Accents";
        Strings["Pref.MeterFps"] = "Peak Meter Refresh Rate";
        Strings["Pref.Card.ArrangementGrid"] = "ARRANGEMENT GRID & BACKGROUND";
        Strings["Pref.GridColor"] = "Grid Background Color";
        Strings["Pref.GridCustomColor"] = "Custom Hex Color";
        Strings["Pref.BgImage"] = "Background Image";
        Strings["Pref.BgImageOpacity"] = "Background Image Opacity";
        Strings["Pref.BgImageMode"] = "Image Layout Mode";
        Strings["Pref.BrowseImage"] = "Browse…";
        Strings["Pref.ClearImage"] = "Clear";
        Strings["Pref.Card.TrackClipStyle"] = "AUDIO TRACK & CLIP STYLING";
        Strings["Pref.ClipShape"] = "Clip Corner Shape";
        Strings["Pref.ClipInterior"] = "Clip Interior Style";
        Strings["Pref.ClipDarkText"] = "Black Track Title & Waveform";
        Strings["Pref.ScanPlugins"] = "Scan Plug-ins";
        Strings["Pref.Rescan"] = "Rescan All";

        // Dialogs & Actions
        Strings["Dialog.Ok"] = "OK";
        Strings["Dialog.Cancel"] = "Cancel";
        Strings["Dialog.Save"] = "Save";
        Strings["Dialog.Discard"] = "Discard";
        Strings["Dialog.Apply"] = "Apply";
        Strings["Dialog.Yes"] = "Yes";
        Strings["Dialog.No"] = "No";
        Strings["Dialog.Confirm"] = "Confirmation";
        Strings["Discord.Details"] = "In the studio";
        Strings["Discord.State"] = "Cooking up something new...";
    }

    public static void LoadDefaultTurkish()
    {
        // General & Transport
        Strings["App.Title"] = "Knox";
        Strings["App.Untitled"] = "Knox — İsimsiz Proje";
        Strings["Transport.Play"] = "Oynat";
        Strings["Transport.Stop"] = "Durdur";
        Strings["Transport.Record"] = "Kayıt";
        Strings["Transport.Loop"] = "Döngü / Loop";
        Strings["Transport.Metronome"] = "Metronom";
        Strings["Transport.Tempo"] = "TEMPO";
        Strings["Transport.TimeSig"] = "ÖLÇÜ";
        Strings["Transport.Key"] = "GAM / TON";
        Strings["Transport.BarBeat"] = "MEZÜR · VURUŞ";
        Strings["Transport.Quantize"] = "KUANTİZE";
        Strings["Transport.Follow"] = "Oynatma Kafasını Takip Et";
        Strings["Transport.TapTempo"] = "DOKUN";
        Strings["Transport.Master"] = "ANA ÇIKIŞ";
        Strings["Transport.Cpu"] = "İŞLEMCİ";
        Strings["Transport.Dsp"] = "DSP";
        Strings["Transport.Rewind"] = "Başa Sar (Sıfırla)";
        Strings["Transport.RewindTip"] = "Başa Sar (Return)";
        Strings["Transport.StopTip"] = "Durdur (Boşluk / Return)";
        Strings["Transport.PlayTip"] = "Oynat (Boşluk)";
        Strings["Transport.RecordTip"] = "Kayıt (R)";
        Strings["Transport.MetroTip"] = "Metronom (M)";
        Strings["Transport.FollowTip"] = "Oynatma kafasını takip et";
        Strings["Transport.LoopTip"] = "Döngü / Loop (Cmd+L)";
        Strings["Transport.CountIn"] = "Sayım (1, 2, 3)";
        Strings["Transport.CountInTip"] = "Sayım (1,2,3,4) — Seçenekler için sağ tıklayın";
        Strings["Transport.CountInOptions"] = "Sayım Seçenekleri";
        Strings["Transport.CountInRecord"] = "Kayıt Öncesi Sayım";
        Strings["Transport.CountInPlayback"] = "Oynatma Öncesi Sayım";
        Strings["Transport.CountIn1Bar"] = "1 Ölçü (4 Vuruş)";
        Strings["Transport.CountIn2Bars"] = "2 Ölçü (8 Vuruş)";
        Strings["Transport.CountInBeat"] = "Sayım: {0} / {1}";
        Strings["Transport.LcdPosTip"] = "Mezür ve zaman gösterimi arasında geçiş yapmak için tıklayın";
        Strings["Transport.LcdTempoTip"] = "Tempoyu ayarlamak için yukarı/aşağı sürükleyin veya çift tıklayın";
        Strings["Transport.LcdTapTip"] = "Ritme göre tempo belirlemek için dokunun (Tap Tempo)";
        Strings["Transport.LcdSigTip"] = "Zaman işaretini (ölçüyü) ayarlamak için sürükleyin";
        Strings["Transport.LcdKeyTip"] = "Proje Gam / Ton işareti";
        Strings["Transport.LcdQTip"] = "Başlatma kuantizasyonu (Oturum Modu)";
        Strings["Transport.MixerToggleTip"] = "Mikser konsolunu aç/kapat (Cmd+M)";
        Strings["Transport.MidiLearnTip"] = "MIDI Öğren — atanabilir kontrolleri vurgulayın, birine tıklayın ve klavyenizdeki bir düğmeyi çevirin";

        // Menu items
        Strings["Menu.File"] = "Dosya";
        Strings["Menu.New"] = "Yeni Proje";
        Strings["Menu.Open"] = "Proje Aç…";
        Strings["Menu.Save"] = "Kaydet";
        Strings["Menu.SaveAs"] = "Farklı Kaydet…";
        Strings["Menu.Import"] = "Ses İçe Aktar…";
        Strings["Menu.Export"] = "Ses Dışa Aktar…";
        Strings["Menu.Preferences"] = "Tercihler / Ayarlar…";
        Strings["Menu.Settings"] = "Ayarlar…";
        Strings["Menu.Exit"] = "Çıkış";

        Strings["Menu.Edit"] = "Düzenle";
        Strings["Menu.Undo"] = "Geri Al";
        Strings["Menu.Redo"] = "Yinele";
        Strings["Menu.Cut"] = "Kes";
        Strings["Menu.Copy"] = "Kopyala";
        Strings["Menu.Paste"] = "Yapıştır";
        Strings["Menu.Delete"] = "Sil";
        Strings["Menu.Duplicate"] = "Çoğalt";
        Strings["Menu.DuplicateClip"] = "Klibi Çoğalt";
        Strings["Menu.Split"] = "Oynatma Çizgisinden Böl";
        Strings["Menu.SelectAll"] = "Tümünü Seç";
        Strings["Menu.LockEnvelopes"] = "Zarf / Otomasyonları Kilitle";

        Strings["Menu.Track"] = "Kanal / İz";
        Strings["Menu.AddAudioTrack"] = "Ses Kanalı Ekle";
        Strings["Menu.AddInstrumentTrack"] = "Enstrüman Kanalı Ekle";
        Strings["Menu.AddReturnTrack"] = "Return / FX Kanalı Ekle";
        Strings["Menu.DuplicateTrack"] = "Kanalı Çoğalt";
        Strings["Menu.DeleteTrack"] = "Kanalı Sil";
        Strings["Menu.FreezeTrack"] = "Kanalı Dondur (Freeze)";
        Strings["Menu.FlattenTrack"] = "Kanalı Bütünleştir (Flatten)";
        Strings["Menu.GroupTracks"] = "Kanalları Grupla";
        Strings["Menu.UngroupTracks"] = "Grup Çöz";

        Strings["Menu.View"] = "Görünüm";
        Strings["Menu.Arrangement"] = "Düzenleme";
        Strings["Menu.Session"] = "Oturum";
        Strings["Menu.Modular"] = "Modüler";
        Strings["Menu.Mixer"] = "Mikser";
        Strings["Menu.Browser"] = "Dosya Tarayıcı";
        Strings["Menu.Detail"] = "Cihaz ve Klip Detayı";
        Strings["Menu.ToggleBrowser"] = "Tarayıcıyı Aç/Kapat";
        Strings["Menu.ToggleDetail"] = "Detay Panelini Aç/Kapat";

        Strings["Menu.Transport"] = "Oynatma / Transport";
        Strings["Menu.PlayStop"] = "Oynat / Durdur";
        Strings["Menu.Record"] = "Kayıt";
        Strings["Menu.Loop"] = "Döngü";
        Strings["Menu.Metronome"] = "Metronom";

        Strings["Menu.Help"] = "Yardım";
        Strings["Menu.WhatsNew"] = "Yenilikler";
        Strings["Menu.About"] = "Knox Hakkında";
        Strings["Menu.Shortcuts"] = "Klavye Kısayolları";

        // Tools & Toolbar
        Strings["Tool.Arrangement"] = "Düzenleme";
        Strings["Tool.Session"] = "Oturum";
        Strings["Tool.Modular"] = "Modüler";
        Strings["Tool.Pointer"] = "İşaretçi / Seçim Aracı (1)";
        Strings["Tool.Pencil"] = "Kalem / Çizim Aracı (2)";
        Strings["Tool.Scissors"] = "Makas / Bölme Aracı (3)";
        Strings["Tool.Eraser"] = "Silgi / Silme Aracı (4)";
        Strings["Tool.Stretch"] = "Esnetme / Zaman Bükme Aracı (5)";
        Strings["Tool.Snap"] = "Hizalama";
        Strings["Tool.SnapTip"] = "Klip hizalama ızgarası";
        Strings["Tool.Automation"] = "Otomasyon";
        Strings["Tool.AutomationTip"] = "Parametre otomasyon kanallarını göster";
        Strings["Tool.AutoRead"] = "Oku";
        Strings["Tool.AutoTouch"] = "Dokun";
        Strings["Tool.AutoLatch"] = "Kilitle";
        Strings["Tool.AutoWrite"] = "Yaz";

        // Detail panel
        Strings["Detail.Mixer"] = "Mikser";
        Strings["Detail.Devices"] = "Cihazlar";
        Strings["Detail.Clip"] = "Klip";
        Strings["Detail.ClipTip"] = "Düzenlemek için bir klip seçin";
        Strings["Detail.Freeze"] = "Dondur";
        Strings["Detail.FreezeTip"] = "Kanalı dondur — CPU tasarrufu için sese dönüştür (Sağ tık: Bütünleştir)";
        Strings["Detail.LiveFreeze"] = "Canlı Dondur";
        Strings["Detail.LiveFreezeTip"] = "Canlı Dondur — alttaki bağlı ses kanalına dök ve kaynağı uykuya al (Düzenlemek için tekrar açabilirsiniz)";
        Strings["Detail.Edit"] = "Düzenle";
        Strings["Detail.EditTip"] = "Dondurulmuş kaynağı düzenle — notaları ve cihazları değiştirmek için uyandırır";
        Strings["Detail.Done"] = "Tamam";
        Strings["Detail.DoneTip"] = "Tamam — kaynağı yeniden dondur ve uyku moduna al";
        Strings["Detail.DiscardTip"] = "Vazgeç — dondurmadan düzenleme modundan çık";
        Strings["Detail.PopOutTip"] = "Cihazlar + Klip panelini ayrı pencerede aç";
        Strings["Detail.CloseTip"] = "Detay panelini kapat";

        // Browser
        Strings["Browser.InstrTab"] = "Enstrüman";
        Strings["Browser.FxTab"] = "Efekt";
        Strings["Browser.MidiTab"] = "MIDI";
        Strings["Browser.FilesTab"] = "Dosyalar";
        Strings["Browser.PresetTab"] = "Ön Tanım";
        Strings["Browser.ProjTab"] = "Projeler";
        Strings["Browser.MapTab"] = "Eşleme";
        Strings["Browser.SearchWatermark"] = "Ara";
        Strings["Browser.Favorites"] = "Favoriler";
        Strings["Browser.AutoAudition"] = "Oto";
        Strings["Browser.EmptyInstr"] = "Enstrüman bulunamadı.\nVST3/AU eklentilerini buraya sürükleyin veya Ayarlar'dan tarayın.";
        Strings["Browser.EmptyFx"] = "Ses efekti bulunamadı.\nAyarlar ▸ Eklentiler sekmesinden tarama yapın.";
        Strings["Browser.EmptyMidi"] = "MIDI efekti bulunamadı.";
        Strings["Browser.EmptyFiles"] = "Örnek ses klasörlerinde ses dosyası bulunamadı.";
        Strings["Browser.EmptyPreset"] = "Kullanıcı ön tanımı (preset) bulunamadı.\nCihaz başlığından preset kaydedebilirsiniz.";
        Strings["Browser.EmptyProj"] = "Proje klasöründe kayıtlı proje bulunamadı.";
        Strings["Browser.EmptyMap"] = "Yapılandırılmış MIDI eşlemesi bulunamadı.";
        Strings["Browser.Reveal"] = "Dosya Yöneticisinde Göster";
        Strings["Browser.DeleteProj"] = "Projeyi Sil…";
        Strings["Browser.EditTags"] = "Etiketleri Düzenle…";
        Strings["Browser.AddFav"] = "Favorilere Ekle";
        Strings["Browser.RemoveFav"] = "Favorilerden Çıkar";
        Strings["Browser.ItemsCount"] = "{0} öge";

        // Mixer & Strips
        Strings["Mixer.Title"] = "Mikser Konsolu";
        Strings["Mixer.Master"] = "Ana Çıkış (Master)";
        Strings["Mixer.StereoOut"] = "Stereo Çıkış";
        Strings["Mixer.Sends"] = "Gönderimler (Sends)";
        Strings["Mixer.AudioFx"] = "Ses Efektleri (FX)";
        Strings["Mixer.Io"] = "Giriş / Çıkış (I/O)";
        Strings["Mixer.Pan"] = "PAN";
        Strings["Mixer.MidiIn"] = "MIDI Girişi";
        Strings["Mixer.Mute"] = "Sessize Al (Mute)";
        Strings["Mixer.Solo"] = "Solo";
        Strings["Mixer.Arm"] = "Kayıt Hazır (Arm)";
        Strings["Mixer.PopOut"] = "Ayrı Pencerede Aç";
        Strings["Mixer.Show"] = "Göster";

        // Track & Arrangement Context
        Strings["Track.Audio"] = "Ses Kanalı";
        Strings["Track.Instrument"] = "Enstrüman Kanalı";
        Strings["Track.Return"] = "Return / FX Kanalı";
        Strings["Track.Group"] = "Kanal Grubu";
        Strings["Track.Rename"] = "Kanalı Yeniden Adlandır…";
        Strings["Track.Color"] = "Rengi Değiştir…";
        Strings["Track.GroupSelected"] = "Seçili Kanalları Grupla";
        Strings["Track.Ungroup"] = "Grubu Çöz";

        // Piano Roll & Clip Editor
        Strings["Clip.Title"] = "Klip Düzenleyici";
        Strings["Clip.Length"] = "Uzunluk";
        Strings["Clip.Velocity"] = "Tuş Şiddeti (Velocity)";
        Strings["Clip.Quantize"] = "Kuantize Et";
        Strings["Clip.Legato"] = "Legato";
        Strings["Clip.Transpose"] = "Transpoze";
        Strings["Clip.Scale"] = "Dizi / Gam";
        Strings["Clip.Snap"] = "Hizalama (Snap)";
        Strings["Clip.Warp"] = "Zaman Bükme (Warp)";
        Strings["Clip.Gain"] = "Kazanç (Gain)";
        Strings["Clip.Reverse"] = "Ters Çevir";
        Strings["Clip.Fold"] = "Katla (Sadece Gam Notaları)";
        Strings["Clip.SliceToDrum"] = "Davul Rafına Dilimle (Slice to Drum Rack)";
        Strings["Clip.ConvertAudioToMidi"] = "Sesi MIDI'ye Dönüştür";
        Strings["Clip.Normalize"] = "Normalize Et";

        // Preferences Sections
        Strings["Pref.Title"] = "Ayarlar & Tercihler";
        Strings["Pref.General"] = "Genel";
        Strings["Pref.Audio"] = "Ses & Donanım";
        Strings["Pref.MIDI"] = "MIDI";
        Strings["Pref.Editing"] = "Düzenleme";
        Strings["Pref.Gamepads"] = "Oyun Kolları";
        Strings["Pref.Plugins"] = "Eklentiler (VST3/AU)";
        Strings["Pref.Library"] = "Kütüphane & Klasörler";
        Strings["Pref.Appearance"] = "Görünüm & Tema";
        Strings["Pref.Shortcuts"] = "Klavye Kısayolları";

        // Preferences Controls
        Strings["Pref.Card.Lang"] = "DİL VE YERELLEŞTİRME";
        Strings["Pref.Card.Startup"] = "BAŞLANGIÇ VE PROJE VARSAYILANLARI";
        Strings["Pref.Card.AudioDevice"] = "SES KARTI VE TAMPON AYARLARI";
        Strings["Pref.Card.Asio"] = "DONANIM ASIO SÜRÜCÜSÜ";
        Strings["Pref.Card.Diagnostics"] = "TANI VE DONANIM TESTLERİ";
        Strings["Pref.Card.MidiInputs"] = "AKTİF MIDI KLAVYELER VE GİRİŞLER";
        Strings["Pref.Card.Timeline"] = "ZAMAN ÇİZELGESİ VE KLİP DÜZENLEME";
        Strings["Pref.Card.Gamepad"] = "BAĞLI OYUN KOLLARI / JOYSTICKLER";
        Strings["Pref.Card.VstPaths"] = "VST3 / AUDIO UNIT KLASÖRLERİ";
        Strings["Pref.Card.SampleDirs"] = "ÖRNEK SES VE PROJE KLASÖRLERİ";
        Strings["Pref.Card.Theme"] = "TEMA VE GÖRSEL ÖZELLEŞTİRME";
        Strings["Pref.Card.Keybindings"] = "KLAVYE KISAYOLLARI";

        Strings["Pref.Startup"] = "Başlangıç Davranışı";
        Strings["Pref.Startup.Welcome"] = "Karşılama Penceresini Göster";
        Strings["Pref.Startup.NewProject"] = "Yeni Boş Proje Oluştur";
        Strings["Pref.Startup.LastProject"] = "Son Düzenlenen Projeyi Aç";
        Strings["Pref.Language"] = "Uygulama Dili";
        Strings["Pref.LanguageHelp"] = "Tercih ettiğiniz arayüz dilini seçin. /Locales klasörüne yeni .json dosyaları ekleyerek kendi çevirilerinizi de kullanabilirsiniz.";
        Strings["Pref.DefaultTempo"] = "Varsayılan Tempo";
        Strings["Pref.DefaultTimeSig"] = "Varsayılan Ölçü / Zaman İşareti";
        Strings["Pref.CountIn"] = "Kayıt Öncesi Sayma (Count-in)";
        Strings["Pref.AutoSave"] = "Otomatik Kaydetme Aralığı";
        Strings["Pref.AudioDriver"] = "Ses Sürücüsü Türü";
        Strings["Pref.OutputDevice"] = "Çıkış Cihazı";
        Strings["Pref.InputDevice"] = "Giriş Cihazı";
        Strings["Pref.SampleRate"] = "Örnekleme Frekansı (Sample Rate)";
        Strings["Pref.BufferSize"] = "Tampon Boyutu (Buffer Size)";
        Strings["Pref.WasapiExclusive"] = "WASAPI Exclusive Modu (En Düşük Gecikme)";
        Strings["Pref.AsioControlPanel"] = "ASIO Sürücü Panelini Aç";
        Strings["Pref.EngineStatus"] = "Ses Motoru Durumu";
        Strings["Pref.TestTone"] = "440Hz Test Sinyali";
        Strings["Pref.StressTest"] = "Motor Stres / DSP Testi";
        Strings["Pref.StressRunning"] = "DSP performans testi çalışıyor…";
        Strings["Pref.StressPassed"] = "Başarılı: {0:0.0}x Gerçek Zamanlı DSP ({1:0}ms) · 0 kesinti";
        Strings["Pref.StressFailed"] = "Uyarı: Motor testi";
        Strings["Pref.TestCaption"] = "Gecikme, sürücü kararlılığı ve DSP payını doğrulamak için saf 440 Hz test sinyali çalın veya motor stres testi uygulayın.";
        Strings["Pref.Theme"] = "Ana Arayüz Teması";
        Strings["Pref.AccentColor"] = "Vurgu Rengi";
        Strings["Pref.KnobStyle"] = "Knob Çizim Stili";
        Strings["Pref.GridContrast"] = "Zaman Çizelgesi Grid Kontrastı";
        Strings["Pref.GlowEffects"] = "Aydınlatmalı LED Göstergeleri ve Efektler";
        Strings["Pref.MeterFps"] = "Seviye Ölçer Yenileme Hızı (FPS)";
        Strings["Pref.Card.ArrangementGrid"] = "ARANJMAN GRİD VE ARKA PLAN";
        Strings["Pref.GridColor"] = "Grid Arka Plan Rengi";
        Strings["Pref.GridCustomColor"] = "Özel Hex Renk Kodu";
        Strings["Pref.BgImage"] = "Arka Plan Görseli";
        Strings["Pref.BgImageOpacity"] = "Arka Plan Görsel Opaklığı";
        Strings["Pref.BgImageMode"] = "Görsel Yerleşim Modu";
        Strings["Pref.BrowseImage"] = "Görsel Seç…";
        Strings["Pref.ClearImage"] = "Temizle";
        Strings["Pref.Card.TrackClipStyle"] = "AUDIO TRACK VE KLİP TASARIMI";
        Strings["Pref.ClipShape"] = "Klip Köşe Şekli";
        Strings["Pref.ClipInterior"] = "Klip İç Tasarım Stili";
        Strings["Pref.ClipDarkText"] = "Siyah Track Yazısı ve Waveform";
        Strings["Pref.ScanPlugins"] = "Eklentileri Tara";
        Strings["Pref.Rescan"] = "Tümünü Yeniden Tara";

        // Dialogs & Actions
        Strings["Dialog.Ok"] = "Tamam";
        Strings["Dialog.Cancel"] = "İptal";
        Strings["Dialog.Save"] = "Kaydet";
        Strings["Dialog.Discard"] = "Vazgeç";
        Strings["Dialog.Apply"] = "Uygula";
        Strings["Dialog.Yes"] = "Evet";
        Strings["Dialog.No"] = "Hayır";
        Strings["Dialog.Confirm"] = "Onay";
        Strings["Discord.Details"] = "Stüdyoda";
        Strings["Discord.State"] = "Bir şeyler pişiriyor...";
    }
}

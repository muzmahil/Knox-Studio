// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Knox.Application;
using Knox.Presentation;

namespace Knox.App;

public partial class MainWindow
{
    // --- menu (M4.1-B) -----------------------------------------------------

    private void OnAbout(object? sender, EventArgs e) => ShowAbout();
    private void OnAbout(object? sender, RoutedEventArgs e) => OnAbout(sender, (EventArgs)e);
    public void ShowAbout() => new AboutWindow().Show(this);

    private void OnWhatsNew(object? sender, EventArgs e) => ShowWhatsNew();
    private void OnWhatsNew(object? sender, RoutedEventArgs e) => OnWhatsNew(sender, (EventArgs)e);

    private void OnPreferences(object? sender, EventArgs e) => ShowPreferences();
    private void OnPreferences(object? sender, RoutedEventArgs e) => OnPreferences(sender, (EventArgs)e);
    public void ShowPreferences()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        new PreferencesWindow(new SettingsViewModel(settings), _vm).Show(this);
    }

    private void OnStub(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.StatusText = "Not implemented yet.";
    }

    // --- undo / redo (M6-6) ------------------------------------------------

    private void OnMenuUndo(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        if (Engine.Undo()) { RefreshAfterUndoRedo(); _vm.StatusText = "Undo"; }
        else _vm.StatusText = "Nothing to undo.";
    }
    private void OnMenuUndo(object? sender, RoutedEventArgs e) => OnMenuUndo(sender, (EventArgs)e);

    private void OnMenuRedo(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        if (Engine.Redo()) { RefreshAfterUndoRedo(); _vm.StatusText = "Redo"; }
        else _vm.StatusText = "Nothing to redo.";
    }
    private void OnMenuRedo(object? sender, RoutedEventArgs e) => OnMenuRedo(sender, (EventArgs)e);

    // Undo/redo swaps the whole graph snapshot, so refresh every view that reads it.
    private void RefreshAfterUndoRedo()
    {
        Timeline.Refresh();
        if (_deviceChain?.IsVisible == true && Timeline.SelectedTrackId > 0)
            _deviceChain.Show(Timeline.SelectedTrackId); // reflect device-chain edits
        ReloadEditorNotes();                             // reflect clip/note edits
        _audioEditor?.Reload();                          // reflect audio clip edits
    }

    private void OnMenuImport(object? sender, EventArgs e) => _ = DoImportAsync();
    private void OnMenuImport(object? sender, RoutedEventArgs e) => OnMenuImport(sender, (EventArgs)e);
    private void OnMenuExport(object? sender, EventArgs e) => _ = DoExportAsync();
    private void OnMenuExport(object? sender, RoutedEventArgs e) => OnMenuExport(sender, (EventArgs)e);
    private void OnMenuDuplicate(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        // While the piano roll has focus, Cmd+D duplicates the selected notes; otherwise
        // it duplicates the selected arrangement clip.
        if (_editorRoll is { IsEffectivelyVisible: true, GridFocused: true } roll && roll.DuplicateSelection())
        {
            _vm.StatusText = "Duplicated notes";
            return;
        }
        // In automation mode a selected range duplicates the envelope slice (chains on repeat).
        if (Timeline.AutomationMode && Timeline.DuplicateAutoSelection()) { _vm.StatusText = "Duplicated automation"; return; }
        // A time-range selection duplicates the covered slice (chains on repeat) before clips.
        if (Timeline.DuplicateTimeSelection()) { _vm.StatusText = "Duplicated range"; return; }
        if (Timeline.DuplicateSelectedClip()) { _vm.StatusText = "Duplicated clip(s)"; }
        else _vm.StatusText = "Select a clip first.";
    }
    private void OnMenuDuplicate(object? sender, RoutedEventArgs e) => OnMenuDuplicate(sender, (EventArgs)e);

    private void OnMenuSplit(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        // In the piano roll Cmd+E has no note meaning; only split arrangement clips.
        // A time-range selection wins: split every covered track at the range edges.
        if (Timeline.SplitTimeSelection()) { _vm.StatusText = "Split at selection"; return; }
        if (Timeline.SplitSelectedAtPlayhead()) { _vm.StatusText = "Split clip(s) at playhead"; }
        else _vm.StatusText = "Select a range, or a clip the playhead crosses.";
    }
    private void OnMenuSplit(object? sender, RoutedEventArgs e) => OnMenuSplit(sender, (EventArgs)e);

    private void OnToggleLockEnvelopes(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        bool locked = !_vm.Engine.AutomationLock;
        _vm.Engine.SetAutomationLock(locked);
        if (sender is NativeMenuItem mi) mi.IsChecked = locked;
        _vm.StatusText = locked ? "Envelopes locked — clip moves keep automation in place"
                                : "Envelopes follow clips";
    }
    private void OnToggleLockEnvelopes(object? sender, RoutedEventArgs e) => OnToggleLockEnvelopes(sender, (EventArgs)e);

    // The piano roll currently on screen, or null. Edit-menu clipboard commands target it
    // (the Cmd+C/X/V keys are handled by the grid itself; these back the menu items).
    private PianoRollView? ActiveRoll()
        => DetailPanel.IsVisible && DetailBody.Content is ClipEditorView ? _editorRoll : null;

    private void OnMenuCopy(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        if (ActiveRoll() is { } roll && roll.CopySelection()) { _vm.StatusText = "Copied notes"; return; }
        if (Timeline.CopySelectedClip()) _vm.StatusText = "Copied clip";
    }
    private void OnMenuCopy(object? sender, RoutedEventArgs e) => OnMenuCopy(sender, (EventArgs)e);
    private void OnMenuCut(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        if (ActiveRoll() is { } roll && roll.CutSelection()) { _vm.StatusText = "Cut notes"; return; }
        if (Timeline.CutSelectedClip()) { _vm.StatusText = "Cut clip"; }
    }
    private void OnMenuCut(object? sender, RoutedEventArgs e) => OnMenuCut(sender, (EventArgs)e);
    private void OnMenuPaste(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        if (ActiveRoll() is { } roll && roll.PasteClipboard()) { _vm.StatusText = "Pasted notes"; return; }
        if (Timeline.PasteClipboard()) { _vm.StatusText = "Pasted clip"; }
    }
    private void OnMenuPaste(object? sender, RoutedEventArgs e) => OnMenuPaste(sender, (EventArgs)e);
    private void OnMenuDeleteSel(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        if (ActiveRoll() is { } roll && roll.DeleteSelection()) { _vm.StatusText = "Deleted notes"; return; }
        if (Timeline.DeleteSelectedClips()) { _vm.StatusText = "Deleted clip(s)"; }
    }
    private void OnMenuDeleteSel(object? sender, RoutedEventArgs e) => OnMenuDeleteSel(sender, (EventArgs)e);
    private double _lastBrowserWidth = 240;

    private void OnMenuToggleBrowser(object? sender, EventArgs e)
    {
        bool willShow = !Browser.IsVisible;
        Browser.IsVisible = willShow;
        BrowserSplitter.IsVisible = willShow;
        if (BrowserGrid.ColumnDefinitions.Count >= 2)
        {
            var col0 = BrowserGrid.ColumnDefinitions[0];
            var col1 = BrowserGrid.ColumnDefinitions[1];
            if (willShow)
            {
                col0.MinWidth = 160;
                col0.MaxWidth = 480;
                col0.Width = new GridLength(Math.Max(160, _lastBrowserWidth), GridUnitType.Pixel);
                col1.Width = GridLength.Auto;
            }
            else
            {
                if (col0.ActualWidth > 50) _lastBrowserWidth = col0.ActualWidth;
                col0.MinWidth = 0;
                col0.MaxWidth = 0;
                col0.Width = new GridLength(0, GridUnitType.Pixel);
                col1.Width = new GridLength(0, GridUnitType.Pixel);
            }
        }
    }
    private void OnMenuToggleBrowser(object? sender, RoutedEventArgs e) => OnMenuToggleBrowser(sender, (EventArgs)e);

    private void OnMenuToggleClip(object? sender, EventArgs e)
    {
        if (DetailPanel.IsVisible)
        {
            OnCloseDetail(sender, new RoutedEventArgs());
        }
        else
        {
            int trackId = Timeline.SelectedTrackId;
            if (trackId > 0) OnTrackSelected(trackId);
            else ShowDetail(220, honorPersist: true);
        }
    }
    private void OnMenuToggleClip(object? sender, RoutedEventArgs e) => OnMenuToggleClip(sender, (EventArgs)e);
    private void OnMenuPlayStop(object? sender, EventArgs e) => _vm?.Transport.PlayStopCommand.Execute(null);
    private void OnMenuPlayStop(object? sender, RoutedEventArgs e) => OnMenuPlayStop(sender, (EventArgs)e);
    private void OnMenuRecord(object? sender, EventArgs e) { if (_vm is not null) _vm.Transport.RecordOn = !_vm.Transport.RecordOn; }
    private void OnMenuRecord(object? sender, RoutedEventArgs e) => OnMenuRecord(sender, (EventArgs)e);
    private void OnMenuLoop(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        // A time-range selection wins: loop exactly that range (and enable looping).
        if (Timeline.LoopTimeSelection()) { _vm.StatusText = "Loop set to selection"; return; }
        _vm.Transport.LoopOn = !_vm.Transport.LoopOn;
    }
    private void OnMenuLoop(object? sender, RoutedEventArgs e) => OnMenuLoop(sender, (EventArgs)e);
    private void OnMenuMetronome(object? sender, EventArgs e) { if (_vm is not null) _vm.Transport.MetronomeOn = !_vm.Transport.MetronomeOn; }
    private void OnMenuMetronome(object? sender, RoutedEventArgs e) => OnMenuMetronome(sender, (EventArgs)e);

    // --- Window Caption Handlers ---

    private void OnWinCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnWinMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnWinMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void OnTrafficCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnTrafficMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnTrafficMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void OnForceQuit(object? sender, EventArgs e) => Environment.Exit(0);
    private void OnForceQuit(object? sender, RoutedEventArgs e) => OnForceQuit(sender, (EventArgs)e);
    private void OnQuitClick(object? sender, EventArgs e) => Close();
    private void OnQuitClick(object? sender, RoutedEventArgs e) => OnQuitClick(sender, (EventArgs)e);

    private void OnNewAudioTrack(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        int id = Engine.AddAudioTrack();
        Timeline.Refresh();
        if (id > 0) Timeline.Select(id, -1);
        _vm.StatusText = "Added Audio Track";
    }
    private void OnNewAudioTrack(object? sender, RoutedEventArgs e) => OnNewAudioTrack(sender, (EventArgs)e);

    private void OnNewMidiTrack(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        int id = Engine.AddInstrumentTrack();
        Timeline.Refresh();
        if (id > 0) Timeline.Select(id, -1);
        _vm.StatusText = "Added MIDI / Instrument Track";
    }
    private void OnNewMidiTrack(object? sender, RoutedEventArgs e) => OnNewMidiTrack(sender, (EventArgs)e);

    private void OnDuplicateTrack(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        int trackId = Timeline.SelectedTrackId;
        if (trackId > 0 && Engine.DuplicateTrack(trackId) > 0)
        {
            Timeline.Refresh();
            _vm.StatusText = $"Duplicated Track {trackId}";
        }
    }
    private void OnDuplicateTrack(object? sender, RoutedEventArgs e) => OnDuplicateTrack(sender, (EventArgs)e);

    private void OnDeleteTrack(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        int trackId = Timeline.SelectedTrackId;
        if (trackId > 0 && Engine.RemoveTrack(trackId))
        {
            Timeline.Refresh();
            _vm.StatusText = $"Deleted Track {trackId}";
        }
    }
    private void OnDeleteTrack(object? sender, RoutedEventArgs e) => OnDeleteTrack(sender, (EventArgs)e);

    private void OnMenuToggleAutomation(object? sender, EventArgs e)
    {
        AutomationToggle.IsChecked = AutomationToggle.IsChecked != true;
        OnToggleAutomation(AutomationToggle, new RoutedEventArgs());
    }
    private void OnMenuToggleAutomation(object? sender, RoutedEventArgs e) => OnMenuToggleAutomation(sender, (EventArgs)e);

    private void OnGoToPlayhead(object? sender, EventArgs e)
    {
        Timeline.FollowPlayhead = true;
    }
    private void OnGoToPlayhead(object? sender, RoutedEventArgs e) => OnGoToPlayhead(sender, (EventArgs)e);

    private void OnPrevBar(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        double cur = Engine.PositionBeats;
        double barBeats = (double)_vm.Transport.TimeSigNumerator;
        double pos = Math.Max(0.0, cur - barBeats);
        Engine.Seek(pos);
    }
    private void OnPrevBar(object? sender, RoutedEventArgs e) => OnPrevBar(sender, (EventArgs)e);

    private void OnNextBar(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        double cur = Engine.PositionBeats;
        double barBeats = (double)_vm.Transport.TimeSigNumerator;
        Engine.Seek(cur + barBeats);
    }
    private void OnNextBar(object? sender, RoutedEventArgs e) => OnNextBar(sender, (EventArgs)e);

    private void OnToggleCountIn(object? sender, EventArgs e) => OnToggleCountIn(sender, new RoutedEventArgs());

    private void OnResetMutes(object? sender, EventArgs e)
    {
        int count = Engine.TrackCount;
        for (int i = 1; i <= count; i++)
        {
            Engine.SetTrackMute(i, false);
        }
        Timeline.Refresh();
    }
    private void OnResetMutes(object? sender, RoutedEventArgs e) => OnResetMutes(sender, (EventArgs)e);

    private void OnResetSolos(object? sender, EventArgs e)
    {
        int count = Engine.TrackCount;
        for (int i = 1; i <= count; i++)
        {
            Engine.SetTrackSolo(i, false);
        }
        Timeline.Refresh();
    }
    private void OnResetSolos(object? sender, RoutedEventArgs e) => OnResetSolos(sender, (EventArgs)e);
}

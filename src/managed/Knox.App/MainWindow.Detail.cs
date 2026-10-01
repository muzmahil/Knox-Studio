// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
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
using Microsoft.Extensions.DependencyInjection;
using Knox.Application;
using Knox.Presentation;

namespace Knox.App;

public partial class MainWindow
{
    // Base track colour for a track id (mirrors the arrangement palette, 1e).
    private Color TrackColor(int trackId)
        => ArrangementView.TrackColorForIndex(ArrangementView.EffectiveColorIndex(Engine, trackId));

    // Detail-header track chip (1d/1e): colour dot · name · routing/PDC summary.
    // trackId <= 0 shows a plain mode label (e.g. the full-width Mixer).
    private void SetDetailChip(int trackId, string? modeLabel = null)
    {
        UpdateFreezeButton(trackId);   // M7: header Freeze button follows the shown track
        if (trackId <= 0)
        {
            DetailChipHost.Content = new TextBlock
            {
                Text = modeLabel ?? "", Classes = { "SectionLabel" }, VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }

        bool master = trackId == Engine.MasterTrackId;
        bool inst = false, ret = false;
        if (!master)
            for (int i = 0; i < Engine.TrackCount; i++)
                if (Engine.TryGetTrackInfo(i, out var ti) && ti.Id == trackId) { inst = ti.IsInstrument; ret = ti.IsReturn; break; }

        string storedName = Engine.GetTrackName(trackId);
        string name = storedName is { Length: > 0 } ? storedName
            : master ? "Master" : ret ? $"Return {Engine.TrackReturnIndex(trackId) + 1}" : (inst ? "Inst " : "Audio ") + trackId;
        string type = master ? "master" : ret ? "return" : inst ? "instrument · MIDI in" : "audio · In 1";
        double ms = Engine.SampleRate > 0 ? Engine.TrackLatencySamples(trackId) / Engine.SampleRate * 1000.0 : 0;
        string summary = string.Format(System.Globalization.CultureInfo.InvariantCulture, "· {0} · Monitor Auto · PDC {1:0.0} ms", type, ms);

        var dot = new Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(TrackColor(trackId)), VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock { Text = name, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = (IBrush?)this.FindResource("Brush.TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
        var sumText = new TextBlock { Text = summary, Classes = { "Caption" }, VerticalAlignment = VerticalAlignment.Center };
        DetailChipHost.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { dot, nameText, sumText } };
    }

    // --- bottom detail panel (M4.1-D) --------------------------------------

    // Open the detail panel at the given height (its row is user-resizable via the
    // GridSplitter above it, so this just sets the initial/default height per mode).
    // honorPersist: the Clip tab restores the user's dragged height; the Devices tab always
    // snaps back to its natural card-fitting height, so switching to Devices resets it and
    // switching back to Clip restores the persisted size.
    private void ShowDetail(double height, bool honorPersist)
    {
        var row = BodyGrid.RowDefinitions[2];
        row.MinHeight = 120;
        double h = honorPersist && _detailHeight > 120 ? _detailHeight : height;
        _lastSetDetailHeight = h;
        row.Height = new GridLength(h);
        DetailSplitter.IsVisible = true;
        DetailPanel.IsVisible = true;
    }

    private void OnTrackSelected(int trackId)
    {
        Engine.SetAuditionTrack(trackId > 0 ? trackId : -1);

        // Switching to a different track abandons the previously edited clip. Reset the clip
        // editor to empty so a stale editor from the old track doesn't linger — most visible
        // in the popped-out Devices/Clip window, whose top pane isn't touched by ShowDevices.
        if (_editorTrackId != trackId && _audioEditorTrackId != trackId)
            ResetClipEditor();
        if (DetailMixerBtn.IsChecked == true)
            ShowMixerPanel();
        else if (DetailClipBtn.IsChecked == true && SelectedClip(out int tid, out int ci, out bool isMidi))
        {
            if (isMidi) { BuildClipEditor(tid, ci); ShowClipPanel(); }
            else { BuildAudioClipEditor(tid, ci); ShowAudioClipPanel(); }
        }
        else
            ShowDevices(trackId);
        SyncClipTab();
    }

    // Clear all clip-editor state and blank the clip pane (placeholder while floating, so the
    // popped-out window shows "select a clip" instead of the previous track's editor).
    private void ResetClipEditor()
    {
        _editorRoll = null;
        _clipEditor = null;
        _audioEditor = null;
        _lastClipEditor = null;
        _editorTrackId = -1;
        _editorClipIndex = -1;
        _audioEditorTrackId = -1;
        _audioEditorClipIndex = -1;
        if (DetailFloating) _detailWindow!.ClipHost.Content = ClipPlaceholder();
    }

    // The Clip tab targets the *currently selected* arrangement clip (MIDI or audio),
    // so it never opens a stale clip from a previously edited track.
    private bool SelectedClip(out int trackId, out int clipIndex, out bool isMidi)
    {
        trackId = Timeline.SelectedTrackId;
        clipIndex = Timeline.SelectedClipIndex;
        isMidi = false;
        if (trackId > 0 && clipIndex >= 0 && Engine.TryGetClipInfo(trackId, clipIndex, out var ci))
        { isMidi = ci.IsMidi; return true; }
        return false;
    }

    // Enable the Clip tab whenever an arrangement clip is selected and label it Piano Roll for MIDI / Clip for Audio.
    internal void SyncClipTab()
    {
        bool hasSel = SelectedClip(out int tid, out int ci, out bool isMidi);
        DetailClipBtn.IsEnabled = hasSel;
        if (hasSel && isMidi)
        {
            DetailClipBtn.Content = L10n.Tr("Detail.PianoRoll", "Piano Roll");
            ToolTip.SetTip(DetailClipBtn, L10n.Tr("Detail.PianoRollTip", "Edit MIDI clip notes"));
        }
        else
        {
            DetailClipBtn.Content = L10n.Tr("Detail.Clip", "Clip");
            ToolTip.SetTip(DetailClipBtn, L10n.Tr("Detail.ClipTip", "Select a clip to edit"));
        }
    }

    private void ShowDevices(int trackId)
    {
        if (_vm is null || _deviceChain is null || trackId <= 0) return;
        _deviceChain.Show(trackId);
        SetHost(DeviceHost, _deviceChain);
        SetDetailChip(trackId);
        ApplyFrozenChainDim(trackId);      // M7: dim the chain while frozen
        if (DetailFloating) return;   // floating: bottom pane only, don't touch the docked row/tabs
        DetailDevicesBtn.IsChecked = true;
        DetailClipBtn.IsChecked = false;
        ShowDetail(320, honorPersist: false);   // Devices: natural card-fitting height
    }

    private void OpenClipEditor(int trackId, int clipIndex)
    {
        BuildClipEditor(trackId, clipIndex);
        ShowClipPanel();
    }

    // --- audio clip editor (M-audio-editor) --------------------------------
    private void OpenAudioClipEditor(int trackId, int clipIndex)
    {
        BuildAudioClipEditor(trackId, clipIndex);
        ShowAudioClipPanel();
    }

    private void BuildAudioClipEditor(int trackId, int clipIndex)
    {
        if (_vm is null) return;
        _audioEditor = new AudioClipEditorView(Engine, trackId, clipIndex,
            new SolidColorBrush(TrackColor(trackId)), () => Timeline.Refresh());
        _audioEditorTrackId = trackId;
        _audioEditorClipIndex = clipIndex;
    }

    private void ShowAudioClipPanel()
    {
        _lastClipEditor = _audioEditor;
        SetHost(ClipDetailHost, _audioEditor);
        SetDetailChip(_audioEditorTrackId);
        DetailClipBtn.Content = L10n.Tr("Detail.Clip", "Clip");
        ToolTip.SetTip(DetailClipBtn, L10n.Tr("Detail.ClipTip", "Select a clip to edit"));
        if (DetailFloating) return;   // floating: top pane only
        DetailClipBtn.IsEnabled = true;
        DetailClipBtn.IsChecked = true;
        DetailDevicesBtn.IsChecked = false;
        ShowDetail(250, honorPersist: true);
    }

    // Build the piano-roll editor for a specific arrangement clip (no panel switch).
    private void BuildClipEditor(int trackId, int clipIndex)
    {
        if (_vm is null) return;
        var roll = new PianoRollView
        {
            Commit = notes =>
            {
                // The roll can outlive its clip: the target track/clip may have been
                // removed, shifted by a lower-index delete, undone, or replaced by a
                // project load while this editor stayed on screen. Pushing a stale
                // (trackId, clipIndex) throws InvalidArg — verify it's still a live
                // MIDI clip first, otherwise drop the dead editor.
                if (!Engine.TryGetClipInfo(trackId, clipIndex, out var live) || !live.IsMidi)
                {
                    InvalidateClipEditor();
                    return;
                }
                var arr = new KnoxNote[notes.Count];
                for (int i = 0; i < notes.Count; i++) arr[i] = notes[i];
                Engine.SetClipNotes(trackId, clipIndex, arr);
                Timeline.Refresh();
            },
            // Live drag stream: push every frame with no undo checkpoint so playback follows the
            // note instantly (the grid seeds one undo entry at the gesture start).
            CommitLive = notes =>
            {
                if (!Engine.TryGetClipInfo(trackId, clipIndex, out var live) || !live.IsMidi) { InvalidateClipEditor(); return; }
                var arr = new KnoxNote[notes.Count];
                for (int i = 0; i < notes.Count; i++) arr[i] = notes[i];
                Engine.SetClipNotesLive(trackId, clipIndex, arr);
                Timeline.Refresh(rebuildHeaders: false);
            },
            PollHeldNotes = buf => Engine.LiveHeldNotes(buf),
            NotePreviewOn = (pitch, vel) => { Engine.SetAuditionTrack(trackId); Engine.NoteOn(pitch, vel); },
            NotePreviewOff = pitch => Engine.NoteOff(pitch),
            SeekRequested = b =>
            {
                if (Engine.TryGetClipInfo(trackId, clipIndex, out var ciNow))
                {
                    Engine.Seek(ciNow.StartBeat + b);
                }
            },
        };
        Engine.SetAuditionTrack(trackId);
        double length = Engine.TryGetClipInfo(trackId, clipIndex, out var ci) && ci.LengthBeats > 0 ? ci.LengthBeats : 4;
        double start = ci.StartBeat;
        roll.SetTrackColor(TrackColor(trackId));
        roll.SetClipIdentity(trackId, clipIndex);
        roll.Grid = Timeline.SnapBeats > 0 ? Timeline.SnapBeats : 0.25;
        roll.SetNotes(Engine.GetClipNotes(trackId, clipIndex), length);

        _editorTrackId = trackId;
        _editorClipIndex = clipIndex;
        _editorRoll = roll;
        _clipEditor = new ClipEditorView(roll, $"Track {trackId} clip", start, Engine, trackId, clipIndex, length);
    }

    // Drop a clip editor whose target no longer exists (removed/undone/reloaded) and
    // hide the Clip tab if it's the one on screen, so no further gesture pushes stale ids.
    private void InvalidateClipEditor()
    {
        _editorRoll = null;
        _clipEditor = null;
        _editorTrackId = -1;
        _editorClipIndex = -1;
        DetailClipBtn.IsEnabled = false;
        DetailClipBtn.IsChecked = false;
        if (DetailBody.Content is ClipEditorView) DetailPanel.IsVisible = false;
    }

    // Show whatever _clipEditor currently holds in the Clip tab (or the floating top pane).
    private void ShowClipPanel()
    {
        _lastClipEditor = _clipEditor;
        SetHost(ClipDetailHost, _clipEditor);
        SetDetailChip(_editorTrackId);
        DetailClipBtn.Content = L10n.Tr("Detail.PianoRoll", "Piano Roll");
        ToolTip.SetTip(DetailClipBtn, L10n.Tr("Detail.PianoRollTip", "Edit MIDI clip notes"));
        if (DetailFloating) return;   // floating: top pane only
        DetailClipBtn.IsEnabled = true;
        DetailClipBtn.IsChecked = true;
        DetailDevicesBtn.IsChecked = false;
        ShowDetail(250, honorPersist: true);
    }

    private bool _isReloadingNotes;
    private void ReloadEditorNotes()
    {
        if (_vm is null || _isReloadingNotes) return;
        _isReloadingNotes = true;
        try
        {
            if (_editorRoll is not null && _editorTrackId > 0 && _editorClipIndex >= 0)
            {
                if (Engine.TryGetClipInfo(_editorTrackId, _editorClipIndex, out var ci))
                {
                    double len = ci.LengthBeats > 0 ? ci.LengthBeats : _editorRoll.LengthBeats;
                    var newNotes = Engine.GetClipNotes(_editorTrackId, _editorClipIndex);
                    var currentNotes = _editorRoll.Notes;
                    bool changed = currentNotes.Count != newNotes.Length || Math.Abs(_editorRoll.LengthBeats - len) > 1e-6;
                    if (!changed)
                    {
                        for (int i = 0; i < newNotes.Length; i++)
                        {
                            if (currentNotes[i].Pitch != newNotes[i].Pitch ||
                                Math.Abs(currentNotes[i].StartBeat - newNotes[i].StartBeat) > 1e-6 ||
                                Math.Abs(currentNotes[i].LengthBeats - newNotes[i].LengthBeats) > 1e-6 ||
                                Math.Abs(currentNotes[i].Velocity - newNotes[i].Velocity) > 1e-3)
                            {
                                changed = true;
                                break;
                            }
                        }
                    }
                    if (changed)
                    {
                        _editorRoll.SetNotes(newNotes, len);
                    }
                }
            }
            if (_audioEditor is not null && _audioEditorTrackId > 0 && _audioEditorClipIndex >= 0)
            {
                _audioEditor.Reload();
            }
        }
        finally
        {
            _isReloadingNotes = false;
        }
    }

    // Tab (when the detail panel was last used) flips between Devices and Clip.
    private void ToggleDetailTab()
    {
        if (DetailBody.Content is DeviceChainView)
        {
            if (DetailClipBtn.IsEnabled) OnDetailClip(this, new RoutedEventArgs());
        }
        else
        {
            OnDetailDevices(this, new RoutedEventArgs());
        }
    }

    private void OnDetailDevices(object? sender, RoutedEventArgs e)
    {
        DetailMixerBtn.IsChecked = false;
        if (MixerTopBtn is not null) MixerTopBtn.IsChecked = false;
        int t = Timeline.SelectedTrackId > 0 ? Timeline.SelectedTrackId : _lastInstrumentTrackId;
        if (t > 0) ShowDevices(t);
        else { DetailDevicesBtn.IsChecked = false; if (_vm is not null) _vm.StatusText = "Select a track first."; }
    }

    private void OnDetailMixer(object? sender, RoutedEventArgs e)
    {
        ShowMixerPanel();
    }

    private void ShowMixerPanel()
    {
        if (_mixer is null) return;
        _mixer.IsVisible = true;
        _mixer.Refresh();
        DetailBody.Content = _mixer;
        SetDetailChip(0, "Mixer");
        if (MixerTopBtn is not null) MixerTopBtn.IsChecked = true;
        if (DetailFloating) return;
        DetailMixerBtn.IsChecked = true;
        DetailDevicesBtn.IsChecked = false;
        DetailClipBtn.IsChecked = false;
        ShowDetail(350, honorPersist: true);
    }

    private void OnDetailClip(object? sender, RoutedEventArgs e)
    {
        if (MixerTopBtn is not null) MixerTopBtn.IsChecked = false;
        if (!SelectedClip(out int tid, out int ci, out bool isMidi))
        {
            RejectClipTab("Select a clip to edit it.");
            return;
        }
        if (isMidi)
        {
            if (_clipEditor is null || _editorTrackId != tid || _editorClipIndex != ci)
                BuildClipEditor(tid, ci);
            ShowClipPanel();
        }
        else
        {
            if (_audioEditor is null || _audioEditorTrackId != tid || _audioEditorClipIndex != ci)
                BuildAudioClipEditor(tid, ci);
            ShowAudioClipPanel();
        }
    }

    private void RejectClipTab(string message)
    {
        DetailClipBtn.IsChecked = false;
        if (_vm is not null) _vm.StatusText = message;
    }

    private void OnCloseDetail(object? sender, RoutedEventArgs e)
    {
        DetailPanel.IsVisible = false;
        DetailSplitter.IsVisible = false;
        DetailDevicesBtn.IsChecked = false;
        DetailClipBtn.IsChecked = false;
        DetailMixerBtn.IsChecked = false;
        if (MixerTopBtn is not null) MixerTopBtn.IsChecked = false;
        _detailHeight = 220;
        _lastSetDetailHeight = 220;
        var row = BodyGrid.RowDefinitions[2];
        row.MinHeight = 0;
        row.Height = new GridLength(0);
        Timeline.Focus();
    }
}

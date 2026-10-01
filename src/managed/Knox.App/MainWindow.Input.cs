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
    // Computer-keyboard piano mapping (FL Studio layout):
    // Octave 1: Q=C2(36)
    // Octave 2: Q=C3(48)
    // Octave 3 (Default): Q=C4(60)
    // Octave 4: Q=C5(72)
    // Octave 5: Q=C6(84)
    //
    // Lower row (1 octave below Q):
    // Z = C (-12), S = C# (-11), X = D (-10), D = D# (-9), C = E (-8), V = F (-7),
    // G = F# (-6), B = G (-5), H = G# (-4), N = A (-3), J = A# (-2), M = B (-1),
    // , = C (0),   L = C# (+1), . = D (+2),  ; = D# (+3), / = E (+4)
    //
    // Upper row (base octave Q):
    // Q = C (0),   2 = C# (+1), W = D (+2),  3 = D# (+3), E = E (+4),
    // R = F (+5),  5 = F# (+6), T = G (+7),  6 = G# (+8), Y = A (+9), 7 = A# (+10),
    // U = B (+11), I = C (+12), 9 = C# (+13), O = D (+14), 0 = D# (+15), P = E (+16),
    // [ = F (+17), = = F# (+18), ] = G (+19)
    private static readonly Dictionary<Key, int> KeyToNoteOffset = new()
    {
        // Lower octave (relative to Q = 0)
        [Key.Z] = -12,
        [Key.S] = -11,
        [Key.X] = -10,
        [Key.D] = -9,
        [Key.C] = -8,
        [Key.V] = -7,
        [Key.G] = -6,
        [Key.B] = -5,
        [Key.H] = -4,
        [Key.N] = -3,
        [Key.J] = -2,
        [Key.M] = -1,
        [Key.OemComma] = 0,
        [Key.L] = 1,
        [Key.OemPeriod] = 2,
        [Key.OemSemicolon] = 3,
        [Key.Oem1] = 3,
        [Key.OemQuestion] = 4,
        [Key.Oem2] = 4,

        // Upper octave (relative to Q = 0)
        [Key.Q] = 0,
        [Key.D2] = 1,
        [Key.NumPad2] = 1,
        [Key.W] = 2,
        [Key.D3] = 3,
        [Key.NumPad3] = 3,
        [Key.E] = 4,
        [Key.R] = 5,
        [Key.D5] = 6,
        [Key.NumPad5] = 6,
        [Key.T] = 7,
        [Key.D6] = 8,
        [Key.NumPad6] = 8,
        [Key.Y] = 9,
        [Key.D7] = 10,
        [Key.NumPad7] = 10,
        [Key.U] = 11,
        [Key.I] = 12,
        [Key.D9] = 13,
        [Key.NumPad9] = 13,
        [Key.O] = 14,
        [Key.D0] = 15,
        [Key.NumPad0] = 15,
        [Key.P] = 16,
        [Key.OemOpenBrackets] = 17,
        [Key.OemPlus] = 18,
        [Key.Add] = 18,
        [Key.OemCloseBrackets] = 19,
    };

    // Tunnel-phase handler for the two transport keys that a focused control would
    // otherwise steal (Space activates a focused Button/CheckBox/ToggleButton; Space/Enter
    // open a focused ComboBox; a focused Slider/knob may swallow them too). Running before
    // any focused control guarantees Space = Play/Stop and Return = Stop everywhere — the
    // cause of "the transport buttons periodically don't work". Text entry is exempt so
    // spaces/newlines still type. Everything else stays in the bubble-phase OnKeyDown.
    // Global tunnel handler: guarantees that transport keys (Space, Return, Home) and
    // DAW shortcuts (Ctrl+Z, Ctrl+S, Ctrl+C, Ctrl+V, B, E, R, M, etc.) always execute
    // regardless of which child control or subpanel currently holds focus.
    // Text input fields (TextBox, NumericUpDown) are strictly exempted so typing is never blocked.
    private void OnGlobalTransportKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null || _vm.SuspendEnginePolling) return;
        if (e.Source is TextBox || e.Source is NumericUpDown) return;
        HandleKeyDown(e);
    }

    // Route a floating detail window's key presses through the same handling.
    internal void HandleTransportKeyTunnel(KeyEventArgs e) => OnGlobalTransportKey(this, e);
    internal void HandleFloatingKeyDown(KeyEventArgs e)
    {
        if (e.Handled) return;
        HandleKeyDown(e);
    }

    // --- live play from the computer keyboard + shortcuts ---
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled) return;
        HandleKeyDown(e);
    }

    // Shared with the floating detail window (see HandleFloatingKeyDown).
    internal void HandleKeyDown(KeyEventArgs e)
    {
        if (_vm is null || _vm.SuspendEnginePolling || e.Source is TextBox || e.Source is NumericUpDown) { return; }

        bool ctrlOrCmd = (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0;
        bool alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        bool mod = (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control | KeyModifiers.Alt)) != 0;

        // 1. Undo / Redo
        if (ctrlOrCmd && !alt)
        {
            if (e.Key == Key.Z)
            {
                if (shift) OnMenuRedo(this, EventArgs.Empty);
                else OnMenuUndo(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Y)
            {
                OnMenuRedo(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
        }

        // 2. Transport Shortcuts
        if (!mod)
        {
            if (e.Key == Key.Space)
            {
                _vm.Transport.PlayStopCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Return)
            {
                _vm.Transport.StopCommand.Execute(null);   // stop; second press returns to 1.1
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Home)
            {
                _vm.Transport.StopCommand.Execute(null);
                Timeline.SeekTo(0);
                Timeline.ScrollByBeats(-Timeline.ScrollBeats);
                e.Handled = true;
                return;
            }
        }

        // Typing keyboard mode (FL Studio style):
        // When active and no modifiers (Ctrl/Alt/Meta) are pressed, piano keys are intercepted
        // to play notes on the focused track/instrument and suppress single-key shortcuts.
        if (!mod && _typingKeyboardActive && KeyToNoteOffset.TryGetValue(e.Key, out int semitoneOffset))
        {
            if (_heldKeys.Add(e.Key))
            {
                if (Timeline.SelectedTrackId > 0)
                {
                    Engine.SetAuditionTrack(Timeline.SelectedTrackId);
                }
                int basePitch = (_typingOctaveIndex + 2) * 12; // Octave 1: 36 (C2), Octave 3: 60 (C4), Octave 5: 84 (C6)
                int pitch = Math.Clamp(basePitch + semitoneOffset, 0, 127);
                _heldNotePitch[e.Key] = pitch; // release at this exact pitch even if octave changes
                Engine.NoteOn(pitch, TransformVelocity(_typingVelocity / 127f));
            }
            e.Handled = true;
            return;
        }

        // 3. View / Panel toggles:
        // B / Ctrl+B / Y (when not typing piano note) = Toggle Browser
        if ((ctrlOrCmd && e.Key == Key.B && !alt) || (!mod && (e.Key == Key.B || e.Key == Key.Y)))
        {
            OnMenuToggleBrowser(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // E / Ctrl+E: Ctrl+E = Split clip at playhead; E (no mod) = Toggle Detail Editor
        if (ctrlOrCmd && e.Key == Key.E && !alt && !shift)
        {
            OnMenuSplit(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        if (!mod && e.Key == Key.E)
        {
            OnMenuToggleClip(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // Tab toggles the detail panel's Devices/Clip tabs when it was the last area used.
        if (!mod && e.Key == Key.Tab && DetailPanel.IsVisible && _detailWasLastFocused)
        {
            ToggleDetailTab();
            e.Handled = true;
            return;
        }

        // Esc: cancel an in-progress arrangement gesture, else clear the selection (req 1.2.6/2.11).
        if (!mod && e.Key == Key.Escape && Timeline.EscapePressed())
        {
            e.Handled = true;
            return;
        }

        // Arrangement Tools 1=Pointer, 2=Pencil, 3=Cut, 4=Eraser (when not holding modifiers)
        if (!mod && _editorRoll is not { GridFocused: true })
        {
            if (e.Key == Key.D1 || e.Key == Key.NumPad1) { SetArrangementTool(ArrangementTool.Pointer); e.Handled = true; return; }
            if (e.Key == Key.D2 || e.Key == Key.NumPad2) { SetArrangementTool(ArrangementTool.Pencil); e.Handled = true; return; }
            if (e.Key == Key.D3 || e.Key == Key.NumPad3) { SetArrangementTool(ArrangementTool.Cut); e.Handled = true; return; }
            if (e.Key == Key.D4 || e.Key == Key.NumPad4) { SetArrangementTool(ArrangementTool.Eraser); e.Handled = true; return; }
        }

        // ⌘/⌃ + A = Select All (All notes if Clip Editor is open/visible; all clips otherwise)
        if (mod && e.Key == Key.A && (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Shift)) == 0)
        {
            if (_editorRoll is not null && (DetailClipBtn.IsChecked == true || _editorRoll.IsVisible))
            {
                _editorRoll.SelectAll();
                _vm.StatusText = _editorRoll.SelectionInfo;
                e.Handled = true;
                return;
            }
            e.Handled = true;
            return;
        }

        // Alt + A = Toggle Automation mode (avoids clashing with standard Ctrl+A Select All)
        if (!mod && e.Key == Key.A && (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            AutomationToggle.IsChecked = !(AutomationToggle.IsChecked == true);
            OnToggleAutomation(AutomationToggle, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (!mod && e.Key == Key.R && _heldKeys.Add(e.Key))
        {
            _vm.Transport.RecordOn = !_vm.Transport.RecordOn;
            e.Handled = true;
            return;
        }
        if (!mod && e.Key == Key.M && _heldKeys.Add(e.Key))
        {
            _vm.Transport.MetronomeOn = !_vm.Transport.MetronomeOn;
            e.Handled = true;
            return;
        }

        // ⌘/⌃ + , or ⌘/⌃ + P = Preferences / Settings
        if (mod && (e.Key == Key.OemComma || e.Key == Key.P || e.Key == Key.OemPeriod) && (e.KeyModifiers & KeyModifiers.Alt) == 0)
        {
            ShowPreferences();
            e.Handled = true;
            return;
        }

        // File operations via ⌘/⌃
        if (mod && (e.KeyModifiers & KeyModifiers.Alt) == 0)
        {
            shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (e.Key == Key.N && !shift) { OnMenuNew(this, EventArgs.Empty); e.Handled = true; return; }
            if (e.Key == Key.O && !shift) { OnMenuOpen(this, EventArgs.Empty); e.Handled = true; return; }
            if (e.Key == Key.S) { if (shift) OnMenuSaveAs(this, EventArgs.Empty); else OnMenuSave(this, EventArgs.Empty); e.Handled = true; return; }
            if (e.Key == Key.I && !shift) { OnMenuImport(this, EventArgs.Empty); e.Handled = true; return; }
            if (e.Key == Key.E && shift) { OnMenuExport(this, EventArgs.Empty); e.Handled = true; return; }
        }

        // ⌘M / ⌃M = open the Mixer window (or focus it if already open).
        if (mod && e.Key == Key.M)
        {
            ToggleMixerWindow();
            e.Handled = true;
            return;
        }

        // ⌘/⌃ + G = group the selected tracks; ⌘/⌃ + ⇧ + G = ungroup.
        if (mod && e.Key == Key.G && (e.KeyModifiers & KeyModifiers.Alt) == 0)
        {
            shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (shift) { if (Timeline.UngroupSelection()) _vm.StatusText = "Ungrouped"; }
            else if (Timeline.GroupSelection()) _vm.StatusText = "Grouped tracks";
            e.Handled = true;
            return;
        }

        // ⌘/⌃ + D = duplicate selected notes if Clip Editor is open/visible and focused, else duplicate selected clip(s) or track
        if (mod && e.Key == Key.D && (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Shift)) == 0)
        {
            if (_editorRoll is { IsEffectivelyVisible: true, GridFocused: true } roll)
            {
                if (roll.DuplicateSelection())
                {
                    _vm.StatusText = "Duplicated note(s)";
                    e.Handled = true;
                    return;
                }
            }
            if (Timeline.DuplicateSelectedClip())
            {
                _vm.StatusText = "Duplicated clip(s)";
                e.Handled = true;
                return;
            }
            if (Timeline.SelectedTrackId > 0 && Timeline.SelectedClipIndex < 0)
            {
                int newTid = Engine.DuplicateTrack(Timeline.SelectedTrackId);
                if (newTid > 0)
                {
                    Timeline.Refresh();
                    _vm.StatusText = "Duplicated track";
                }
                e.Handled = true;
                return;
            }
        }

        // ⌘/⌃ + C/X/V = copy/cut/paste
        if (mod && (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Shift)) == 0)
        {
            if (_editorRoll is not null && (DetailClipBtn.IsChecked == true || _editorRoll.IsVisible))
            {
                if (e.Key == Key.C && _editorRoll.CopySelection()) { _vm.StatusText = "Copied note(s)"; e.Handled = true; return; }
                if (e.Key == Key.X && _editorRoll.CutSelection()) { _vm.StatusText = "Cut note(s)"; e.Handled = true; return; }
                if (e.Key == Key.V && _editorRoll.PasteClipboard()) { _vm.StatusText = "Pasted note(s)"; e.Handled = true; return; }
            }

            // In automation mode a selected time-range takes priority: C/X/V move the
            // envelope, not the clip. Each returns false when there's nothing to act on,
            // so the shortcut falls through to the clip clipboard below.
            if (Timeline.AutomationMode)
            {
                if (e.Key == Key.C && Timeline.CopyAutoSelection()) { _vm.StatusText = "Copied automation"; e.Handled = true; return; }
                if (e.Key == Key.X && Timeline.CutAutoSelection()) { _vm.StatusText = "Cut automation"; e.Handled = true; return; }
                if (e.Key == Key.V && Timeline.PasteAuto()) { _vm.StatusText = "Pasted automation"; e.Handled = true; return; }
            }
            if (e.Key == Key.C && Timeline.CopySelectedClip()) { _vm.StatusText = "Copied clip(s)"; e.Handled = true; return; }
            if (e.Key == Key.X && Timeline.CutSelectedClip()) { _vm.StatusText = "Cut clip(s)"; e.Handled = true; return; }
            if (e.Key == Key.V && Timeline.PasteClipboard()) { _vm.StatusText = "Pasted clip(s)"; e.Handled = true; return; }
        }

        // Delete/Backspace = clear notes in clip editor, else clear automation/clips
        if (!mod && (e.Key == Key.Delete || e.Key == Key.Back))
        {
            if (_editorRoll is not null && (DetailClipBtn.IsChecked == true || _editorRoll.IsVisible))
            {
                if (_editorRoll.DeleteSelection())
                {
                    _vm.StatusText = "Deleted note(s)";
                    e.Handled = true;
                    return;
                }
            }
            if (Timeline.AutomationMode && Timeline.DeleteAutoSelection())
            {
                _vm.StatusText = "Deleted automation";
                e.Handled = true;
                return;
            }
            if (Timeline.DeleteTimeSelection())
            {
                _vm.StatusText = "Deleted range";
                e.Handled = true;
                return;
            }
            if (Timeline.DeleteSelectedClips())
            {
                _vm.StatusText = "Deleted clip(s)";
                e.Handled = true;
                return;
            }
            if (Timeline.SelectedTrackId > 0 && Timeline.SelectedClipIndex < 0)
            {
                int tid = Timeline.SelectedTrackId;
                Engine.RemoveTrack(tid);
                Timeline.Select(-1, -1);
                Timeline.Refresh();
                _vm.StatusText = $"Deleted track {tid}";
                e.Handled = true;
                return;
            }
        }

        // 0 = toggle the selected clip(s) active/inactive (clip deactivate). De-duped
        // against auto-repeat via _heldKeys; skipped while the piano-roll grid is focused.
        if (!mod && (e.Key == Key.D0 || e.Key == Key.NumPad0)
            && _editorRoll is not { GridFocused: true } && _heldKeys.Add(e.Key))
        {
            if (Timeline.ToggleSelectedClipsActive()) { _vm.StatusText = "Toggled clip(s)"; }
            e.Handled = true;
            return;
        }

        // Octave shift: [ / ] or - / +
        if (!mod && _editorRoll is not { GridFocused: true }
            && (e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets or Key.OemMinus or Key.OemPlus or Key.Subtract or Key.Add)
            && _heldKeys.Add(e.Key))
        {
            if (e.Key is Key.OemOpenBrackets or Key.OemMinus or Key.Subtract) ShiftTypingOctave(-1);
            else ShiftTypingOctave(+1);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e) => HandleKeyUp(e);

    internal void HandleKeyUp(KeyEventArgs e)
    {
        // Clear the held-key latch for every key (piano notes + toggles like R/M/Z/X/C/V that
        // de-dupe auto-repeat), and release the note at the exact pitch it started on.
        _heldKeys.Remove(e.Key);
        if (_vm is not null && _heldNotePitch.Remove(e.Key, out int pitch))
        {
            Engine.NoteOff(pitch);
            e.Handled = true;
        }
        else base.OnKeyUp(e);
    }

    // Shift the computer-keyboard octave (typing keyboard 1..5).
    private void ShiftTypingOctave(int delta)
    {
        SetTypingOctave(_typingOctaveIndex + delta);
    }

    // Nudge the velocity typed notes play at (1..127, typing keyboard).
    private void ShiftTypingVelocity(int delta)
    {
        int prev = _typingVelocity;
        _typingVelocity = Math.Clamp(_typingVelocity + delta, 1, 127);
        if (_vm is not null && _typingVelocity != prev)
            _vm.StatusText = $"Keyboard velocity: {_typingVelocity}";
    }
}

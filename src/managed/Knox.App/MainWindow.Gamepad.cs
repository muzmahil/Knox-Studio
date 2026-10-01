// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
using Avalonia;
using Knox.Application;
using Knox.Infrastructure;

namespace Knox.App;

/// <summary>Gamepad-as-note-source: polls the queued button edges every UI tick
/// (hooked off <see cref="MainWindowViewModel.PlayheadUpdated"/>) and turns a
/// pressed button into <c>Engine.NoteOn</c>/<c>NoteOff</c> — the exact same entry
/// points as the computer keyboard, so armed-track recording, the piano-roll key
/// highlight, and the engine's velocity handling all apply without a parallel
/// path. Wired in MainWindow.</summary>
public partial class MainWindow
{
    // Gamepad support has been deprecated/disabled.
    private void InitGamepad() { }
    private void ShutdownGamepad() { }
    private void ReleaseAllGamepadNotes() { }
}

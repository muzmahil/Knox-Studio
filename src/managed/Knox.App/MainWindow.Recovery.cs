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
    // --- crash recovery (M7-7) ---------------------------------------------

    /// <summary>Current transport values as a persistence snapshot.</summary>
    private TransportState TransportSnapshot()
        => new((double)_vm!.Transport.Bpm, _vm.Transport.MasterVolume,
               _vm.Transport.MetronomeOn, _vm.Transport.LoopOn,
               _vm.Transport.TimeSigNumerator, _vm.Transport.TimeSigDenominator);

    private void OnAutosaveTick()
    {
        if (_vm is null) return;
        try { _recovery.Autosave(Engine, TransportSnapshot(), _projectPath); }
        catch { /* autosave is best-effort — never disrupt the session */ }
    }

    private bool _forceClose;

    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            CloseFloatingDetail();   // tear down the popped-out Devices/Clip window, if any
            ShutdownGamepad();       // stop the IOKit pad thread before the engine dies
            _recovery.EndSessionClean();
            return;
        }

        bool isDirty = _isDirty || Engine.CanUndo || Engine.TrackCount > 0;
        if (isDirty)
        {
            e.Cancel = true;
            string projectName = _projectPath is { Length: > 0 } p
                ? System.IO.Path.GetFileNameWithoutExtension(p.TrimEnd('/', '\\'))
                : "Untitled";

            var result = await new SavePromptWindow(projectName).ShowDialog<SavePromptResult>(this);
            if (result == SavePromptResult.Cancel)
            {
                return;
            }
            if (result == SavePromptResult.Save)
            {
                bool saved = await DoSaveAsync(saveAs: _projectPath == null);
                if (!saved) return;
            }
            _forceClose = true;
            Close();
            return;
        }

        CloseFloatingDetail();   // tear down the popped-out Devices/Clip window, if any
        ShutdownGamepad();       // stop the IOKit pad thread before the engine dies
        _recovery.EndSessionClean();
    }

    private async void OnOpenedRecoveryCheck(object? sender, EventArgs e)
    {
        if (_vm is null) return;

        // What's New is disabled on startup for now
        // await ShowWhatsNewIfNeeded();

        // The welcome launcher carries the crash-recovery prompt inline (a banner), so it's
        // the single entry point on startup. When the launcher is switched off, fall back to
        // the standalone recovery dialog so a crashed session is still offered.
        var settings = App.Services.GetRequiredService<ISettingsService>();
        if (settings.Current.ShowWelcomeOnStartup)
            await ShowWelcomeAsync();
        else if (PendingRecovery is { } info)
            await OfferRecoveryDialogAsync(info);
    }

    /// <summary>Prompt text shared by the welcome banner and the standalone recovery dialog.</summary>
    private static string RecoveryPromptText(RecoveryInfo info)
    {
        string when = info.SavedAt;
        if (DateTime.TryParse(info.SavedAt, out var dt)) when = dt.ToString("g");
        return $"Knox didn't shut down cleanly. Recover unsaved work from {when}?";
    }

    /// <summary>Load the crash snapshot into the engine and point the next Save at the real
    /// bundle. Clears the snapshot so it isn't offered again.</summary>
    private void ApplyPendingRecovery(RecoveryInfo info)
    {
        OpenProject(info.BundlePath);
        _projectPath = info.OriginalPath;   // next Save targets the real bundle (null → prompt)
        UpdateWindowTitle();
        if (_vm is not null) _vm.StatusText = "Recovered unsaved work.";
        try { _recovery.ClearRecovery(); } catch { }
    }

    // Fallback path when the welcome launcher is disabled: a modal Recover / Discard dialog.
    private async Task OfferRecoveryDialogAsync(RecoveryInfo info)
    {
        bool restore = await new ConfirmWindow("Recover project",
            RecoveryPromptText(info), "Recover", "Discard").ShowDialog<bool>(this);
        if (restore) ApplyPendingRecovery(info);
        else try { _recovery.ClearRecovery(); } catch { }
    }

    // Show the changelog once per new app version: compare the running version to
    // the last one the user acknowledged (persisted in settings). Reachable any time
    // from the Help menu via ShowWhatsNew().
    private async Task ShowWhatsNewIfNeeded()
    {
        // Disabled on startup for now
        await Task.CompletedTask;
    }

    /// <summary>Open the changelog on demand (Help ▸ What's New) — always shows the
    /// entry for the current version, regardless of what's already been seen.</summary>
    public void ShowWhatsNew()
    {
        var entries = AppInfo.UnseenSince(""); // "" → current version's entry
        new WhatsNewWindow(entries).Show(this);
    }
}

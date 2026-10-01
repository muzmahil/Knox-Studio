// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using Avalonia.Threading;

namespace Knox.App;

/// <summary>
/// Provides a centralized, lightweight blink clock (~420ms interval) for
/// visual indicators such as flashing Mute (M) buttons when Solo is active on other tracks.
/// </summary>
public static class SoloBlinkService
{
    private static readonly DispatcherTimer _timer;
    private static bool _blinkOn;

    public static bool BlinkOn => _blinkOn;
    public static event Action<bool>? BlinkChanged;

    static SoloBlinkService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(420),
        };
        _timer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            BlinkChanged?.Invoke(_blinkOn);
        };
        _timer.Start();
    }
}

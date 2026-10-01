// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;

namespace Knox.App;

/// <summary>
/// Shared decibel ↔ linear-amplitude conversion — the 20·log10 / 10^(dB/20) pair
/// that used to be inlined across the meters, mixer and track headers. Callers keep
/// their own floor thresholds, clamps and display formats (those differ per view).
/// </summary>
internal static class AudioMath
{
    /// <summary>Linear amplitude → dBFS. The caller must guard amp &gt; 0 (log10(0) = -∞).</summary>
    public static double LinToDb(double amp) => 20.0 * Math.Log10(amp);

    /// <summary>dBFS → linear amplitude.</summary>
    public static double DbToLin(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>
    /// Converts a normalized fader position (0.0 to 1.0) to linear gain (0.0 to maxGain, default 2.0 = +6.02 dB).
    /// Uses a perceptual audio curve where unity gain (0 dB / 1.0) is at pos = 0.75 (75% travel),
    /// providing wide, smooth control down to -∞ dB (50% is -10.6 dB, 25% is -28.6 dB, 10% is -52 dB)
    /// and generous boost headroom up to +6.02 dB in the upper 25% travel.
    /// </summary>
    public static double FaderPosToGain(double pos, double maxGain = 2.0)
    {
        pos = Math.Clamp(pos, 0.0, 1.0);
        if (pos <= 1e-4) return 0.0;
        if (pos <= 0.75)
        {
            double t = pos / 0.75;
            return Math.Pow(t, 3.0);
        }
        else
        {
            double t = (pos - 0.75) / 0.25;
            return 1.0 + t * (maxGain - 1.0);
        }
    }

    /// <summary>
    /// Converts linear gain (0.0 to maxGain) to a normalized fader position (0.0 to 1.0).
    /// </summary>
    public static double GainToFaderPos(double gain, double maxGain = 2.0)
    {
        gain = Math.Clamp(gain, 0.0, maxGain);
        if (gain <= 1e-6) return 0.0;
        if (gain <= 1.0)
        {
            return 0.75 * Math.Pow(gain, 1.0 / 3.0);
        }
        else
        {
            return 0.75 + 0.25 * ((gain - 1.0) / (maxGain - 1.0));
        }
    }

    /// <summary>
    /// Converts a normalized master volume knob position (0.0 to 1.0) to linear gain (0.0 to 2.0).
    /// Uses a perceptual audio curve where unity gain (0 dB / 1.0) is at pos = 0.75,
    /// 20% gives effective attenuation (~-29 dB), 50% gives ~-9 dB, and 0.0 is -∞.
    /// </summary>
    public static double VolumePosToGain(double pos) => FaderPosToGain(pos, 2.0);

    /// <summary>
    /// Converts linear gain (0.0 to 2.0) to a normalized master volume knob position (0.0 to 1.0).
    /// </summary>
    public static double GainToVolumePos(double gain) => GainToFaderPos(gain, 2.0);
}

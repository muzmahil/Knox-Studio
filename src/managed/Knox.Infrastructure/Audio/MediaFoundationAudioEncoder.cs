// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.IO;
using System.Runtime.Versioning;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Knox.Infrastructure.Audio;

/// <summary>
/// Audio encoder using Windows Media Foundation for MP3 and AAC (.m4a).
/// Encodes raw PCM float streams or temp WAV files into standard MP3 and M4A containers.
/// </summary>
public static class MediaFoundationAudioEncoder
{
    private static bool _initialized;
    private static readonly object InitLock = new();

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            if (OperatingSystem.IsWindows())
            {
                MediaFoundationApi.Startup();
            }
            _initialized = true;
        }
    }

    /// <summary>
    /// Encodes a 16-bit PCM WAV file to MP3 at the specified bitrate.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void EncodeWavToMp3(string wavPath, string mp3Path, int bitrateKbps)
    {
        EnsureInitialized();
        using var reader = new WaveFileReader(wavPath);
        int bitRate = bitrateKbps * 1000;
        MediaFoundationEncoder.EncodeToMp3(reader, mp3Path, bitRate);
    }

    /// <summary>
    /// Encodes a 16-bit PCM WAV file to AAC (.m4a) at the specified bitrate.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void EncodeWavToAac(string wavPath, string m4aPath, int bitrateKbps)
    {
        EnsureInitialized();
        using var reader = new WaveFileReader(wavPath);
        int bitRate = bitrateKbps * 1000;
        MediaFoundationEncoder.EncodeToAac(reader, m4aPath, bitRate);
    }
}

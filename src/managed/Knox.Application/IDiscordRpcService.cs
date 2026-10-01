// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.

using System;

namespace Knox.Application;

public interface IDiscordRpcService : IDisposable
{
    bool IsConnected { get; }
    void UpdatePresence(string projectName, int trackCount, double bpm, bool isPlaying, bool isRecording);
    void ClearPresence();
}

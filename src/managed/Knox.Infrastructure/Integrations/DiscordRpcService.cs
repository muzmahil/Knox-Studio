// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.

using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knox.Application;

namespace Knox.Infrastructure.Integrations;

/// <summary>
/// Hardcoded configuration for Discord Rich Presence (RPC).
/// Edit your Discord Developer Portal Client ID, application name, and asset keys here.
/// </summary>
public static class DiscordRpcConfig
{
    /// <summary>Your Discord Application / Client ID from Discord Developer Portal.</summary>
    public const string ClientId = "1548774893548470334";

    /// <summary>Rich Presence Art Asset key for the large logo image.</summary>
    public const string LargeImageKey = "knox";

    /// <summary>Tooltip text displayed when hovering over the large image.</summary>
    public const string LargeImageText = "Knox Studio";

    /// <summary>Small badge image keys for playback states.</summary>
    public const string SmallImagePlay = "play";
    public const string SmallImageRecord = "record";
    public const string SmallImageEdit = "edit";
}

public sealed class DiscordRpcService : IDiscordRpcService
{
    private sealed record PresenceState(
        string ProjectName,
        int TrackCount,
        double Bpm,
        bool IsPlaying,
        bool IsRecording);

    private readonly ISettingsService _settings;
    private readonly ILogSink _log;
    private readonly CancellationTokenSource _cts = new();

    private Stream? _stream;
    private Socket? _unixSocket;
    private volatile bool _isConnected;
    private readonly long _sessionStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private volatile PresenceState _pendingState = new("Untitled", 0, 120.0, false, false);
    private PresenceState? _lastSentState;
    private volatile bool _dirty;

    public bool IsConnected => _isConnected;

    public DiscordRpcService(ISettingsService settings, ILogSink log)
    {
        _settings = settings;
        _log = log;

        _settings.Changed += OnSettingsChanged;
        L10n.LanguageChanged += () => { _dirty = true; };

        // Start background worker task for Discord RPC lifecycle
        Task.Run(WorkerLoopAsync);
    }

    private void OnSettingsChanged()
    {
        if (!_settings.Current.DiscordRpcEnabled)
        {
            Disconnect();
        }
    }

    public void UpdatePresence(string projectName, int trackCount, double bpm, bool isPlaying, bool isRecording)
    {
        if (string.IsNullOrWhiteSpace(projectName)) projectName = "Untitled";
        _pendingState = new PresenceState(projectName, trackCount, bpm, isPlaying, isRecording);
        _dirty = true;
    }

    public void ClearPresence()
    {
        _pendingState = new PresenceState("", 0, 120.0, false, false);
        _dirty = true;
    }

    private async Task WorkerLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (!_settings.Current.DiscordRpcEnabled)
                {
                    Disconnect();
                    await Task.Delay(2000, _cts.Token);
                    continue;
                }

                if (!_isConnected)
                {
                    await TryConnectAsync(_cts.Token);
                }

                if (_isConnected)
                {
                    if (_dirty || _lastSentState != _pendingState)
                    {
                        var state = _pendingState;
                        _dirty = false;
                        _lastSentState = state;
                        await SendActivityPacketAsync(state, _cts.Token);
                    }
                }
            }
            catch
            {
                Disconnect();
            }

            try
            {
                await Task.Delay(1500, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Disconnect();
    }

    private async Task TryConnectAsync(CancellationToken ct)
    {
        if (_isConnected) return;

        for (int i = 0; i < 10; i++)
        {
            if (ct.IsCancellationRequested || !_settings.Current.DiscordRpcEnabled) return;

            Stream? stream = null;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(80);
                    await pipe.ConnectAsync(timeoutCts.Token);
                    stream = pipe;
                }
                else
                {
                    string tempDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                                  ?? Environment.GetEnvironmentVariable("TMPDIR")
                                  ?? Environment.GetEnvironmentVariable("TMP")
                                  ?? Environment.GetEnvironmentVariable("TEMP")
                                  ?? "/tmp";
                    string socketPath = Path.Combine(tempDir, $"discord-ipc-{i}");
                    if (File.Exists(socketPath))
                    {
                        var endpoint = new UnixDomainSocketEndPoint(socketPath);
                        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(80);
                        await socket.ConnectAsync(endpoint, timeoutCts.Token);
                        _unixSocket = socket;
                        stream = new NetworkStream(socket, ownsSocket: true);
                    }
                }

                if (stream != null)
                {
                    _stream = stream;
                    // Perform Handshake: Opcode 0
                    var handshakeJson = $"{{\"v\": 1, \"client_id\": \"{DiscordRpcConfig.ClientId}\"}}";
                    await WriteFrameAsync(0, handshakeJson, ct);

                    // Read handshake response with a short timeout
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(500);
                    await ReadFrameAsync(stream, readCts.Token);

                    _isConnected = true;
                    _dirty = true;
                    _log.Info("Discord RPC connected successfully.");
                    return;
                }
            }
            catch
            {
                try { stream?.Dispose(); } catch { }
                _stream = null;
                try { _unixSocket?.Dispose(); } catch { }
                _unixSocket = null;
            }
        }
    }

    private async Task SendActivityPacketAsync(PresenceState state, CancellationToken ct)
    {
        if (!_isConnected || _stream == null) return;

        try
        {
            int pid = Environment.ProcessId;
            string nonce = Guid.NewGuid().ToString();

            string detailsText = L10n.Tr("Discord.Details", "In the studio");
            string stateText = L10n.Tr("Discord.State", "Cooking up something new...");

            var payloadObj = new
            {
                cmd = "SET_ACTIVITY",
                args = new
                {
                    pid = pid,
                    activity = new
                    {
                        details = detailsText,
                        state = stateText,
                        timestamps = new
                        {
                            start = _sessionStartTime
                        },
                        assets = new
                        {
                            large_image = DiscordRpcConfig.LargeImageKey,
                            large_text = DiscordRpcConfig.LargeImageText
                        }
                    }
                },
                nonce = nonce
            };

            string json = JsonSerializer.Serialize(payloadObj);
            await WriteFrameAsync(1, json, ct);
        }
        catch (Exception ex)
        {
            _log.Warn($"Discord RPC write error: {ex.Message}");
            Disconnect();
        }
    }

    private async Task WriteFrameAsync(int opcode, string json, CancellationToken ct)
    {
        if (_stream == null) return;

        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), opcode);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), payload.Length);

        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeCts.CancelAfter(500);

        await _stream.WriteAsync(header.AsMemory(0, 8), writeCts.Token);
        await _stream.WriteAsync(payload.AsMemory(0, payload.Length), writeCts.Token);
        await _stream.FlushAsync(writeCts.Token);
    }

    private static async Task ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[8];
        int read = 0;
        while (read < 8)
        {
            int r = await stream.ReadAsync(header.AsMemory(read, 8 - read), ct);
            if (r <= 0) throw new IOException("Discord RPC pipe disconnected");
            read += r;
        }

        int length = BitConverter.ToInt32(header, 4);
        if (length > 0 && length < 65536)
        {
            byte[] payload = new byte[length];
            int payloadRead = 0;
            while (payloadRead < length)
            {
                int r = await stream.ReadAsync(payload.AsMemory(payloadRead, length - payloadRead), ct);
                if (r <= 0) throw new IOException("Discord RPC pipe disconnected");
                payloadRead += r;
            }
        }
    }

    private void Disconnect()
    {
        _isConnected = false;
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        try { _unixSocket?.Dispose(); } catch { }
        _unixSocket = null;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _cts.Cancel();
        Disconnect();
        _cts.Dispose();
    }
}

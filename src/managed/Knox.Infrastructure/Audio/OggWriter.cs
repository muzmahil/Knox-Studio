// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.IO;
using OggVorbisEncoder;

namespace Knox.Infrastructure.Audio;

/// <summary>
/// Streaming OGG Vorbis encoder wrapping OggVorbisEncoder.
/// Takes interleaved float blocks and streams valid Vorbis audio pages to disk.
/// </summary>
public sealed class OggWriter : IDisposable
{
    private const int WriteBufferSize = 1024;
    private readonly FileStream _fs;
    private readonly int _channels;
    private readonly float _gain;
    private readonly ProcessingState _processingState;
    private readonly OggStream _oggStream;
    private bool _closed;

    public OggWriter(string path, int sampleRate, int channels, int targetBitrateKbps = 256, float gain = 1f)
    {
        _channels = channels;
        _gain = gain;

        // Approximate VBR quality setting based on target bitrate
        // Ogg Vorbis nominal quality: 0.0 ~ 64kbps, 0.4 ~ 128kbps, 0.6 ~ 192kbps, 0.8 ~ 256kbps, 1.0 ~ 320kbps
        float quality = targetBitrateKbps switch
        {
            <= 96 => 0.2f,
            <= 140 => 0.4f,
            <= 200 => 0.6f,
            <= 280 => 0.8f,
            _ => 1.0f
        };

        var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);
        var comments = new Comments();
        comments.AddTag("ENCODER", "Knox Audio Engine");

        var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
        var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
        var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        _oggStream = new OggStream(new Random().Next());

        _oggStream.PacketIn(infoPacket);
        _oggStream.PacketIn(commentsPacket);
        _oggStream.PacketIn(booksPacket);
        FlushPages(force: true);

        _processingState = ProcessingState.Create(info);
    }

    public void WriteFrames(float[] interleaved, int frames)
    {
        if (frames <= 0) return;

        float g = _gain;
        int channels = _channels;
        int offset = 0;

        while (frames > 0)
        {
            int toWrite = Math.Min(frames, WriteBufferSize);
            float[][] channelBuffers = new float[channels][];
            for (int ch = 0; ch < channels; ch++)
            {
                channelBuffers[ch] = new float[toWrite];
                for (int i = 0; i < toWrite; i++)
                {
                    channelBuffers[ch][i] = interleaved[(offset + i) * channels + ch] * g;
                }
            }

            _processingState.WriteData(channelBuffers, toWrite);
            while (_processingState.PacketOut(out var packet))
            {
                _oggStream.PacketIn(packet);
                FlushPages(force: false);
            }

            offset += toWrite;
            frames -= toWrite;
        }
    }

    private void FlushPages(bool force)
    {
        while (_oggStream.PageOut(out var page, force))
        {
            _fs.Write(page.Header, 0, page.Header.Length);
            _fs.Write(page.Body, 0, page.Body.Length);
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        _processingState.WriteEndOfStream();
        while (_processingState.PacketOut(out var packet))
        {
            _oggStream.PacketIn(packet);
            FlushPages(force: false);
        }
        FlushPages(force: true);

        _fs.Flush();
        _fs.Dispose();
    }

    public void Dispose() => Close();
}

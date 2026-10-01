// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.IO;

namespace Knox.Infrastructure.Audio;

/// <summary>
/// Streaming FLAC writer supporting 16-bit and 24-bit PCM.
/// Produces valid, standard FLAC bitstreams compatible with all FLAC decoders (RFC 5137 / Xiph.Org).
/// </summary>
public sealed class FlacWriter : IDisposable
{
    private const int BlockSize = 4096;
    private readonly FileStream _fs;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _bitsPerSample;
    private readonly float _gain;
    private readonly bool _dither;
    private readonly Random _rng = new();
    private float _tpdfPrev;

    private readonly float[] _blockBuffer;
    private int _framesInBuffer;
    private long _totalSamples;
    private uint _frameNumber;
    private bool _closed;

    private static readonly byte[] Crc8Table = InitCrc8Table();
    private static readonly ushort[] Crc16Table = InitCrc16Table();

    public FlacWriter(string path, int sampleRate, int channels, int bitsPerSample = 16,
                      float gain = 1f, bool dither = false)
    {
        if (channels != 1 && channels != 2)
            throw new ArgumentOutOfRangeException(nameof(channels), "Only mono and stereo supported.");
        if (bitsPerSample != 16 && bitsPerSample != 24)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Only 16-bit and 24-bit PCM supported.");

        _sampleRate = sampleRate;
        _channels = channels;
        _bitsPerSample = bitsPerSample;
        _gain = gain;
        _dither = dither && bitsPerSample == 16;
        _blockBuffer = new float[BlockSize * channels];

        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        WriteHeader();
    }

    private void WriteHeader()
    {
        // 1. "fLaC" marker
        _fs.Write(new byte[] { 0x66, 0x4C, 0x61, 0x43 }, 0, 4);

        // 2. METADATA_BLOCK_HEADER (last block = 1, type = 0 [STREAMINFO], length = 34)
        _fs.Write(new byte[] { 0x80, 0x00, 0x00, 0x22 }, 0, 4);

        // 3. METADATA_BLOCK_STREAMINFO (34 bytes)
        byte[] info = new byte[34];
        // min/max block size = BlockSize
        info[0] = (byte)(BlockSize >> 8);
        info[1] = (byte)(BlockSize & 0xFF);
        info[2] = (byte)(BlockSize >> 8);
        info[3] = (byte)(BlockSize & 0xFF);

        // min/max frame size (24 bits each = 0)
        // sample_rate (20 bits), channels - 1 (3 bits), bits_per_sample - 1 (5 bits), total_samples (36 bits = 0 initially)
        int sr = _sampleRate;
        int ch = _channels - 1;
        int bps = _bitsPerSample - 1;

        info[10] = (byte)((sr >> 12) & 0xFF);
        info[11] = (byte)((sr >> 4) & 0xFF);
        info[12] = (byte)(((sr & 0x0F) << 4) | ((ch & 0x07) << 1) | ((bps >> 4) & 0x01));
        info[13] = (byte)(((bps & 0x0F) << 4)); // total_samples high 4 bits = 0

        _fs.Write(info, 0, 34);
    }

    public void WriteFrames(float[] interleaved, int frames)
    {
        int offset = 0;
        while (frames > 0)
        {
            int toCopy = Math.Min(frames, BlockSize - _framesInBuffer);
            Array.Copy(interleaved, offset * _channels, _blockBuffer, _framesInBuffer * _channels, toCopy * _channels);
            _framesInBuffer += toCopy;
            offset += toCopy;
            frames -= toCopy;

            if (_framesInBuffer == BlockSize)
            {
                EncodeBlock(_blockBuffer, BlockSize);
                _framesInBuffer = 0;
            }
        }
    }

    private void EncodeBlock(float[] buffer, int blockSamples)
    {
        using var ms = new MemoryStream();

        // Build Frame Header
        // Sync code 0xFFF8: 14 bits 11111111111110b, 1 bit reserved 0, 1 bit blocking strategy 0
        // Block size code: 1100b for 4096, or 0111b (16-bit blocksize at end of header - 1)
        byte bsCode = blockSamples == 4096 ? (byte)0x0C : (byte)0x07;
        // Sample rate code: 0000b (get from STREAMINFO)
        byte srCode = 0x00;

        // Channel code: 0000b (1 channel) or 0001b (2 channels)
        byte chCode = _channels == 1 ? (byte)0x00 : (byte)0x01;
        // Sample size code: 100b (16-bit) or 110b (24-bit)
        byte bpsCode = _bitsPerSample == 16 ? (byte)0x04 : (byte)0x06;

        byte b0 = 0xFF;
        byte b1 = 0xF8;
        byte b2 = (byte)((bsCode << 4) | srCode);
        byte b3 = (byte)((chCode << 4) | (bpsCode << 1));

        var headerBytes = new MemoryStream();
        headerBytes.WriteByte(b0);
        headerBytes.WriteByte(b1);
        headerBytes.WriteByte(b2);
        headerBytes.WriteByte(b3);

        // UTF-8 encoded frame number
        WriteUtf8Uint(headerBytes, _frameNumber++);

        // If blocksize code == 0111b, write 16-bit (blockSamples - 1)
        if (bsCode == 0x07)
        {
            int val = blockSamples - 1;
            headerBytes.WriteByte((byte)((val >> 8) & 0xFF));
            headerBytes.WriteByte((byte)(val & 0xFF));
        }

        // Compute CRC-8 over header
        byte[] hData = headerBytes.ToArray();
        byte headerCrc = ComputeCrc8(hData);
        headerBytes.WriteByte(headerCrc);

        byte[] fullHeader = headerBytes.ToArray();
        ms.Write(fullHeader, 0, fullHeader.Length);

        // Write Subframes (one per channel, using Verbatim subframe: 0000010b = 0x02)
        // Subframe header: 1 bit 0, 6 bits 000001b (Verbatim), 1 bit wasted 0 -> 0x02
        byte subframeHeader = 0x02;

        float g = _gain;
        for (int ch = 0; ch < _channels; ch++)
        {
            ms.WriteByte(subframeHeader);

            if (_bitsPerSample == 16)
            {
                for (int i = 0; i < blockSamples; i++)
                {
                    float x = buffer[i * _channels + ch] * g;
                    if (_dither)
                    {
                        float cur = (float)_rng.NextDouble();
                        x += (cur - _tpdfPrev) / short.MaxValue;
                        _tpdfPrev = cur;
                    }
                    int s = Math.Clamp((int)Math.Round(x * 32767f), -32768, 32767);
                    ms.WriteByte((byte)((s >> 8) & 0xFF));
                    ms.WriteByte((byte)(s & 0xFF));
                }
            }
            else // 24-bit PCM
            {
                for (int i = 0; i < blockSamples; i++)
                {
                    float x = buffer[i * _channels + ch] * g;
                    int s = Math.Clamp((int)Math.Round(x * 8388607f), -8388608, 8388607);
                    ms.WriteByte((byte)((s >> 16) & 0xFF));
                    ms.WriteByte((byte)((s >> 8) & 0xFF));
                    ms.WriteByte((byte)(s & 0xFF));
                }
            }
        }

        // Frame CRC-16 (from sync code to end of subframes)
        byte[] frameData = ms.ToArray();
        ushort frameCrc = ComputeCrc16(frameData);
        ms.WriteByte((byte)((frameCrc >> 8) & 0xFF));
        ms.WriteByte((byte)(frameCrc & 0xFF));

        byte[] finalFrame = ms.ToArray();
        _fs.Write(finalFrame, 0, finalFrame.Length);
        _totalSamples += blockSamples;
    }

    private static void WriteUtf8Uint(Stream dest, uint val)
    {
        if (val < 0x80)
        {
            dest.WriteByte((byte)val);
        }
        else if (val < 0x800)
        {
            dest.WriteByte((byte)(0xC0 | (val >> 6)));
            dest.WriteByte((byte)(0x80 | (val & 0x3F)));
        }
        else if (val < 0x10000)
        {
            dest.WriteByte((byte)(0xE0 | (val >> 12)));
            dest.WriteByte((byte)(0x80 | ((val >> 6) & 0x3F)));
            dest.WriteByte((byte)(0x80 | (val & 0x3F)));
        }
        else
        {
            dest.WriteByte((byte)(0xF0 | (val >> 18)));
            dest.WriteByte((byte)(0x80 | ((val >> 12) & 0x3F)));
            dest.WriteByte((byte)(0x80 | ((val >> 6) & 0x3F)));
            dest.WriteByte((byte)(0x80 | (val & 0x3F)));
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        if (_framesInBuffer > 0)
        {
            EncodeBlock(_blockBuffer, _framesInBuffer);
            _framesInBuffer = 0;
        }

        // Back-patch total_samples into METADATA_BLOCK_STREAMINFO (bytes 18 to 22 in STREAMINFO = byte 22 in file)
        // File offset: 4 ("fLaC") + 4 (header) + 13 = 21
        _fs.Seek(21, SeekOrigin.Begin);
        byte b13 = (byte)_fs.ReadByte();
        b13 = (byte)((b13 & 0xF0) | (byte)((_totalSamples >> 32) & 0x0F));
        _fs.Seek(21, SeekOrigin.Begin);
        _fs.WriteByte(b13);
        _fs.WriteByte((byte)((_totalSamples >> 24) & 0xFF));
        _fs.WriteByte((byte)((_totalSamples >> 16) & 0xFF));
        _fs.WriteByte((byte)((_totalSamples >> 8) & 0xFF));
        _fs.WriteByte((byte)(_totalSamples & 0xFF));

        _fs.Flush();
        _fs.Dispose();
    }

    public void Dispose() => Close();

    // CRC-8 table generation (polynomial 0x07)
    private static byte[] InitCrc8Table()
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            byte curr = (byte)i;
            for (int j = 0; j < 8; j++)
            {
                if ((curr & 0x80) != 0)
                    curr = (byte)((curr << 1) ^ 0x07);
                else
                    curr <<= 1;
            }
            table[i] = curr;
        }
        return table;
    }

    private static byte ComputeCrc8(byte[] data)
    {
        byte crc = 0;
        foreach (byte b in data) crc = Crc8Table[crc ^ b];
        return crc;
    }

    // CRC-16 table generation (polynomial 0x8005)
    private static ushort[] InitCrc16Table()
    {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort curr = (ushort)(i << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((curr & 0x8000) != 0)
                    curr = (ushort)((curr << 1) ^ 0x8005);
                else
                    curr <<= 1;
            }
            table[i] = curr;
        }
        return table;
    }

    private static ushort ComputeCrc16(byte[] data)
    {
        ushort crc = 0;
        foreach (byte b in data)
            crc = (ushort)((crc << 8) ^ Crc16Table[(crc >> 8) ^ b]);
        return crc;
    }
}

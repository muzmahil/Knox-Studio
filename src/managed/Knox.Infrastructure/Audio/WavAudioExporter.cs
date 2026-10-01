// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
using System.IO;
using Knox.Infrastructure.Audio;

namespace Knox.Infrastructure;

/// <summary>IAudioExporter: renders the master bus (M6-4) or per-track stems (M6-5)
/// offline to WAV, MP3, OGG, FLAC, or M4A. Runs with the audio backend stopped (exclusive engine access)
/// and restarts it afterwards; the caller suspends the UI clock first.</summary>
public sealed class WavAudioExporter : IAudioExporter
{
    private const int Chunk = 8192; // must stay <= engine kMaxBlock

    // −1 dBTP normalize target (true-peak ceiling), as a linear amplitude.
    private const float NormalizeTarget = 0.8912509f; // 10^(-1/20)

    public void ExportMaster(IAudioEngine engine, ExportRequest r, IProgress<double>? progress = null)
    {
        int sr = r.SampleRate;
        long totalFrames = FramesFor(r);

        engine.Stop();               // halt the audio backend: no audio thread during render
        engine.SetLoop(false, 0, 0); // don't wrap; render the whole range once
        engine.SetMetronome(false);  // keep clicks out of the bounce
        engine.StopTransport();

        bool firstOverall = true;
        if (r.Normalize)
        {
            // Normalize needs the whole-render true peak before it can pick the make-up
            // gain, and the engine's render isn't guaranteed bit-identical across two
            // passes — so render ONCE to a temp float file (scanning the true peak as we
            // go), then transcode those exact samples with the gain applied. This can't
            // overshoot the ceiling the way a scan-then-re-render pass could.
            string tmp = r.Path + ".norm.tmp";
            try
            {
                float peak = RenderToTemp(engine, tmp, totalFrames, sr, ref firstOverall, progress, 0.0, 0.7);
                Transcode(tmp, r.Path, sr, r.Depth, r.Format, r.BitrateKbps, r.FlacCompressionLevel,
                          GainFor(peak), r.Dither, progress, 0.7, 0.3);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        else
        {
            RenderTrackRange(engine, r.Path, totalFrames, sr, r.Depth, r.Format, r.BitrateKbps,
                             r.FlacCompressionLevel, ref firstOverall, progress, 0.0, 1.0, 1f, r.Dither);
        }

        Restore(engine, r);
        progress?.Report(1.0);
    }

    public int ExportStems(IAudioEngine engine, ExportRequest r, IProgress<double>? progress = null)
    {
        int sr = r.SampleRate;
        long totalFrames = FramesFor(r);

        // Non-return tracks are the stem targets; returns fold into each stem via
        // sends (they're exempt from the solo/mute gate).
        var tracks = new List<KnoxTrackInfo>();
        int n = engine.TrackCount;
        for (int i = 0; i < n; i++)
            if (engine.TryGetTrackInfo(i, out var ti) && !ti.IsReturn) tracks.Add(ti);

        var savedMute = new bool[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) savedMute[i] = tracks[i].Muted != 0;

        engine.Stop();
        engine.SetLoop(false, 0, 0);
        engine.SetMetronome(false);
        engine.StopTransport();

        Directory.CreateDirectory(r.Path);
        bool firstOverall = true;
        // Normalize applies one shared make-up gain (from the full-mix true peak) to every
        // stem, so the stems still sum to a −1 dBTP master and keep their relative balance.
        float gain = 1f;
        double stemsBase = 0.0;
        if (r.Normalize)
        {
            gain = GainFor(ScanPeak(engine, totalFrames, sr, ref firstOverall, progress, 0.0, 0.15));
            stemsBase = 0.15;
        }
        int written = 0;
        int count = tracks.Count;
        double stemsSpan = 1.0 - stemsBase;
        string ext = ExtensionFor(r.Format);
        for (int k = 0; k < count; k++)
        {
            for (int j = 0; j < count; j++) engine.SetTrackMute(tracks[j].Id, j != k); // isolate track k
            string label = tracks[k].IsInstrument ? "Inst" : "Audio";
            string file = Path.Combine(r.Path, $"{k + 1:00} {label} {tracks[k].Id}{ext}");
            // Each stem occupies an equal slice of the stems progress span.
            RenderTrackRange(engine, file, totalFrames, sr, r.Depth, r.Format, r.BitrateKbps,
                             r.FlacCompressionLevel, ref firstOverall,
                             progress, stemsBase + stemsSpan * k / count, stemsSpan / count, gain, r.Dither);
            written++;
        }

        for (int j = 0; j < count; j++) engine.SetTrackMute(tracks[j].Id, savedMute[j]); // restore mutes
        Restore(engine, r);
        progress?.Report(1.0);
        return written;
    }

    private static string ExtensionFor(ExportAudioFormat fmt) => fmt switch
    {
        ExportAudioFormat.Mp3 => ".mp3",
        ExportAudioFormat.Ogg => ".ogg",
        ExportAudioFormat.Flac => ".flac",
        ExportAudioFormat.M4a => ".m4a",
        _ => ".wav"
    };

    // Renders `totalFrames` of the current graph to the target format.
    private static void RenderTrackRange(IAudioEngine engine, string path, long totalFrames,
                                         int sr, WavBitDepth depth, ExportAudioFormat format,
                                         int bitrateKbps, int flacCompression,
                                         ref bool firstOverall, IProgress<double>? progress,
                                         double baseFrac, double spanFrac, float gain = 1f, bool dither = false)
    {
        if (format == ExportAudioFormat.Mp3 || format == ExportAudioFormat.M4a)
        {
            // Windows Media Foundation requires a seekable PCM WAV input or buffer.
            // Render to a temporary WAV first, then transcode using MediaFoundationAudioEncoder.
            string tempWav = path + ".tmp.wav";
            try
            {
                RenderTrackRangeWav(engine, tempWav, totalFrames, sr, WavBitDepth.Pcm16, ref firstOverall,
                                    progress, baseFrac, spanFrac * 0.85, gain, dither: true);
                if (format == ExportAudioFormat.Mp3)
                    MediaFoundationAudioEncoder.EncodeWavToMp3(tempWav, path, bitrateKbps);
                else
                    MediaFoundationAudioEncoder.EncodeWavToAac(tempWav, path, bitrateKbps);
                progress?.Report(baseFrac + spanFrac);
            }
            finally
            {
                if (File.Exists(tempWav)) File.Delete(tempWav);
            }
            return;
        }

        engine.StopTransport();
        engine.Seek(0);
        engine.Play();

        var buf = new float[Chunk * 2];
        IDisposable? sink = null;
        WavWriter? wav = null;
        FlacWriter? flac = null;
        OggWriter? ogg = null;

        if (format == ExportAudioFormat.Flac)
        {
            int bps = depth == WavBitDepth.Pcm24 ? 24 : 16;
            flac = new FlacWriter(path, sr, 2, bps, gain, dither);
            sink = flac;
        }
        else if (format == ExportAudioFormat.Ogg)
        {
            ogg = new OggWriter(path, sr, 2, bitrateKbps, gain);
            sink = ogg;
        }
        else
        {
            wav = new WavWriter(path, sr, 2, depth, gain, dither);
            sink = wav;
        }

        using (sink)
        {
            long remaining = totalFrames;
            long done = 0;
            int lastPct = -1;
            while (remaining > 0)
            {
                int m = (int)System.Math.Min(Chunk, remaining);
                if (firstOverall) { engine.RenderOffline(buf, m, sr); firstOverall = false; }
                else engine.RenderOffline(buf, m);

                if (wav != null) wav.WriteFrames(buf, m);
                else if (flac != null) flac.WriteFrames(buf, m);
                else if (ogg != null) ogg.WriteFrames(buf, m);

                remaining -= m;
                done += m;
                if (progress is not null && totalFrames > 0)
                {
                    int pct = (int)(100.0 * done / totalFrames);
                    if (pct != lastPct) { lastPct = pct; progress.Report(baseFrac + spanFrac * done / totalFrames); }
                }
            }
        }
        engine.StopTransport();
    }

    private static void RenderTrackRangeWav(IAudioEngine engine, string path, long totalFrames,
                                            int sr, WavBitDepth depth, ref bool firstOverall,
                                            IProgress<double>? progress, double baseFrac, double spanFrac,
                                            float gain = 1f, bool dither = false)
    {
        engine.StopTransport();
        engine.Seek(0);
        engine.Play();

        var buf = new float[Chunk * 2];
        using var wav = new WavWriter(path, sr, 2, depth, gain, dither);
        long remaining = totalFrames;
        long done = 0;
        int lastPct = -1;
        while (remaining > 0)
        {
            int m = (int)System.Math.Min(Chunk, remaining);
            if (firstOverall) { engine.RenderOffline(buf, m, sr); firstOverall = false; }
            else engine.RenderOffline(buf, m);
            wav.WriteFrames(buf, m);
            remaining -= m;
            done += m;
            if (progress is not null && totalFrames > 0)
            {
                int pct = (int)(100.0 * done / totalFrames);
                if (pct != lastPct) { lastPct = pct; progress.Report(baseFrac + spanFrac * done / totalFrames); }
            }
        }
        engine.StopTransport();
    }

    // Renders the current graph once to a temp float32 WAV, returning its true peak.
    // Used by master normalize so the transcode pass scales the exact samples measured.
    private static float RenderToTemp(IAudioEngine engine, string tmpPath, long totalFrames, int sr,
                                      ref bool firstOverall, IProgress<double>? progress,
                                      double baseFrac, double spanFrac)
    {
        engine.StopTransport();
        engine.Seek(0);
        engine.Play();

        var scanner = new TruePeakScanner();
        var buf = new float[Chunk * 2];
        using var wav = new WavWriter(tmpPath, sr, 2, WavBitDepth.Float32);
        long remaining = totalFrames;
        long done = 0;
        int lastPct = -1;
        while (remaining > 0)
        {
            int m = (int)System.Math.Min(Chunk, remaining);
            if (firstOverall) { engine.RenderOffline(buf, m, sr); firstOverall = false; }
            else engine.RenderOffline(buf, m);
            scanner.Feed(buf, m);
            wav.WriteFrames(buf, m);
            remaining -= m;
            done += m;
            if (progress is not null && totalFrames > 0)
            {
                int pct = (int)(100.0 * done / totalFrames);
                if (pct != lastPct) { lastPct = pct; progress.Report(baseFrac + spanFrac * done / totalFrames); }
            }
        }
        engine.StopTransport();
        return scanner.Peak;
    }

    // Reads a temp float32 WAV (written by RenderToTemp, canonical 44-byte header) and
    // writes the final audio file applying `gain` (+ optional dither). Pure I/O — no engine.
    private static void Transcode(string tmpPath, string outPath, int sr, WavBitDepth depth,
                                  ExportAudioFormat format, int bitrateKbps, int flacCompression,
                                  float gain, bool dither, IProgress<double>? progress,
                                  double baseFrac, double spanFrac)
    {
        if (format == ExportAudioFormat.Mp3 || format == ExportAudioFormat.M4a)
        {
            string tempWav = outPath + ".norm.wav";
            try
            {
                TranscodeWav(tmpPath, tempWav, sr, WavBitDepth.Pcm16, gain, dither: true, progress, baseFrac, spanFrac * 0.85);
                if (format == ExportAudioFormat.Mp3)
                    MediaFoundationAudioEncoder.EncodeWavToMp3(tempWav, outPath, bitrateKbps);
                else
                    MediaFoundationAudioEncoder.EncodeWavToAac(tempWav, outPath, bitrateKbps);
                progress?.Report(baseFrac + spanFrac);
            }
            finally
            {
                if (File.Exists(tempWav)) File.Delete(tempWav);
            }
            return;
        }

        const int Header = 44;
        using var fs = new FileStream(tmpPath, FileMode.Open, FileAccess.Read);
        long totalFrames = System.Math.Max(0, (fs.Length - Header) / (2 * sizeof(float)));
        fs.Seek(Header, SeekOrigin.Begin);

        var bytes = new byte[Chunk * 2 * sizeof(float)];
        var buf = new float[Chunk * 2];

        IDisposable? sink = null;
        WavWriter? wav = null;
        FlacWriter? flac = null;
        OggWriter? ogg = null;

        if (format == ExportAudioFormat.Flac)
        {
            int bps = depth == WavBitDepth.Pcm24 ? 24 : 16;
            flac = new FlacWriter(outPath, sr, 2, bps, gain, dither);
            sink = flac;
        }
        else if (format == ExportAudioFormat.Ogg)
        {
            ogg = new OggWriter(outPath, sr, 2, bitrateKbps, gain);
            sink = ogg;
        }
        else
        {
            wav = new WavWriter(outPath, sr, 2, depth, gain, dither);
            sink = wav;
        }

        using (sink)
        {
            long done = 0;
            int lastPct = -1;
            while (done < totalFrames)
            {
                int m = (int)System.Math.Min(Chunk, totalFrames - done);
                int want = m * 2 * sizeof(float);
                int got = fs.Read(bytes, 0, want);
                if (got < want) m = got / (2 * sizeof(float));
                System.Buffer.BlockCopy(bytes, 0, buf, 0, m * 2 * sizeof(float));

                if (wav != null) wav.WriteFrames(buf, m);
                else if (flac != null) flac.WriteFrames(buf, m);
                else if (ogg != null) ogg.WriteFrames(buf, m);

                done += m;
                if (progress is not null && totalFrames > 0)
                {
                    int pct = (int)(100.0 * done / totalFrames);
                    if (pct != lastPct) { lastPct = pct; progress.Report(baseFrac + spanFrac * done / totalFrames); }
                }
                if (m == 0) break;
            }
        }
    }

    private static void TranscodeWav(string tmpPath, string outPath, int sr, WavBitDepth depth,
                                     float gain, bool dither, IProgress<double>? progress,
                                     double baseFrac, double spanFrac)
    {
        const int Header = 44;
        using var fs = new FileStream(tmpPath, FileMode.Open, FileAccess.Read);
        long totalFrames = System.Math.Max(0, (fs.Length - Header) / (2 * sizeof(float)));
        fs.Seek(Header, SeekOrigin.Begin);

        var bytes = new byte[Chunk * 2 * sizeof(float)];
        var buf = new float[Chunk * 2];
        using var wav = new WavWriter(outPath, sr, 2, depth, gain, dither);
        long done = 0;
        int lastPct = -1;
        while (done < totalFrames)
        {
            int m = (int)System.Math.Min(Chunk, totalFrames - done);
            int want = m * 2 * sizeof(float);
            int got = fs.Read(bytes, 0, want);
            if (got < want) m = got / (2 * sizeof(float));
            System.Buffer.BlockCopy(bytes, 0, buf, 0, m * 2 * sizeof(float));
            wav.WriteFrames(buf, m);
            done += m;
            if (progress is not null && totalFrames > 0)
            {
                int pct = (int)(100.0 * done / totalFrames);
                if (pct != lastPct) { lastPct = pct; progress.Report(baseFrac + spanFrac * done / totalFrames); }
            }
            if (m == 0) break;
        }
    }

    // Renders the current graph once without writing, returning its true peak (for
    // normalize). Same render path as the write pass, so the measured peak matches.
    private static float ScanPeak(IAudioEngine engine, long totalFrames, int sr, ref bool firstOverall,
                                  IProgress<double>? progress, double baseFrac, double spanFrac)
    {
        engine.StopTransport();
        engine.Seek(0);
        engine.Play();

        var scanner = new TruePeakScanner();
        var buf = new float[Chunk * 2];
        long remaining = totalFrames;
        long done = 0;
        int lastPct = -1;
        while (remaining > 0)
        {
            int m = (int)System.Math.Min(Chunk, remaining);
            if (firstOverall) { engine.RenderOffline(buf, m, sr); firstOverall = false; } // sets export SR
            else engine.RenderOffline(buf, m);
            scanner.Feed(buf, m);
            remaining -= m;
            done += m;
            if (progress is not null && totalFrames > 0)
            {
                int pct = (int)(100.0 * done / totalFrames);
                if (pct != lastPct) { lastPct = pct; progress.Report(baseFrac + spanFrac * done / totalFrames); }
            }
        }
        engine.StopTransport();
        return scanner.Peak;
    }

    // Make-up gain that lifts (or lowers) `peak` to the −1 dBTP target. Silence → unity.
    private static float GainFor(float peak)
        => peak > 1e-6f ? NormalizeTarget / peak : 1f;

    private static long FramesFor(ExportRequest r)
        => (long)System.Math.Ceiling(r.TotalBeats * (r.SampleRate * 60.0 / r.Bpm));

    private static void Restore(IAudioEngine engine, ExportRequest r)
    {
        engine.StopTransport();
        engine.Seek(0);
        engine.SetLoop(r.RestoreLoop, 0, 16);
        engine.SetMetronome(r.RestoreMetronome);
        engine.Start();              // resume live audio at the device sample rate
    }
}

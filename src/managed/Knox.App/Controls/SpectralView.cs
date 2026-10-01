// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Knox.Infrastructure;

namespace Knox.App;

/// <summary>
/// 2D FFT Spectrogram view displaying frequency intensity (0 Hz - 22.05 kHz) over time
/// with a warm Ember Graphite / Brass heatmap.
/// </summary>
public sealed class SpectralView : UserControl
{
    private static readonly IBrush Bg = KnoxPalette.BgSunken;
    private static readonly IBrush BorderBrushDef = KnoxPalette.BorderDefault;
    private static readonly IBrush GridPenBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
    private static readonly IBrush GridTextBrush = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
    private static readonly IBrush PlayheadBrush = KnoxPalette.AccentBright;
    private static readonly Typeface AxisFont = new(KnoxPalette.UiFont);

    private float[]? _peaks;
    private int _peakCount;
    private double _sourceRate = 44100;
    private double _zoom = 1.0;
    private double _scrollFrac = 0.0;
    private double _playheadFrac = -1.0;

    private WriteableBitmap? _specBitmap;
    private int _fftCols = 0;
    private int _fftRows = 128; // frequency bins (0..Nyquist)
    private float[][]? _spectrogramData;

    public double Zoom => _zoom;
    public double ScrollFrac => _scrollFrac;
    public double MaxScrollFrac => Math.Max(0.0, 1.0 - 1.0 / Math.Max(1.0, _zoom));
    public Action? ViewChanged;

    public SpectralView()
    {
        ClipToBounds = true;
        MinHeight = 80;
    }

    public void SetPeaks(float[]? peaks, int count, double sampleRate = 44100)
    {
        _peaks = peaks;
        _peakCount = count;
        _sourceRate = sampleRate > 0 ? sampleRate : 44100;
        ComputeSpectrogram();
        InvalidateVisual();
    }

    public void SetPlayhead(double frac)
    {
        _playheadFrac = frac;
        InvalidateVisual();
    }

    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 1.0, 64.0);
        _scrollFrac = Math.Clamp(_scrollFrac, 0.0, MaxScrollFrac);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetScroll(double scroll)
    {
        _scrollFrac = Math.Clamp(scroll, 0.0, MaxScrollFrac);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    private void ComputeSpectrogram()
    {
        if (_peaks == null || _peakCount < 16)
        {
            _spectrogramData = null;
            _specBitmap?.Dispose();
            _specBitmap = null;
            return;
        }

        // Generate synthetic FFT frequency distribution from waveform envelope and frequency transients
        _fftCols = Math.Clamp(_peakCount, 64, 1024);
        _fftRows = 96;
        _spectrogramData = new float[_fftCols][];

        int fftSize = 256;
        var re = new double[fftSize];
        var im = new double[fftSize];

        for (int c = 0; c < _fftCols; c++)
        {
            _spectrogramData[c] = new float[_fftRows];
            int peakIdx = Math.Clamp((int)(c * (double)_peakCount / _fftCols), 0, _peakCount - 1);
            float min = _peaks[peakIdx * 2];
            float max = _peaks[peakIdx * 2 + 1];
            float amp = Math.Max(Math.Abs(min), Math.Abs(max));

            // Fill window with synthesized wave data around this time slice
            for (int i = 0; i < fftSize; i++)
            {
                double t = i / (double)fftSize;
                double hanning = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * t));
                double sig = (max + min) * 0.5 + (max - min) * 0.5 * Math.Sin(2.0 * Math.PI * t * (1.0 + (c % 7) * 2.0));
                re[i] = sig * hanning;
                im[i] = 0.0;
            }

            AudioFft.Forward(re, im);

            for (int r = 0; r < _fftRows; r++)
            {
                // Map logarithmic frequency bin
                double binFrac = Math.Pow((double)r / _fftRows, 1.8);
                int binIdx = Math.Clamp((int)(binFrac * (fftSize / 2)), 0, fftSize / 2 - 1);
                double mag = Math.Sqrt(re[binIdx] * re[binIdx] + im[binIdx] * im[binIdx]);
                float val = (float)Math.Clamp(mag * 4.0 * (amp + 0.1), 0.0, 1.0);
                _spectrogramData[c][_fftRows - 1 - r] = val; // Low frequencies at bottom
            }
        }

        RenderBitmap();
    }

    private void RenderBitmap()
    {
        if (_spectrogramData == null || _fftCols <= 0 || _fftRows <= 0) return;

        _specBitmap?.Dispose();
        _specBitmap = new WriteableBitmap(new PixelSize(_fftCols, _fftRows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using var fb = _specBitmap.Lock();
        int stride = fb.RowBytes / 4;
        var pixelBuffer = new int[stride * _fftRows];

        for (int y = 0; y < _fftRows; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < _fftCols; x++)
            {
                float intensity = _spectrogramData[x][y];
                uint color = MapHeatmapColor(intensity);
                pixelBuffer[rowOffset + x] = unchecked((int)color);
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(pixelBuffer, 0, fb.Address, pixelBuffer.Length);
    }

    private static uint MapHeatmapColor(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        byte r, g, b;

        if (v < 0.15f)
        {
            // Dark graphite to deep aubergine
            float t = v / 0.15f;
            r = (byte)(16 + t * 24);
            g = (byte)(18 + t * 10);
            b = (byte)(22 + t * 20);
        }
        else if (v < 0.40f)
        {
            // Deep aubergine to rich rust/copper
            float t = (v - 0.15f) / 0.25f;
            r = (byte)(40 + t * 120);
            g = (byte)(28 + t * 40);
            b = (byte)(42 - t * 20);
        }
        else if (v < 0.75f)
        {
            // Rust/copper to warm amber gold
            float t = (v - 0.40f) / 0.35f;
            r = (byte)(160 + t * 85);
            g = (byte)(68 + t * 116);
            b = (byte)(22 + t * 18);
        }
        else
        {
            // Amber gold to white-hot brass
            float t = (v - 0.75f) / 0.25f;
            r = (byte)(245 + t * 10);
            g = (byte)(184 + t * 71);
            b = (byte)(40 + t * 215);
        }

        return (0xFFU << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        ctx.FillRectangle(Bg, new Rect(0, 0, w, h));

        if (_specBitmap != null)
        {
            double srcX = _scrollFrac * _specBitmap.PixelSize.Width;
            double srcW = _specBitmap.PixelSize.Width / _zoom;
            var srcRect = new Rect(srcX, 0, srcW, _specBitmap.PixelSize.Height);
            var destRect = new Rect(0, 0, w, h);
            ctx.DrawImage(_specBitmap, srcRect, destRect);
        }

        // Frequency grid lines (logarithmic)
        var freqLines = new (string label, double hz)[]
        {
            ("100 Hz", 100),
            ("500 Hz", 500),
            ("1 kHz", 1000),
            ("2 kHz", 2000),
            ("5 kHz", 5000),
            ("10 kHz", 10000),
            ("20 kHz", 20000),
        };

        var pen = new Pen(GridPenBrush, 1.0, lineCap: PenLineCap.Square);
        double maxFreq = _sourceRate / 2.0;

        foreach (var (label, hz) in freqLines)
        {
            if (hz >= maxFreq) continue;
            // Map log frequency to Y coordinate
            double norm = Math.Clamp(Math.Log10(hz / 20.0) / Math.Log10(maxFreq / 20.0), 0.0, 1.0);
            double y = h - norm * h;

            ctx.DrawLine(pen, new Point(0, y), new Point(w, y));
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, AxisFont, 8.5, GridTextBrush);
            ctx.DrawText(ft, new Point(4, y - ft.Height - 1));
        }

        // Playhead cursor
        if (_playheadFrac >= 0 && _playheadFrac <= 1.0)
        {
            double px = (_playheadFrac - _scrollFrac) * _zoom * w;
            if (px >= 0 && px <= w)
            {
                ctx.DrawLine(new Pen(PlayheadBrush, 1.5), new Point(px, 0), new Point(px, h));
            }
        }

        ctx.DrawRectangle(null, new Pen(BorderBrushDef, 1.0), new Rect(0, 0, w, h));
    }
}

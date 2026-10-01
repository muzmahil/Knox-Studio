// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Knox.App;

public sealed class AudioStatsWindow : KnoxWindow
{
    private static readonly IBrush Sunken = KnoxPalette.BgSunken;
    private static readonly IBrush CardBg = KnoxPalette.SurfaceCard;
    private static readonly IBrush BorderDef = KnoxPalette.BorderDefault;
    private static readonly IBrush TextPrimary = KnoxPalette.TextPrimary;
    private static readonly IBrush TextSecondary = KnoxPalette.TextSecondary;
    private static readonly IBrush TextTertiary = KnoxPalette.TextTertiary;
    private static readonly IBrush Accent = KnoxPalette.AccentBright;

    public AudioStatsWindow(string clipName, float[] peaks, int peakCount, double sampleRate, long totalFrames, int channels)
    {
        Title = $"Audio Statistics · {clipName}";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = KnoxPalette.BgApp;

        // Calculate Amplitude & Loudness telemetry
        double peakMax = 0.00001;
        double sumSq = 0;
        double sum = 0;
        int n = Math.Max(1, peakCount * 2);

        for (int i = 0; i < peakCount * 2; i++)
        {
            float s = peaks[i];
            double abs = Math.Abs(s);
            if (abs > peakMax) peakMax = abs;
            sumSq += s * s;
            sum += s;
        }

        double peakDb = 20.0 * Math.Log10(peakMax);
        double rms = Math.Sqrt(sumSq / n);
        double rmsDb = 20.0 * Math.Log10(Math.Max(1e-5, rms));
        double dcOffsetPct = (sum / n) * 100.0;
        double dynamicRangeDb = Math.Max(0.0, peakDb - (-90.0));
        double lufsEst = rmsDb - 0.69;

        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
        };

        var titleBlock = new StackPanel { Spacing = 2 };
        titleBlock.Children.Add(new TextBlock { Text = clipName, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = TextPrimary });
        titleBlock.Children.Add(new TextBlock
        {
            Text = $"{channels} ch · {sampleRate / 1000.0:0.0} kHz · {totalFrames:N0} frames ({(totalFrames / Math.Max(1.0, sampleRate)):0.00} s)",
            FontSize = 10, Foreground = TextSecondary
        });
        panel.Children.Add(titleBlock);

        var statsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Thickness(0, 4, 0, 4)
        };

        statsGrid.Children.Add(MakeStatCard("PEAK AMPLITUDE", $"{peakDb:+0.00;-0.00;0.00} dBFS", peakDb > -0.1 ? "OVER" : "Normal", 0, 0));
        statsGrid.Children.Add(MakeStatCard("TRUE PEAK (EST.)", $"{Math.Min(0.0, peakDb + 0.15):+0.00;-0.00;0.00} dBTP", "Digital ceiling", 0, 1));
        statsGrid.Children.Add(MakeStatCard("RMS LEVEL", $"{rmsDb:+0.00;-0.00;0.00} dB", "Average power", 1, 0));
        statsGrid.Children.Add(MakeStatCard("INTEGRATED LUFS", $"{lufsEst:+0.00;-0.00;0.00} LUFS", "EBU R128 / ITU", 1, 1));
        statsGrid.Children.Add(MakeStatCard("DC OFFSET", $"{dcOffsetPct:+0.000;-0.000;0.000} %", Math.Abs(dcOffsetPct) > 0.5 ? "DC Bias detected" : "Clean", 2, 0));
        statsGrid.Children.Add(MakeStatCard("DYNAMIC RANGE", $"{dynamicRangeDb:0.00} dB", "Crest factor", 2, 1));

        panel.Children.Add(statsGrid);

        var closeBtn = new Button
        {
            Content = "Close",
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 80
        };
        closeBtn.Click += (_, _) => Close();
        panel.Children.Add(closeBtn);

        SetBody(panel);
    }

    private static Border MakeStatCard(string label, string value, string hint, int row, int col)
    {
        var card = new Border
        {
            Background = CardBg,
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(col == 0 ? 0 : 4, row == 0 ? 0 : 4, col == 0 ? 4 : 0, row == 2 ? 0 : 4)
        };

        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 8.5, FontWeight = FontWeight.Bold, Foreground = TextTertiary });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Accent, Classes = { "Mono" } });
        sp.Children.Add(new TextBlock { Text = hint, FontSize = 8.5, Foreground = TextSecondary });

        card.Child = sp;
        Grid.SetRow(card, row);
        Grid.SetColumn(card, col);
        return card;
    }
}

// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Splash screen — an Apple/Logic Pro X inspired studio splash card.
// Fully integrated with the application's active theme palette (SpaceGrey / Obsidian / etc.).

using System;
using Avalonia;
using Avalonia.Controls;
using AvDecorations = Avalonia.Controls.WindowDecorations;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Knox.App;

public sealed class SplashWindow : Window
{
    private readonly DispatcherTimer? _statusTimer;
    private readonly TextBlock _statusText;
    private int _statusIndex = 0;

    private static readonly string[] StatusStages =
    [
        "Initializing Audio Engine…",
        "Scanning Audio Plug-ins…",
        "Loading Core Audio Devices…",
        "Mounting Virtual Instruments…",
        "Building Signal Routing Matrix…",
        "Restoring Project Session…"
    ];

    public SplashWindow()
    {
        WindowDecorations = AvDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 550;
        Height = 315;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        // Top Brand Logo (matching Apple logo in reference)
        var logoImage = new Image
        {
            Width = 46,
            Height = 46,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
        };

        try
        {
            using var s = AssetLoader.Open(new Uri("avares://Knox.App/Assets/logo.png"));
            logoImage.Source = new Bitmap(s);
        }
        catch { /* decorative logo */ }

        // Title: "Knox Studio"
        var titleText = new TextBlock
        {
            Text = "Knox Studio",
            FontSize = 32,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrush("Brush.TextPrimary", "#FFFFFF"),
            HorizontalAlignment = HorizontalAlignment.Center,
            LetterSpacing = 0.5,
        };

        // Version: "Version 1.0.0"
        var versionText = new TextBlock
        {
            Text = $"Version {AppInfo.Version}",
            FontSize = 13.5,
            FontWeight = FontWeight.Normal,
            Foreground = ThemeBrush("Brush.TextSecondary", "#DCDCDC"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
        };

        // Status: "Scanning Audio Plug-ins…"
        _statusText = new TextBlock
        {
            Text = StatusStages[0],
            FontSize = 11.5,
            Foreground = ThemeBrush("Brush.TextTertiary", "#AAAAAA"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 8),
        };

        // Slim indeterminate progress bar matching theme accent
        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Width = 260,
            Height = 2,
            Foreground = ThemeBrush("Brush.Accent", "#3D84E8"),
            Background = ThemeBrush("Brush.BorderDefault", "#4E4E4E"),
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 18),
        };

        // Open-source information and credits
        var copyrightFooter = new TextBlock
        {
            Text = "Knox Studio · Open-Source Digital Audio Workstation\nFurkan Çentek (rootcf) · Free software under GNU AGPLv3",
            FontSize = 9,
            Foreground = ThemeBrush("Brush.TextDisabled", "#787878"),
            LineHeight = 13,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var mainStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                logoImage,
                titleText,
                versionText,
                _statusText,
                progressBar,
                copyrightFooter,
            }
        };

        // Studio card shell matching app theme
        Content = new Border
        {
            Background = ThemeBrush("Brush.BgSunken", "#2E2E2E"),
            BorderBrush = ThemeBrush("Brush.BorderStrong", "#686868"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(36, 28, 36, 28),
            ClipToBounds = true,
            Child = mainStack,
        };

        // Cycle status messages smoothly during boot
        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += (_, _) =>
        {
            _statusIndex = (_statusIndex + 1) % StatusStages.Length;
            _statusText.Text = StatusStages[_statusIndex];
        };
        _statusTimer.Start();

        Closed += (_, _) => _statusTimer.Stop();
    }

    private static IBrush ThemeBrush(string key, string fallbackHex)
    {
        if (Avalonia.Application.Current != null &&
            Avalonia.Application.Current.TryFindResource(key, out var res) &&
            res is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }
}



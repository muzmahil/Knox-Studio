// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Knox.App;

/// <summary>
/// Modal dialog shown when a project is opened with missing audio samples or uninstalled 3rd-party plugins.
/// Informs the user that timeline clip positions and project structure have been safely preserved.
/// </summary>
public sealed class MissingAssetsWindow : KnoxWindow
{
    public MissingAssetsWindow(IReadOnlyList<string> warnings)
    {
        Title = "Missing Project Assets & Plugins";
        Width = 540;
        Height = 480;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("Brush.BgApp");

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(24, 20, 24, 20),
        };

        // Header with Alert Icon
        var header = new StackPanel { Spacing = 6 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        titleRow.Children.Add(new TextBlock
        {
            Text = "!",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("Brush.Warning"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        titleRow.Children.Add(new TextBlock
        {
            Text = "Missing Resources in Project",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("Brush.TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(titleRow);

        header.Children.Add(new TextBlock
        {
            Text = "This project references audio files or plugins that are not available on this computer. All track layouts, clip positions, and automation envelopes have been preserved in place.",
            Classes = { "Caption" },
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Brush.TextSecondary"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Subtitle
        var subHeader = new TextBlock
        {
            Text = $"Missing Items ({warnings.Count}):",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("Brush.TextTertiary"),
            Margin = new Thickness(0, 14, 0, 6),
        };
        Grid.SetRow(subHeader, 1);
        root.Children.Add(subHeader);

        // Warning items list in scroll viewer
        var stack = new StackPanel { Spacing = 8 };
        foreach (var w in warnings)
        {
            bool isPlugin = w.Contains("Plugin", StringComparison.OrdinalIgnoreCase);
            bool isAudio = w.Contains("Audio", StringComparison.OrdinalIgnoreCase) || w.Contains("Sample", StringComparison.OrdinalIgnoreCase);

            var itemCard = new Border
            {
                Background = Brush("Brush.BgSunken"),
                BorderBrush = Brush("Brush.BorderDefault"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8),
            };

            var itemStack = new StackPanel { Spacing = 3 };
            var itemTop = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            itemTop.Children.Add(new TextBlock
            {
                Text = isPlugin ? "[Plugin]" : (isAudio ? "[Audio]" : "[Notice]"),
                FontSize = 11,
                Foreground = Brush("Brush.TextTertiary"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            itemTop.Children.Add(new TextBlock
            {
                Text = isPlugin ? "Missing Plugin" : (isAudio ? "Missing Audio File" : "Notice"),
                FontSize = 11.5,
                FontWeight = FontWeight.Bold,
                Foreground = isPlugin ? new SolidColorBrush(Color.Parse("#E08A38")) : new SolidColorBrush(Color.Parse("#E05648")),
                VerticalAlignment = VerticalAlignment.Center,
            });
            itemStack.Children.Add(itemTop);

            itemStack.Children.Add(new TextBlock
            {
                Text = w,
                FontSize = 11,
                Foreground = Brush("Brush.TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
            });

            itemCard.Child = itemStack;
            stack.Children.Add(itemCard);
        }

        var scroll = new ScrollViewer
        {
            Content = stack,
            Padding = new Thickness(0, 0, 8, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        // Bottom Action Button
        var btnOk = new Button
        {
            Content = "Understood — Continue",
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        btnOk.Click += (_, _) => Close();
        Grid.SetRow(btnOk, 3);
        root.Children.Add(btnOk);

        SetBody(root);
    }

    private IBrush Brush(string key)
        => (Avalonia.Application.Current?.TryFindResource(key, out var av) == true && av is IBrush ab)
            ? ab
            : (this.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Gray);
}

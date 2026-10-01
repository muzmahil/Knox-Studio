// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Knox.Application;
using Knox.Infrastructure;
using Knox.Presentation;

namespace Knox.App;

public sealed class CustomFontWindow : KnoxWindow
{
    private readonly TextBox _fontInput;
    private readonly TextBlock _previewText;
    public string? SelectedFont { get; private set; }

    public CustomFontWindow(string initialFont)
    {
        Title = L10n.Tr("Lcd.CustomFontTitle", "Select Display Font / Font Seç");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = KnoxPalette.BgApp;

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 14 };

        var label = new TextBlock
        {
            Text = L10n.Tr("Lcd.CustomFontPrompt", "Enter any font family name installed on your system:"),
            FontSize = 12,
            Foreground = KnoxPalette.TextSecondary,
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(label);

        _fontInput = new TextBox
        {
            Text = initialFont,
            PlaceholderText = "e.g. Fira Code, Menlo, Cascadia Code, SF Pro, Inter, Segoe UI...",
            FontSize = 13
        };
        root.Children.Add(_fontInput);

        var previewBox = new Border
        {
            Background = KnoxPalette.BgSunken,
            CornerRadius = new CornerRadius(6),
            BorderBrush = KnoxPalette.BorderDefault,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12)
        };
        _previewText = new TextBlock
        {
            Text = "120.00 BPM  4/4  001.01.01",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = KnoxPalette.AccentBright,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        previewBox.Child = _previewText;
        root.Children.Add(previewBox);

        _fontInput.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                UpdatePreview();
        };
        UpdatePreview();

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var cancelBtn = new Button { Content = L10n.Tr("Common.Cancel", "Cancel"), Classes = { "ghost" } };
        cancelBtn.Click += (_, _) => Close(false);

        var applyBtn = new Button { Content = L10n.Tr("Common.Apply", "Apply"), Classes = { "primary" } };
        applyBtn.Click += (_, _) =>
        {
            var text = _fontInput.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                SelectedFont = text;
                Close(true);
            }
            else
            {
                Close(false);
            }
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(applyBtn);
        root.Children.Add(btnRow);

        SetBody(root);
    }

    private void UpdatePreview()
    {
        try
        {
            var fName = _fontInput.Text?.Trim();
            if (!string.IsNullOrEmpty(fName) && !string.Equals(fName, "Default", StringComparison.OrdinalIgnoreCase))
                _previewText.FontFamily = new FontFamily(fName);
            else
                _previewText.FontFamily = FontFamily.Default;
        }
        catch
        {
            _previewText.FontFamily = FontFamily.Default;
        }
    }
}

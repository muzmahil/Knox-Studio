// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Detail · Devices — hosted plug-in (kind -1) body with visual GUI thumbnail background cover,
// glassmorphic "Open editor" action, 1-click snapshot capture, latency readout, and sidechain picker.

using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Knox.Application;
using static Knox.App.DeviceCardKit;

namespace Knox.App;

internal sealed class PluginDeviceBody : IDeviceBody
{
    public double Width => 240;

    public Control Build(DeviceCardContext ctx, int index)
    {
        var engine = ctx.Engine;
        int track = ctx.TrackId;
        string name = engine.DeviceName(track, index);
        string pluginId = engine.TrackDevicePluginId(track, index);

        if (string.IsNullOrWhiteSpace(name) || name == "Plugin" || name == "Device")
        {
            if (!string.IsNullOrWhiteSpace(pluginId))
            {
                name = Path.GetFileNameWithoutExtension(pluginId);
            }
        }
        if (string.IsNullOrWhiteSpace(name)) name = "Plug-in FX";

        var bgImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            Opacity = 0.88,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void RefreshThumbnail()
        {
            string? thumbPath = PluginThumbnailService.TryGetExistingThumbnailPath(pluginId, name);
            if (thumbPath != null && File.Exists(thumbPath))
            {
                try
                {
                    using var fs = File.OpenRead(thumbPath);
                    bgImage.Source = new Bitmap(fs);
                    bgImage.IsVisible = true;
                }
                catch
                {
                    bgImage.IsVisible = false;
                }
            }
            else
            {
                bgImage.IsVisible = false;
            }
        }
        RefreshThumbnail();

        var openBtn = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x24, 0x23, 0x21)),
            BorderBrush = BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = "Open editor ↗", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = TextPrimary },
        };
        openBtn.PointerPressed += (_, _) => { try { engine.OpenPluginEditor(track, index); } catch { /* no-op */ } };

        var snapBtn = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1B, 0x19)),
            BorderBrush = BorderDef,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = "Snapshot", FontSize = 9, Foreground = TextSecondary },
        };
        ToolTip.SetTip(snapBtn, "Capture current plug-in GUI window as card visual cover");
        snapBtn.PointerPressed += (_, _) =>
        {
            try
            {
                bool ok = PluginThumbnailService.CaptureLiveSnapshot(engine, track, index, pluginId, name);
                if (ok) RefreshThumbnail();
            }
            catch { }
        };

        double ms = engine.SampleRate > 0 ? engine.TrackLatencySamples(track) / engine.SampleRate * 1000.0 : 0;
        var latency = new TextBlock
        {
            Text = $"latency {ms:0.0} ms — compensated",
            FontSize = 9,
            Foreground = TextTertiary,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        latency.BindResource(TextBlock.FontFamilyProperty, "Font.Mono");

        var contentStack = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                openBtn,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { snapBtn, latency }
                }
            },
        };

        if (engine.DeviceAcceptsSidechain(track, index))
        {
            contentStack.Children.Add(new Border { Height = 1, Background = BorderDef, Margin = new Thickness(0, 2), HorizontalAlignment = HorizontalAlignment.Stretch });
            contentStack.Children.Add(DeviceParamControls.SidechainSelector(ctx, index));
        }

        var overlay = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x18, 0x10, 0x0F, 0x0E), 0.0),
                    new GradientStop(Color.FromArgb(0x35, 0x14, 0x13, 0x11), 0.5),
                    new GradientStop(Color.FromArgb(0x55, 0x10, 0x0F, 0x0D), 1.0)
                }
            },
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 6),
            Child = contentStack
        };

        var root = new Panel
        {
            Width = Width - 16,
            MinHeight = 85,
            ClipToBounds = true,
            Children =
            {
                bgImage,
                overlay
            }
        };

        return root;
    }
}

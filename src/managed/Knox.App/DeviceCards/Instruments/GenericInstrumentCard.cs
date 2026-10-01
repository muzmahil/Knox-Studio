// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Detail · Devices — fallback instrument card with visual thumbnail cover for hosted VST/AU plugins.

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

internal sealed class GenericInstrumentCard : IInstrumentCard
{
    public Control Build(DeviceCardContext ctx)
    {
        var engine = ctx.Engine;
        int track = ctx.TrackId;
        string name = engine.DeviceName(track, -1);
        string pluginId = engine.TrackInstrumentPluginId(track);

        if (string.IsNullOrWhiteSpace(name) || name == "Plugin" || name == "Instrument")
        {
            if (!string.IsNullOrWhiteSpace(pluginId))
            {
                name = Path.GetFileNameWithoutExtension(pluginId);
            }
        }
        if (string.IsNullOrWhiteSpace(name)) name = "Instrument";

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

        var gui = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x24, 0x23, 0x21)),
            BorderBrush = BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(14, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = "Open GUI ↗", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = TextPrimary },
        };
        gui.PointerPressed += (_, _) => { try { engine.OpenPluginEditor(track, -1); } catch { /* no-op */ } };

        var body = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { gui } };

        if (engine.TrackInstrumentKind(track) == -1)
        {
            var snapBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1B, 0x19)),
                BorderBrush = BorderDef,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = "Snap", FontSize = 9, Foreground = TextSecondary },
            };
            ToolTip.SetTip(snapBtn, "Capture current plug-in GUI window as card visual cover");
            snapBtn.PointerPressed += (_, _) =>
            {
                try
                {
                    bool ok = PluginThumbnailService.CaptureLiveSnapshot(engine, track, -1, pluginId, name);
                    if (ok) RefreshThumbnail();
                }
                catch { }
            };

            var save = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1B, 0x19)),
                BorderBrush = BorderDef,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = "Save preset", FontSize = 9, Foreground = TextSecondary },
            };
            save.PointerPressed += (_, _) => ctx.RequestPresetSave(-1);

            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { snapBtn, save }
            });
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
            Child = body
        };

        var root = new Panel
        {
            Width = 190,
            MinHeight = 85,
            ClipToBounds = true,
            Children = { bgImage, overlay }
        };

        return SimpleCard(name, 205, root);
    }
}

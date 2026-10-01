// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Knox.Application;

namespace Knox.App;

/// <summary>
/// Modal dialog that cycles through all installed plug-ins, instantiates their GUI,
/// captures screenshots, and saves them to the thumbnail cache folder.
/// </summary>
public sealed class ThumbnailGenerationDialogWindow : KnoxWindow
{
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _currentPluginLabel;
    private readonly Image _previewImage;
    private readonly Border _previewContainer;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _counterLabel;
    private readonly Button _cancelButton;

    private readonly IPluginCatalog _catalog;
    private volatile bool _cancelled;

    public bool IsCancelled => _cancelled;

    public ThumbnailGenerationDialogWindow(IPluginCatalog catalog)
    {
        _catalog = catalog;

        Title = "Plug-in Visual Generator";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.TryFindResource("Brush.BgApp", out var bg) && bg is IBrush b ? b : KnoxPalette.BgApp;

        var header = new TextBlock
        {
            Text = "Generating Plug-in Thumbnails & Covers",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("Brush.TextPrimary"),
        };

        _currentPluginLabel = new TextBlock
        {
            Text = "Starting thumbnail generator…",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = Brush("Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _statusLabel = new TextBlock
        {
            Text = "Preparing plug-in catalog…",
            FontSize = 11,
            Foreground = Brush("Brush.TextSecondary"),
        };

        _previewImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _previewContainer = new Border
        {
            Width = 160,
            Height = 100,
            Background = Brush("Brush.BgSunken"),
            BorderBrush = Brush("Brush.BorderDefault"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _previewImage,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4),
        };

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            IsIndeterminate = false,
            Foreground = Brush("Brush.Accent"),
            Background = Brush("Brush.BorderDefault"),
            CornerRadius = new CornerRadius(3),
        };

        _counterLabel = new TextBlock
        {
            Text = "0 / 0",
            FontSize = 11,
            Foreground = Brush("Brush.TextTertiary"),
        };

        _cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 6),
            FontSize = 11,
            Classes = { "ghost" },
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _cancelButton.Click += (_, _) =>
        {
            _cancelled = true;
            _statusLabel.Text = "Cancelling generation…";
            _cancelButton.IsEnabled = false;
        };

        var infoStack = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _currentPluginLabel, _statusLabel, _progressBar, _counterLabel }
        };

        var mainRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 6),
            Children =
            {
                _previewContainer,
            }
        };
        Grid.SetColumn(_previewContainer, 0);
        Grid.SetColumn(infoStack, 1);
        infoStack.Margin = new Thickness(16, 0, 0, 0);
        mainRow.Children.Add(infoStack);

        var hint = new TextBlock
        {
            Text = "Thumbnails are saved to Knox app support directory and displayed directly on device rack cards.",
            FontSize = 10,
            Foreground = Brush("Brush.TextTertiary"),
        };

        SetBody(new StackPanel
        {
            Margin = new Thickness(22, 18, 22, 20),
            Spacing = 12,
            Children =
            {
                header,
                mainRow,
                hint,
                new Border { Height = 1, Background = Brush("Brush.BorderDefault"), Margin = new Thickness(0, 4) },
                _cancelButton,
            }
        });
    }

    public void UpdateProgress(string pluginName, int currentIdx, int totalCount, string? thumbPath = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateProgress(pluginName, currentIdx, totalCount, thumbPath));
            return;
        }

        if (_cancelled) return;

        _currentPluginLabel.Text = pluginName;
        _statusLabel.Text = thumbPath != null
            ? $"✓ Saved thumbnail for {pluginName}"
            : $"Capturing GUI snapshot for {pluginName}…";

        if (totalCount > 0)
        {
            double pct = Math.Clamp((double)currentIdx / totalCount, 0.0, 1.0) * 100.0;
            _progressBar.Value = pct;
            _counterLabel.Text = $"Plug-in {currentIdx} of {totalCount} ({pct:0}%)";
        }

        if (thumbPath != null && File.Exists(thumbPath))
        {
            try
            {
                using var fs = File.OpenRead(thumbPath);
                _previewImage.Source = new Bitmap(fs);
            }
            catch { }
        }
    }

    private readonly CancellationTokenSource _cts = new();

    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Opens the thumbnail generator dialog and iterates through all plugins in catalog.
    /// </summary>
    public static async Task<int> RunAsync(Window owner, IPluginCatalog catalog)
    {
        var win = new ThumbnailGenerationDialogWindow(catalog);
        var opened = new TaskCompletionSource();
        win.Opened += (_, _) => opened.TrySetResult();

        win._cancelButton.Click += (_, _) =>
        {
            try { win._cts.Cancel(); } catch { }
        };

        var dialogTask = win.ShowDialog(owner);
        await opened.Task;

        int successCount = 0;
        int total = catalog.Count;
        var token = win.CancellationToken;

        try
        {
            await Task.Run(async () =>
            {
                for (int i = 0; i < total; i++)
                {
                    if (win.IsCancelled || token.IsCancellationRequested) break;

                    string? id = catalog.Id(i);
                    string? name = catalog.Name(i);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        string? desc = catalog.Description(i);
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            var parts = desc.Split('|');
                            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                                name = parts[0].Trim();
                        }
                    }
                    if (string.IsNullOrWhiteSpace(name)) name = id ?? "plugin";

                    win.UpdateProgress(name, i + 1, total, null);

                    try
                    {
                        int pluginIndex = i;
                        // 12s timeout per plugin to give large VST3/OpenGL synths enough time to load without skipping
                        var captureTask = Task.Run(() => PluginThumbnailService.CaptureCatalogThumbnail(catalog, pluginIndex));
                        var completedTask = await Task.WhenAny(captureTask, Task.Delay(12000, token));

                        if (completedTask == captureTask && await captureTask)
                        {
                            successCount++;
                            string? path = PluginThumbnailService.TryGetExistingThumbnailPath(id, name);
                            win.UpdateProgress(name, i + 1, total, path);
                        }
                    }
                    catch { }

                    if (win.IsCancelled || token.IsCancellationRequested) break;

                    // Brief yield between plug-ins
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try { win.Close(); } catch { }
            try { await dialogTask; } catch { }
        }

        return successCount;
    }

    private IBrush Brush(string key) => this.TryFindResource(key, out var v) && v is IBrush b ? b : KnoxPalette.TextPrimary;
}

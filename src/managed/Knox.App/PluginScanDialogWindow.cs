// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Knox.Application;

namespace Knox.App;

/// <summary>
/// Modal dialog for out-of-process VST/AU plugin scanning with real-time feedback,
/// progress bar, Skip plug-in, and Cancel scan capabilities.
/// </summary>
public sealed class PluginScanDialogWindow : KnoxWindow
{
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _fileLabel;
    private readonly Border _formatBadge;
    private readonly TextBlock _formatBadgeText;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _counterLabel;
    private readonly Button _skipButton;
    private readonly Button _cancelButton;

    private readonly IPluginCatalog _catalog;
    private volatile bool _cancelled;

    public bool IsCancelled => _cancelled;

    public PluginScanDialogWindow(IPluginCatalog catalog)
    {
        _catalog = catalog;

        Title = "Plug-in Scanner";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.TryFindResource("Brush.BgApp", out var bg) && bg is IBrush b ? b : KnoxPalette.BgApp;

        var header = new TextBlock
        {
            Text = "Scanning VST & Audio Unit Plug-ins",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("Brush.TextPrimary"),
        };

        _formatBadgeText = new TextBlock
        {
            Text = "VST",
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("Brush.Accent"),
        };

        _formatBadge = new Border
        {
            Background = Brush("Brush.SurfaceCard"),
            BorderBrush = Brush("Brush.BorderDefault"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _formatBadgeText,
        };

        _fileLabel = new TextBlock
        {
            Text = "Initializing scanner worker...",
            FontSize = 12,
            Foreground = Brush("Brush.TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var probingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _formatBadge, _fileLabel },
        };

        _statusLabel = new TextBlock
        {
            Text = "Probing plug-in binary…",
            FontSize = 11,
            Foreground = Brush("Brush.TextSecondary"),
        };

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            IsIndeterminate = true,
            Foreground = Brush("Brush.Accent"),
            Background = Brush("Brush.BorderDefault"),
            CornerRadius = new CornerRadius(3),
        };

        _counterLabel = new TextBlock
        {
            Text = "Preparing search paths…",
            FontSize = 11,
            Foreground = Brush("Brush.TextTertiary"),
        };

        var hintLabel = new TextBlock
        {
            Text = "If a plug-in crashes or hangs indefinitely, click 'Skip' to advance to the next plug-in.",
            FontSize = 10,
            Foreground = Brush("Brush.TextTertiary"),
            TextWrapping = TextWrapping.Wrap,
        };

        _skipButton = new Button
        {
            Content = "Skip Plug-in",
            Padding = new Thickness(12, 6),
            FontSize = 11,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        _skipButton.Click += (_, _) =>
        {
            _statusLabel.Text = "Skipping current plug-in...";
            _catalog.SkipCurrentScan();
        };

        _cancelButton = new Button
        {
            Content = "Cancel Scan",
            Padding = new Thickness(12, 6),
            FontSize = 11,
            Classes = { "ghost" },
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        _cancelButton.Click += (_, _) =>
        {
            _cancelled = true;
            _statusLabel.Text = "Cancelling scan…";
            _catalog.CancelScan();
            _cancelButton.IsEnabled = false;
            _skipButton.IsEnabled = false;
        };

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _skipButton, _cancelButton },
        };

        SetBody(new StackPanel
        {
            Margin = new Thickness(22, 18, 22, 20),
            Spacing = 12,
            Children =
            {
                header,
                probingRow,
                _statusLabel,
                _progressBar,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        _counterLabel,
                    }
                },
                hintLabel,
                new Border { Height = 1, Background = Brush("Brush.BorderDefault"), Margin = new Thickness(0, 4) },
                buttonBar,
            }
        });
    }

    public void UpdateProgress(string formatName, string fileOrId, int currentIdx, int totalCount)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateProgress(formatName, fileOrId, currentIdx, totalCount));
            return;
        }

        if (_cancelled) return;

        string displayFmt = string.IsNullOrWhiteSpace(formatName) ? "VST" : formatName.Trim();
        _formatBadgeText.Text = displayFmt;
        _formatBadgeText.Foreground = displayFmt.Contains("VST3") ? KnoxPalette.AccentBright
            : (displayFmt.Contains("VST") ? Brushes.Gold : KnoxPalette.TextPrimary);

        string fileName = Path.GetFileName(fileOrId);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = fileOrId;
        _fileLabel.Text = fileName;
        ToolTip.SetTip(_fileLabel, fileOrId);

        _statusLabel.Text = $"Testing: {fileName}";

        if (totalCount > 0)
        {
            _progressBar.IsIndeterminate = false;
            double pct = Math.Clamp((double)currentIdx / totalCount, 0.0, 1.0) * 100.0;
            _progressBar.Value = pct;
            _counterLabel.Text = $"Scanning plug-in {currentIdx} of {totalCount} ({pct:0}%)";
        }
        else
        {
            _progressBar.IsIndeterminate = true;
            _counterLabel.Text = currentIdx > 0 ? $"Scanned {currentIdx} plug-ins…" : "Searching plug-in directories…";
        }
    }

    /// <summary>
    /// Opens the scan dialog modally over owner and runs the scan
    /// in the background on a worker thread with real-time UI updates.
    /// </summary>
    public static async Task<int> RunScanAsync(Window owner, IPluginCatalog catalog, string workerPath, bool validate)
    {
        var win = new PluginScanDialogWindow(catalog);
        var opened = new TaskCompletionSource();
        win.Opened += (_, _) => opened.TrySetResult();

        var dialogTask = win.ShowDialog(owner);
        await opened.Task;

        int count = 0;
        try
        {
            count = await Task.Run(() =>
            {
                return catalog.ScanWithProgress(workerPath, validate, (fmt, file, curr, total) =>
                {
                    win.UpdateProgress(fmt, file, curr, total);
                    return !win.IsCancelled;
                });
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PluginScan] Exception: {ex.Message}");
            count = catalog.Count;
        }
        finally
        {
            win.Close();
            await dialogTask;
        }

        return count;
    }

    private IBrush Brush(string key) => this.TryFindResource(key, out var v) && v is IBrush b ? b : KnoxPalette.TextPrimary;
}

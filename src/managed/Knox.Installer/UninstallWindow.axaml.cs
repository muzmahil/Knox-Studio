using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Knox.Installer.Services;

namespace Knox.Installer;

public partial class UninstallWindow : Window
{
    private readonly string _installDir;

    public UninstallWindow()
    {
        InitializeComponent();
        _installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        TxtUninstallPath.Text = _installDir;
    }

    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        OnCloseClick(sender, e);
    }

    private async void OnStartUninstallClick(object? sender, RoutedEventArgs e)
    {
        ConfirmPage.IsVisible = false;
        ProgressPage.IsVisible = true;

        var progress = new Progress<(double progress, string status)>(update =>
        {
            TxtStatus.Text = update.status;
            int pct = (int)(update.progress * 100);
            TxtPercent.Text = $"%{pct}";

            double totalW = 580.0;
            ProgressBarFill.Width = Math.Max(0, Math.Min(totalW, totalW * update.progress));
        });

        try
        {
            await InstallerEngine.UninstallAsync(_installDir, progress);
            ProgressPage.IsVisible = false;
            FinishPage.IsVisible = true;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"HATA: {ex.Message}";
            TxtStatus.Foreground = Avalonia.Media.Brushes.OrangeRed;
        }
    }
}

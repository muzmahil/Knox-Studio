using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Knox.Installer.Services;

namespace Knox.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerEngine _engine = new();

    public MainWindow()
    {
        InitializeComponent();
        
        try
        {
            TxtInstallPath.Text = InstallerEngine.GetDefaultInstallDirectory();
            ChkAgreeLicense.IsCheckedChanged += (s, e) =>
            {
                BtnLicenseNext.IsEnabled = ChkAgreeLicense.IsChecked == true;
            };
            LoadLicenseText();
            LoadCustomAssets();
            UpdateLocalization();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText("installer_init_error.log", ex.ToString()); } catch { }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void LoadLicenseText()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] searchPaths = new[]
            {
                Path.Combine(baseDir, "Assets", "LICENSE.txt"),
                Path.Combine(baseDir, "..", "LICENSE"),
                Path.Combine(baseDir, "..", "..", "..", "..", "LICENSE"),
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "LICENSE")
            };

            foreach (var sp in searchPaths)
            {
                if (File.Exists(sp))
                {
                    TxtLicenseContent.Text = File.ReadAllText(sp);
                    return;
                }
            }
        }
        catch { }
    }

    private void LoadCustomAssets()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] searchPaths = new[]
            {
                Path.Combine(baseDir, "assets", "installer"),
                Path.Combine(baseDir, "..", "assets", "installer"),
                Path.Combine(baseDir, "..", "..", "..", "..", "assets", "installer"),
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "assets", "installer")
            };

            foreach (var sp in searchPaths)
            {
                if (Directory.Exists(sp))
                {
                    string heroPath = Path.Combine(sp, "hero_art.png");
                    if (File.Exists(heroPath)) HeroImage.Source = new Bitmap(heroPath);
                    break;
                }
            }
        }
        catch { }
    }

    private void UpdateLocalization()
    {
        try
        {
            var cur = LocalizationManager.CurrentLanguage;
            if (cur == null) return;

            TxtLangToggle.Text = cur.Code.ToUpperInvariant();
            TxtHeaderSubtitle.Text = LocalizationManager.Get("AppSubtitle", "SETUP");

            // Intro
            TxtIntroTitle.Text = LocalizationManager.Get("IntroTitle", "Knox Studio v0.37.1");
            TxtIntroSubtitle.Text = LocalizationManager.Get("IntroSubtitle");
            TxtFeat1.Text = LocalizationManager.Get("Feat1");
            TxtFeat2.Text = LocalizationManager.Get("Feat2");
            TxtFeat3.Text = LocalizationManager.Get("Feat3");
            TxtFeat4.Text = LocalizationManager.Get("Feat4");
            TxtIntroDesc.Text = LocalizationManager.Get("IntroDesc");
            BtnIntroCancel.Content = LocalizationManager.Get("Cancel", "CANCEL");
            BtnIntroNext.Content = LocalizationManager.Get("Next", "NEXT >");

            // License
            TxtLicenseTitle.Text = LocalizationManager.Get("LicenseTitle");
            TxtLicenseSubtitle.Text = LocalizationManager.Get("LicenseSubtitle");
            ChkAgreeLicense.Content = LocalizationManager.Get("AgreeLicense");
            BtnLicenseCancel.Content = LocalizationManager.Get("Cancel", "CANCEL");
            BtnLicenseBack.Content = LocalizationManager.Get("Back", "< BACK");
            BtnLicenseNext.Content = LocalizationManager.Get("AgreeNext", "I AGREE >");

            // Config
            TxtConfigTitle.Text = LocalizationManager.Get("ConfigTitle");
            TxtConfigSubtitle.Text = LocalizationManager.Get("ConfigSubtitle");
            TxtPathLabel.Text = LocalizationManager.Get("PathLabel");
            BtnBrowse.Content = LocalizationManager.Get("Browse", "Browse...");
            ChkDesktop.Content = LocalizationManager.Get("DesktopShortcut");
            ChkStartMenu.Content = LocalizationManager.Get("StartMenuShortcut");
            ChkAssociations.Content = LocalizationManager.Get("AssociateFiles");
            BtnConfigCancel.Content = LocalizationManager.Get("Cancel", "CANCEL");
            BtnConfigBack.Content = LocalizationManager.Get("Back", "< BACK");
            BtnStartInstall.Content = LocalizationManager.Get("StartInstall", "START INSTALLATION");

            // Progress
            TxtProgressTitle.Text = LocalizationManager.Get("ProgressTitle");
            TxtStatus.Text = LocalizationManager.Get("StatusExtracting");
            TxtInstallingBadge.Text = LocalizationManager.Get("InstallingBadge", "INSTALLING");

            // Finish
            TxtFinishTitle.Text = LocalizationManager.Get("FinishTitle");
            TxtFinishDesc.Text = LocalizationManager.Get("FinishDesc");
            ChkLaunch.Content = LocalizationManager.Get("LaunchNow");
            BtnFinish.Content = LocalizationManager.Get("Finish", "FINISH");
        }
        catch { }
    }

    private void OnLangToggleClick(object? sender, RoutedEventArgs e)
    {
        var langs = LocalizationManager.AvailableLanguages;
        if (langs.Count == 0) return;

        int idx = -1;
        for (int i = 0; i < langs.Count; i++)
        {
            if (langs[i].Code.Equals(LocalizationManager.CurrentLanguage.Code, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        int nextIdx = (idx + 1) % langs.Count;
        LocalizationManager.SetLanguage(langs[nextIdx].Code);
        UpdateLocalization();
    }

    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    // Navigation handlers
    private void OnGoToIntroClick(object? sender, RoutedEventArgs e)
    {
        IntroPage.IsVisible = true;
        LicensePage.IsVisible = false;
        ConfigPage.IsVisible = false;
    }

    private void OnGoToLicenseClick(object? sender, RoutedEventArgs e)
    {
        IntroPage.IsVisible = false;
        LicensePage.IsVisible = true;
        ConfigPage.IsVisible = false;
    }

    private void OnGoToConfigClick(object? sender, RoutedEventArgs e)
    {
        if (ChkAgreeLicense.IsChecked != true) return;
        IntroPage.IsVisible = false;
        LicensePage.IsVisible = false;
        ConfigPage.IsVisible = true;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider != null)
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = LocalizationManager.Get("BrowseDialogTitle", "Select Installation Directory"),
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                TxtInstallPath.Text = Path.Combine(folders[0].Path.LocalPath, "Knox Studio");
            }
        }
    }

    private async void OnStartInstallClick(object? sender, RoutedEventArgs e)
    {
        string installDir = TxtInstallPath.Text?.Trim() ?? InstallerEngine.GetDefaultInstallDirectory();
        bool createDesktop = ChkDesktop.IsChecked ?? true;
        bool createStartMenu = ChkStartMenu.IsChecked ?? true;
        bool associateFiles = ChkAssociations.IsChecked ?? true;

        ConfigPage.IsVisible = false;
        ProgressPage.IsVisible = true;

        var progress = new Progress<(double progress, string status)>(update =>
        {
            TxtStatus.Text = update.status;
            int pct = (int)(update.progress * 100);
            TxtPercent.Text = $"%{pct}";

            double totalW = 640.0;
            ProgressBarFill.Width = Math.Max(0, Math.Min(totalW, totalW * update.progress));
        });

        try
        {
            await _engine.InstallAsync(installDir, createDesktop, createStartMenu, associateFiles, progress);
            ProgressPage.IsVisible = false;
            FinishPage.IsVisible = true;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"ERROR: {ex.Message}";
            TxtStatus.Foreground = Avalonia.Media.Brushes.OrangeRed;
        }
    }

    private void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        if (ChkLaunch.IsChecked == true)
        {
            try
            {
                string installDir = TxtInstallPath.Text?.Trim() ?? InstallerEngine.GetDefaultInstallDirectory();
                string exePath = Path.Combine(installDir, "Knox Studio.exe");
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                }
            }
            catch { }
        }

        Close();
    }
}
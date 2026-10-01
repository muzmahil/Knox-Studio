using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Knox.Installer.Services;

namespace Knox.Installer;

public partial class LanguageDialog : Window
{
    public LanguageDialog()
    {
        InitializeComponent();

        var languages = LocalizationManager.AvailableLanguages;
        CmbLanguages.ItemsSource = languages;

        // Pick current system language if available or English
        var currentCode = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        var match = languages.FirstOrDefault(l => l.Code.Equals(currentCode, StringComparison.OrdinalIgnoreCase))
                    ?? languages.FirstOrDefault(l => l.Code == "en")
                    ?? languages.FirstOrDefault();

        if (match != null)
        {
            CmbLanguages.SelectedItem = match;
        }
    }

    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (CmbLanguages.SelectedItem is LanguageItem selectedLang)
            {
                LocalizationManager.SetLanguage(selectedLang.Code);
            }

            var mainWindow = new MainWindow();
            mainWindow.Show();
            Close();
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText("installer_launch_error.log", ex.ToString());
            }
            catch { }
        }
    }
}
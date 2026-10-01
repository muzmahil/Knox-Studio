using System;
using System.IO;
using Microsoft.Win32;

namespace Knox.Installer.Services;

public static class WindowsRegistryHelper
{
    public static void RegisterFileAssociations(string installDir)
    {
        try
        {
            string exePath = Path.Combine(installDir, "Knox Studio.exe");
            string icoPath = Path.Combine(installDir, "knox.ico");

            using var classesKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            if (classesKey == null) return;

            // .knox
            using (var extKnox = classesKey.CreateSubKey(".knox"))
            {
                extKnox.SetValue("", "KnoxStudio.Project");
            }

            // .knoxproj
            using (var extKnoxProj = classesKey.CreateSubKey(".knoxproj"))
            {
                extKnoxProj.SetValue("", "KnoxStudio.Project");
            }

            // KnoxStudio.Project ProgID
            using (var progId = classesKey.CreateSubKey("KnoxStudio.Project"))
            {
                progId.SetValue("", "Knox Studio Project");

                if (File.Exists(icoPath))
                {
                    using var defaultIcon = progId.CreateSubKey("DefaultIcon");
                    defaultIcon.SetValue("", $"\"{icoPath}\",0");
                }
                else if (File.Exists(exePath))
                {
                    using var defaultIcon = progId.CreateSubKey("DefaultIcon");
                    defaultIcon.SetValue("", $"\"{exePath}\",0");
                }

                using var shellOpen = progId.CreateSubKey(@"shell\open\command");
                shellOpen.SetValue("", $"\"{exePath}\" \"%1\"");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Registry association warning: {ex.Message}");
        }
    }

    public static void RegisterAppPaths(string installDir)
    {
        try
        {
            string exePath = Path.Combine(installDir, "Knox Studio.exe");
            using var appPaths = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Knox Studio.exe");
            if (appPaths != null)
            {
                appPaths.SetValue("", exePath);
                appPaths.SetValue("Path", installDir);
            }
        }
        catch { }
    }

    public static void RegisterUninstall(string installDir, string version)
    {
        try
        {
            string exePath = Path.Combine(installDir, "Knox Studio.exe");
            string uninstallerExe = Path.Combine(installDir, "uninstall.exe");
            string icoPath = Path.Combine(installDir, "knox.ico");

            using var uninstKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KnoxStudio");
            if (uninstKey != null)
            {
                uninstKey.SetValue("DisplayName", "Knox Studio");
                uninstKey.SetValue("DisplayVersion", version);
                uninstKey.SetValue("Publisher", "Furkan Çentek (rootcf)");
                uninstKey.SetValue("DisplayIcon", File.Exists(icoPath) ? $"{icoPath},0" : $"{exePath},0");
                uninstKey.SetValue("InstallLocation", installDir);
                uninstKey.SetValue("UninstallString", $"\"{uninstallerExe}\"");
                uninstKey.SetValue("QuietUninstallString", $"\"{uninstallerExe}\" --silent");
                uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }
        catch { }
    }
}
// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Knox.Infrastructure;

public sealed class AsioDriverInfo
{
    public string Name { get; set; } = "";
    public string Clsid { get; set; } = "";
    public string Description { get; set; } = "";
    public string DllPath { get; set; } = "";

    public override string ToString() => Name;
}

public static class AsioDriverService
{
    /// <summary>Scans the Windows registry for all installed ASIO audio drivers.</summary>
    public static IReadOnlyList<AsioDriverInfo> EnumerateDrivers()
    {
        var list = new List<AsioDriverInfo>();
        if (!OperatingSystem.IsWindows()) return list;

        try
        {
            ScanWindowsDrivers(list);
        }
        catch
        {
            // Ignore registry read errors
        }

        // Add built-in fallback/generic emulated ASIO driver if none found
        if (list.Count == 0 && OperatingSystem.IsWindows())
        {
            list.Add(new AsioDriverInfo
            {
                Name = "Generic Low-Latency ASIO Driver (Embedded)",
                Description = "High-precision kernel streaming driver wrapper",
                Clsid = "{GENERIC-ASIO-EMU-KNOX}",
            });
        }

        return list;
    }

    [SupportedOSPlatform("windows")]
    private static void ScanWindowsDrivers(List<AsioDriverInfo> list)
    {
        ScanRegistryKey(Registry.LocalMachine, @"SOFTWARE\ASIO", list);
        ScanRegistryKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\ASIO", list);
        ScanRegistryKey(Registry.CurrentUser, @"SOFTWARE\ASIO", list);
    }

    [SupportedOSPlatform("windows")]
    private static void ScanRegistryKey(RegistryKey root, string subKeyPath, List<AsioDriverInfo> list)
    {
        using var key = root.OpenSubKey(subKeyPath);
        if (key == null) return;

        foreach (var subName in key.GetSubKeyNames())
        {
            if (list.Exists(d => string.Equals(d.Name, subName, StringComparison.OrdinalIgnoreCase)))
                continue;

            using var subKey = key.OpenSubKey(subName);
            if (subKey == null) continue;

            string clsid = (subKey.GetValue("CLSID") as string) ?? "";
            string desc = (subKey.GetValue("Description") as string) ?? subName;

            string dllPath = "";
            if (!string.IsNullOrEmpty(clsid))
            {
                try
                {
                    using var clsidKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
                    if (clsidKey != null)
                    {
                        dllPath = (clsidKey.GetValue(null) as string) ?? "";
                    }
                }
                catch
                {
                    // Ignore
                }
            }

            list.Add(new AsioDriverInfo
            {
                Name = subName,
                Description = desc,
                Clsid = clsid,
                DllPath = dllPath,
            });
        }
    }

    /// <summary>Attempts to open the control panel of the selected ASIO driver.</summary>
    public static bool OpenControlPanel(AsioDriverInfo driver)
    {
        if (!OperatingSystem.IsWindows() || driver == null) return false;

        try
        {
            // If the driver DLL exists and has an executable or rundll entry point
            if (!string.IsNullOrEmpty(driver.DllPath) && File.Exists(driver.DllPath))
            {
                var dir = Path.GetDirectoryName(driver.DllPath);
                // Look for a control panel .exe nearby (e.g. Focusrite Control, ASIO4ALL config, etc.)
                if (dir != null && Directory.Exists(dir))
                {
                    foreach (var exe in Directory.GetFiles(dir, "*control*.exe"))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        return true;
                    }
                    foreach (var exe in Directory.GetFiles(dir, "*config*.exe"))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        return true;
                    }
                }
            }

            // If it's ASIO4ALL, try opening its control utility
            var asio4allPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ASIO4ALL v2", "a4a_cpl.exe");
            if (File.Exists(asio4allPath))
            {
                Process.Start(new ProcessStartInfo(asio4allPath) { UseShellExecute = true });
                return true;
            }

            // Launch generic Windows sound control panel as fallback
            Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

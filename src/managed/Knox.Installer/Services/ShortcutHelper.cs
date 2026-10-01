using System;
using System.Diagnostics;
using System.IO;

namespace Knox.Installer.Services;

public static class ShortcutHelper
{
    public static void CreateShortcut(string shortcutPath, string targetPath, string description, string iconPath)
    {
        try
        {
            string vbsScript = 
                "Set oWS = WScript.CreateObject(\"WScript.Shell\")\r\n" +
                "sLinkFile = \"" + shortcutPath.Replace("\\", "\\\\") + "\"\r\n" +
                "Set oLink = oWS.CreateShortcut(sLinkFile)\r\n" +
                "oLink.TargetPath = \"" + targetPath.Replace("\\", "\\\\") + "\"\r\n" +
                "oLink.WorkingDirectory = \"" + Path.GetDirectoryName(targetPath)?.Replace("\\", "\\\\") + "\"\r\n" +
                "oLink.Description = \"" + description + "\"\r\n" +
                "oLink.IconLocation = \"" + iconPath.Replace("\\", "\\\\") + ", 0\"\r\n" +
                "oLink.Save\r\n";

            string tempVbs = Path.Combine(Path.GetTempPath(), $"knox_shortcut_{Guid.NewGuid():N}.vbs");
            File.WriteAllText(tempVbs, vbsScript);

            var psi = new ProcessStartInfo("wscript.exe", $"\"{tempVbs}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(3000);

            try { File.Delete(tempVbs); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Shortcut creation warning: {ex.Message}");
        }
    }
}
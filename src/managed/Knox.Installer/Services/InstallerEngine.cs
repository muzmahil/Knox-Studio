using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;

namespace Knox.Installer.Services;

public class InstallerEngine
{
    public static string GetDefaultInstallDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", "Knox Studio");
    }

    public async Task InstallAsync(
        string installDir,
        bool createDesktop,
        bool createStartMenu,
        bool associateFiles,
        IProgress<(double progress, string status)> progress)
    {
        await Task.Run(() =>
        {
            progress.Report((0.05, "Hedef kurulum dizini hazirlaniyor..."));
            if (!Directory.Exists(installDir))
            {
                Directory.CreateDirectory(installDir);
            }

            var assembly = Assembly.GetExecutingAssembly();
            using Stream? payloadStream = assembly.GetManifestResourceStream("Knox.Installer.Payload.payload.zip");

            if (payloadStream != null)
            {
                progress.Report((0.15, "Knox Studio dosyalari ayiklaniyor..."));
                using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read);
                int totalEntries = archive.Entries.Count;
                int count = 0;

                foreach (var entry in archive.Entries)
                {
                    count++;
                    double p = 0.15 + (0.65 * count / Math.Max(1, totalEntries));
                    progress.Report((p, $"Ayiklaniyor: {entry.Name}..."));

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        string dirPath = Path.Combine(installDir, entry.FullName);
                        Directory.CreateDirectory(dirPath);
                        continue;
                    }

                    string destPath = Path.Combine(installDir, entry.FullName);
                    string? parentDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }
            else
            {
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                string pubDir = Path.Combine(currentDir, "..", "dist", "publish-x64");
                if (!Directory.Exists(pubDir))
                {
                    pubDir = Path.Combine(currentDir, "publish-x64");
                }

                if (Directory.Exists(pubDir))
                {
                    var files = Directory.GetFiles(pubDir, "*.*", SearchOption.AllDirectories);
                    int count = 0;
                    foreach (var f in files)
                    {
                        count++;
                        double p = 0.15 + (0.65 * count / Math.Max(1, files.Length));
                        string rel = Path.GetRelativePath(pubDir, f);
                        progress.Report((p, $"Kopyalaniyor: {rel}..."));
                        string dest = Path.Combine(installDir, rel);
                        string? pDir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(pDir)) Directory.CreateDirectory(pDir);
                        File.Copy(f, dest, true);
                    }
                }
            }

            string exePath = Path.Combine(installDir, "Knox Studio.exe");
            string icoPath = Path.Combine(installDir, "knox.ico");
            string uninstallerExe = Path.Combine(installDir, "uninstall.exe");

            // Copy running installer as uninstall.exe
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (File.Exists(currentExe))
                {
                    File.Copy(currentExe, uninstallerExe, true);
                }
            }
            catch { }

            if (createDesktop)
            {
                progress.Report((0.85, "Masaustu kisayolu olusturuluyor..."));
                string desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string shortcut = Path.Combine(desktopFolder, "Knox Studio.lnk");
                ShortcutHelper.CreateShortcut(shortcut, exePath, "Knox Studio - Next-Gen Audio Workstation", icoPath);
            }

            if (createStartMenu)
            {
                progress.Report((0.90, "Baslat menusu kisayolu olusturuluyor..."));
                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string shortcut = Path.Combine(startMenu, "Knox Studio.lnk");
                ShortcutHelper.CreateShortcut(shortcut, exePath, "Knox Studio - Next-Gen Audio Workstation", icoPath);
            }

            if (associateFiles)
            {
                progress.Report((0.95, "Windows Kayit Defteri dosya iliskilendirmeleri yapiliyor (.knox, .knoxproj)..."));
                WindowsRegistryHelper.RegisterFileAssociations(installDir);
            }

            WindowsRegistryHelper.RegisterAppPaths(installDir);
            WindowsRegistryHelper.RegisterUninstall(installDir, "0.37.1");

            progress.Report((1.0, "Kurulum tamamlandi!"));
        });
    }

    public static async Task UninstallAsync(string installDir, IProgress<(double progress, string status)>? progress = null)
    {
        await Task.Run(() =>
        {
            progress?.Report((0.1, "Knox Studio durduruluyor..."));
            try
            {
                foreach (var proc in Process.GetProcessesByName("Knox Studio"))
                {
                    try { proc.Kill(); proc.WaitForExit(2000); } catch { }
                }
                foreach (var proc in Process.GetProcessesByName("Knox.App"))
                {
                    try { proc.Kill(); proc.WaitForExit(2000); } catch { }
                }
            }
            catch { }

            progress?.Report((0.3, "Kayit Defteri temizleniyor..."));
            try
            {
                using var classesKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes", true);
                if (classesKey != null)
                {
                    try { classesKey.DeleteSubKeyTree(".knox", false); } catch { }
                    try { classesKey.DeleteSubKeyTree(".knoxproj", false); } catch { }
                    try { classesKey.DeleteSubKeyTree("KnoxStudio.Project", false); } catch { }
                }
                using var appPaths = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths", true);
                if (appPaths != null)
                {
                    try { appPaths.DeleteSubKeyTree("Knox Studio.exe", false); } catch { }
                    try { appPaths.DeleteSubKeyTree("Knox.App.exe", false); } catch { }
                }
                using var uninst = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
                if (uninst != null)
                {
                    try { uninst.DeleteSubKeyTree("KnoxStudio", false); } catch { }
                }
            }
            catch { }

            progress?.Report((0.5, "Kisayollar siliniyor..."));
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string desktopLnk = Path.Combine(desktop, "Knox Studio.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string startLnk = Path.Combine(startMenu, "Knox Studio.lnk");
                if (File.Exists(startLnk)) File.Delete(startLnk);
            }
            catch { }

            progress?.Report((0.7, "Uygulama dosyalari siliniyor..."));
            try
            {
                if (Directory.Exists(installDir))
                {
                    foreach (var file in Directory.GetFiles(installDir, "*.*", SearchOption.AllDirectories))
                    {
                        string fName = Path.GetFileName(file);
                        if (!fName.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
                        }
                    }
                    foreach (var dir in Directory.GetDirectories(installDir, "*", SearchOption.AllDirectories))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }
            catch { }

            progress?.Report((0.9, "Kaldirma tamamlandi."));

            // Background self cleanup for uninstall.exe and remaining empty folder
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"timeout /t 1 /nobreak >nul & rmdir /s /q \\\"{installDir}\\\"\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch { }

            progress?.Report((1.0, "Tamamlandi."));
        });
    }
}
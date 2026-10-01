// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Knox.Application;

/// <summary>
/// Manages plug-in GUI thumbnail snapshots stored in %APPDATA%/Knox/thumbnails/
/// for rendering rich visual covers in the Device rack and browser.
/// </summary>
public static class PluginThumbnailService
{
    private static string? _thumbnailsDir;

    /// <summary>Directory holding all cached plug-in PNG thumbnails.</summary>
    public static string ThumbnailsDirectory
    {
        get
        {
            if (_thumbnailsDir is null)
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (OperatingSystem.IsMacOS())
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    appData = Path.Combine(home, "Library", "Application Support");
                }
                _thumbnailsDir = Path.Combine(appData, "Knox", "thumbnails");
                Directory.CreateDirectory(_thumbnailsDir);
            }
            return _thumbnailsDir;
        }
    }

    /// <summary>Returns a legal filename without invalid path characters.</summary>
    public static string SanitizeFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "plugin";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    /// <summary>Checks if a thumbnail PNG exists for the given plug-in id or name.</summary>
    public static bool HasThumbnail(string? pluginId, string? pluginName)
    {
        var path = TryGetExistingThumbnailPath(pluginId, pluginName);
        return path != null && File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private static string NormalizePluginName(string name)
    {
        string n = name.Trim();
        // Remove common 64-bit suffixes
        string[] suffixes = { "_x64", "-64", "_64", " x64", " (x64)", " (64bit)", " (64-bit)" };
        foreach (var s in suffixes)
        {
            if (n.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                n = n[..^s.Length].Trim();
                break;
            }
        }
        return n;
    }

    /// <summary>Resolves the path to an existing thumbnail, or null if none exists.</summary>
    public static string? TryGetExistingThumbnailPath(string? pluginId, string? pluginName)
    {
        string dir = ThumbnailsDirectory;
        if (!Directory.Exists(dir)) return null;

        // 1. Direct name lookup
        if (!string.IsNullOrWhiteSpace(pluginName))
        {
            string cleanName = SanitizeFilename(pluginName);
            string p = Path.Combine(dir, $"{cleanName}.png");
            if (File.Exists(p) && new FileInfo(p).Length > 0) return p;

            string normName = SanitizeFilename(NormalizePluginName(pluginName));
            if (normName != cleanName)
            {
                string pNorm = Path.Combine(dir, $"{normName}.png");
                if (File.Exists(pNorm) && new FileInfo(pNorm).Length > 0) return pNorm;
            }
        }

        // 2. Direct pluginId lookup
        if (!string.IsNullOrWhiteSpace(pluginId))
        {
            string cleanId = SanitizeFilename(pluginId);
            string p = Path.Combine(dir, $"{cleanId}.png");
            if (File.Exists(p) && new FileInfo(p).Length > 0) return p;

            // Strip format prefix if present (e.g. VST-Serum_x64-58667358 -> Serum_x64)
            var parts = pluginId.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Length > 2 && part != "VST" && part != "VST3" && part != "AU" && !part.All(char.IsDigit) && !part.All(c => "0123456789abcdefABCDEF".Contains(c)))
                {
                    string pPart = Path.Combine(dir, $"{SanitizeFilename(part)}.png");
                    if (File.Exists(pPart) && new FileInfo(pPart).Length > 0) return pPart;
                }
            }

            // Also check if pluginId is a path (e.g. filename)
            string fileName = Path.GetFileNameWithoutExtension(pluginId);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                string cleanFile = SanitizeFilename(fileName);
                string p2 = Path.Combine(dir, $"{cleanFile}.png");
                if (File.Exists(p2) && new FileInfo(p2).Length > 0) return p2;

                string normFile = SanitizeFilename(NormalizePluginName(fileName));
                if (normFile != cleanFile)
                {
                    string pNormFile = Path.Combine(dir, $"{normFile}.png");
                    if (File.Exists(pNormFile) && new FileInfo(pNormFile).Length > 0) return pNormFile;
                }
            }
        }

        // 3. Smart loose / fuzzy scan in directory
        string searchKey = !string.IsNullOrWhiteSpace(pluginName) ? pluginName : (pluginId ?? "");
        if (!string.IsNullOrWhiteSpace(searchKey) && searchKey.Length >= 3 && searchKey != "Plugin" && searchKey != "Device")
        {
            string keyNorm = CleanAlphaNumeric(searchKey);
            try
            {
                var files = Directory.GetFiles(dir, "*.png");
                foreach (var f in files)
                {
                    string baseName = Path.GetFileNameWithoutExtension(f);
                    if (baseName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)) baseName = baseName.Substring(5);
                    string fileNorm = CleanAlphaNumeric(baseName);
                    if (fileNorm.Length >= 3 && (keyNorm.Contains(fileNorm) || fileNorm.Contains(keyNorm)))
                    {
                        if (new FileInfo(f).Length > 0) return f;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    private static string CleanAlphaNumeric(string s)
    {
        var chars = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    /// <summary>Returns the canonical target thumbnail file path for saving.</summary>
    public static string GetCanonicalThumbnailPath(string? pluginId, string? pluginName)
    {
        string dir = ThumbnailsDirectory;
        string name = !string.IsNullOrWhiteSpace(pluginName) ? pluginName : (pluginId ?? "plugin");
        return Path.Combine(dir, $"{SanitizeFilename(name)}.png");
    }

    /// <summary>Captures a live snapshot from an open editor window on a track device.</summary>
    public static bool CaptureLiveSnapshot(IAudioEngine engine, int trackId, int deviceIndex, string? pluginId, string? pluginName)
    {
        string dir = ThumbnailsDirectory;
        Directory.CreateDirectory(dir);

        string canonical = GetCanonicalThumbnailPath(pluginId, pluginName);
        bool ok = false;
        try
        {
            ok = engine.CapturePluginEditorSnapshot(trackId, deviceIndex, canonical);
        }
        catch { }

        // If live capture did not produce a file (e.g. window closed or minimized), fallback to scanworker
        if (!ok || !File.Exists(canonical) || new FileInfo(canonical).Length == 0)
        {
            string targetName = !string.IsNullOrWhiteSpace(pluginName) ? pluginName : (pluginId ?? "plugin");
            string targetFileOrId = !string.IsNullOrWhiteSpace(pluginId) ? pluginId : targetName;
            ok = TryCaptureViaScanWorker("ANY", targetFileOrId, canonical, targetName, 10000);
        }

        if (ok && File.Exists(canonical) && new FileInfo(canonical).Length > 0)
        {
            CopyAliases(canonical, pluginId, pluginName);
            return true;
        }
        return false;
    }

    /// <summary>Captures an offline thumbnail for a catalog plug-in by index with full crash isolation.</summary>
    public static bool CaptureCatalogThumbnail(IPluginCatalog catalog, int index)
    {
        if (index < 0 || index >= catalog.Count) return false;

        string? id = catalog.Id(index);
        string? file = catalog.FilePath(index);
        string? format = catalog.Format(index);
        string? name = catalog.Name(index);

        if (string.IsNullOrWhiteSpace(file)) file = id;
        if (string.IsNullOrWhiteSpace(name)) name = id ?? "plugin";
        if (string.IsNullOrWhiteSpace(format)) format = "ANY";

        string dir = ThumbnailsDirectory;
        Directory.CreateDirectory(dir);

        string target = GetCanonicalThumbnailPath(id, name);

        // Try crash-isolated scanworker child process with 10s timeout
        bool ok = TryCaptureViaScanWorker(format, file!, target, name, 10000);

        if (ok && File.Exists(target) && new FileInfo(target).Length > 0)
        {
            CopyAliases(target, id, name);
            if (!string.IsNullOrWhiteSpace(file))
            {
                string fn = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(fn)) CopyAliases(target, null, fn);
            }
            return true;
        }
        return false;
    }

    /// <summary>Captures an offline thumbnail for a catalog plug-in.</summary>
    public static bool CaptureCatalogThumbnail(IPluginCatalog catalog, string identifier, string pluginName)
    {
        int idx = catalog.IndexOfId(identifier);
        if (idx >= 0) return CaptureCatalogThumbnail(catalog, idx);

        string dir = ThumbnailsDirectory;
        Directory.CreateDirectory(dir);

        string target = GetCanonicalThumbnailPath(identifier, pluginName);

        // Try crash-isolated scanworker child process first
        bool ok = TryCaptureViaScanWorker("ANY", identifier, target, pluginName, 10000);

        if (ok && File.Exists(target) && new FileInfo(target).Length > 0)
        {
            CopyAliases(target, identifier, pluginName);
            return true;
        }
        return false;
    }

    private static void CopyAliases(string sourcePath, string? id, string? name)
    {
        string dir = ThumbnailsDirectory;
        try
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string cleanName = SanitizeFilename(name);
                string pName = Path.Combine(dir, $"{cleanName}.png");
                if (pName != sourcePath && !File.Exists(pName))
                {
                    File.Copy(sourcePath, pName, true);
                }

                string normName = SanitizeFilename(NormalizePluginName(name));
                if (normName != cleanName)
                {
                    string pNorm = Path.Combine(dir, $"{normName}.png");
                    if (pNorm != sourcePath)
                    {
                        File.Copy(sourcePath, pNorm, true);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                string idPath = Path.Combine(dir, $"{SanitizeFilename(id)}.png");
                if (idPath != sourcePath)
                {
                    File.Copy(sourcePath, idPath, true);
                }
            }
        }
        catch { }
    }

    private static bool TryCaptureViaScanWorker(string format, string fileOrId, string outPngPath, string pluginName, int timeoutMs = 10000)
    {
        try
        {
            string workerName = OperatingSystem.IsWindows() ? "knox-scanworker.exe" : "knox-scanworker";
            string workerPath = Path.Combine(AppContext.BaseDirectory, workerName);

            if (!File.Exists(workerPath))
            {
                string[] candidates = {
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "native", "knox.engine", "build", "Release", workerName),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "native", "knox.engine", "build", "Release", workerName),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "native", "knox.engine", "build", workerName),
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) { workerPath = Path.GetFullPath(c); break; }
                }
            }

            if (!File.Exists(workerPath)) return false;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--thumbnail");
            psi.ArgumentList.Add(format);
            psi.ArgumentList.Add(fileOrId);
            psi.ArgumentList.Add(outPngPath);
            psi.ArgumentList.Add(pluginName);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;

            if (proc.WaitForExit(timeoutMs))
            {
                return proc.ExitCode == 0 && File.Exists(outPngPath) && new FileInfo(outPngPath).Length > 0;
            }
            else
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }
}

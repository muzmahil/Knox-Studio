// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System.Runtime.InteropServices;

namespace Knox.Infrastructure;

public sealed partial class KnoxEngine
{
    // --- Plugin hosting spike (M3-0) ---------------------------------------

    /// <summary>
    /// Opens a JUCE test window to verify the JUCE message loop coexists with
    /// the Avalonia run loop (M3-0 spike). Must be called from the UI thread.
    /// </summary>
    public static void OpenPluginHostTestWindow() => Check(NativeMethods.PluginHostOpenTestWindow());

    // --- Plugin scanning & catalog (M3-1) ----------------------------------

    public delegate bool PluginScanProgressCallback(string formatName, string fileOrId, int currentIdx, int totalCount);

    /// <summary>
    /// Scans installed AU/VST3/VST2 plugins out-of-process using the given worker
    /// executable. Blocking. Returns the number of known plugins.
    /// </summary>
    public static int ScanPlugins(string workerPath, bool validate = true)
    {
        int n = NativeMethods.PluginHostScanEx(workerPath, validate ? 1 : 0);
        if (n < 0) throw new KnoxEngineException($"Plugin scan failed (code {n}).");
        return n;
    }

    /// <summary>
    /// Scans installed AU/VST3/VST2 plugins out-of-process with a progress callback.
    /// Returns the number of known plugins.
    /// </summary>
    public static int ScanPluginsWithProgress(string workerPath, bool validate, PluginScanProgressCallback? callback)
    {
        NativeMethods.PluginScanCallbackNative? nativeCb = null;
        if (callback != null)
        {
            nativeCb = (fmtPtr, filePtr, curr, total) =>
            {
                string fmt = Marshal.PtrToStringUTF8(fmtPtr) ?? "";
                string file = Marshal.PtrToStringUTF8(filePtr) ?? "";
                bool cont = callback(fmt, file, curr, total);
                return cont ? 0 : 1;
            };
        }
        int n = NativeMethods.PluginHostScanWithProgress(workerPath, validate ? 1 : 0, nativeCb);
        if (n < 0) throw new KnoxEngineException($"Plugin scan failed (code {n}).");
        return n;
    }

    /// <summary>Skips the current plugin being scanned in the child process.</summary>
    public static void SkipCurrentPluginScan() => NativeMethods.PluginHostSkipCurrentScan();

    /// <summary>Cancels the plugin scan process.</summary>
    public static void CancelPluginScan() => NativeMethods.PluginHostCancelScan();

    /// <summary>Captures a GUI screenshot/thumbnail PNG of a plugin by identifier.</summary>
    public static bool CapturePluginThumbnail(string identifier, string outPngPath)
        => NativeMethods.PluginHostCapturePluginThumbnail(identifier, outPngPath) != 0;

    /// <summary>Number of plugins currently in the catalog.</summary>
    public static int PluginCount => NativeMethods.PluginHostPluginCount();

    /// <summary>Human-readable catalog entry ("Name | Format | inst|fx | Manufacturer"), or null.</summary>
    public static string? PluginDescription(int index)
        => Marshal.PtrToStringUTF8(NativeMethods.PluginHostPluginDesc(index));

    /// <summary>Stable identifier for a catalog entry (for project save), or null (M7-6c).</summary>
    public static string? PluginId(int index)
        => Marshal.PtrToStringUTF8(NativeMethods.PluginHostPluginId(index));

    /// <summary>File path on disk for a catalog entry, or null.</summary>
    public static string? PluginFile(int index)
        => Marshal.PtrToStringUTF8(NativeMethods.PluginHostPluginFile(index));

    /// <summary>Plugin format name (e.g. VST3, VST, AudioUnit) for a catalog entry, or null.</summary>
    public static string? PluginFormat(int index)
        => Marshal.PtrToStringUTF8(NativeMethods.PluginHostPluginFormat(index));

    /// <summary>Plugin display name for a catalog entry, or null.</summary>
    public static string? PluginName(int index)
        => Marshal.PtrToStringUTF8(NativeMethods.PluginHostPluginName(index));

    /// <summary>Catalog index whose identifier matches, or -1 if the plugin isn't installed.</summary>
    public static int PluginIndexOfId(string identifier)
        => NativeMethods.PluginHostIndexOfId(identifier);

    /// <summary>Adds an extra plugin search directory (persisted; used on the next scan).</summary>
    public static void AddScanPath(string dir) => Check(NativeMethods.PluginHostAddScanPath(dir));

    /// <summary>Removes the extra scan directory at <paramref name="index"/>.</summary>
    public static void RemoveScanPath(int index) => Check(NativeMethods.PluginHostRemoveScanPath(index));

    /// <summary>Number of user-added scan directories.</summary>
    public static int ScanPathCount => NativeMethods.PluginHostScanPathCount();

    /// <summary>The extra scan directory at <paramref name="index"/>, or null.</summary>
    public static string? ScanPath(int index)
        => Marshal.PtrToStringUTF8(NativeMethods.PluginHostScanPath(index));

    /// <summary>Sets the octave transpose for virtual typing keyboard in VST host windows.</summary>
    public static void SetTypingOctave(int octave) => NativeMethods.PluginHostSetTypingOctave(octave);

    /// <summary>Enables or disables virtual typing keyboard interception in VST host windows.</summary>
    public static void SetTypingActive(bool active) => NativeMethods.PluginHostSetTypingActive(active ? 1 : 0);
}


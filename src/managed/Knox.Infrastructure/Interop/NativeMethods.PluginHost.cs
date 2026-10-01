// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System.Runtime.InteropServices;

namespace Knox.Infrastructure;

/// <summary>Plugin hosting: scan worker, catalog, scan paths (M3-0/M3-1).</summary>
/// <remarks>Part of <see cref="KnoxEngine"/>'s P/Invoke surface; see knox_engine.h.</remarks>
internal static partial class NativeMethods
{
    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_open_test_window")]
    internal static partial NotaResult PluginHostOpenTestWindow();

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_scan", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PluginHostScan(string workerPath);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_scan_ex", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PluginHostScanEx(string workerPath, int validate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PluginScanCallbackNative(IntPtr formatName, IntPtr fileOrId, int currentIdx, int totalCount);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_scan_with_progress", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PluginHostScanWithProgress(string workerPath, int validate, PluginScanCallbackNative? callback);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_skip_current_scan")]
    internal static partial void PluginHostSkipCurrentScan();

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_cancel_scan")]
    internal static partial void PluginHostCancelScan();

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_capture_plugin_thumbnail", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PluginHostCapturePluginThumbnail(string identifier, string outPngPath);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_plugin_count")]
    internal static partial int PluginHostPluginCount();

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_plugin_desc")]
    internal static partial IntPtr PluginHostPluginDesc(int index);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_plugin_id")]
    internal static partial IntPtr PluginHostPluginId(int index);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_plugin_file")]
    internal static partial IntPtr PluginHostPluginFile(int index);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_plugin_format")]
    internal static partial IntPtr PluginHostPluginFormat(int index);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_plugin_name")]
    internal static partial IntPtr PluginHostPluginName(int index);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_index_of_id", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PluginHostIndexOfId(string identifier);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_add_scan_path", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial NotaResult PluginHostAddScanPath(string dir);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_remove_scan_path")]
    internal static partial NotaResult PluginHostRemoveScanPath(int index);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_scan_path_count")]
    internal static partial int PluginHostScanPathCount();

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_scan_path")]
    internal static partial IntPtr PluginHostScanPath(int index);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_set_typing_octave")]
    internal static partial void PluginHostSetTypingOctave(int octave);

    [LibraryImport(Lib, EntryPoint = "nota_pluginhost_set_typing_active")]
    internal static partial void PluginHostSetTypingActive(int active);
}

// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

namespace Knox.Application;

public delegate bool PluginScanProgressHandler(string formatName, string fileOrId, int currentIdx, int totalCount);

/// <summary>The scanned AU/VST3 plugin catalog and its user scan paths. Wraps the
/// engine's process-wide plugin host so ViewModels stay Infrastructure-free.</summary>
public interface IPluginCatalog
{
    /// <summary>Rescans installed plugins out-of-process. Returns the known count.</summary>
    int Scan(string workerPath, bool validate = true);
    /// <summary>Rescans installed plugins out-of-process with progress callback. Returns known count.</summary>
    int ScanWithProgress(string workerPath, bool validate, PluginScanProgressHandler? progressHandler);
    /// <summary>Skips the current plugin being scanned.</summary>
    void SkipCurrentScan();
    /// <summary>Cancels the overall plugin scan.</summary>
    void CancelScan();
    /// <summary>Number of plugins currently in the catalog.</summary>
    int Count { get; }
    /// <summary>Human-readable entry ("Name | Format | inst|fx | Manufacturer"), or null.</summary>
    string? Description(int index);
    /// <summary>Stable identifier for a catalog entry, or null.</summary>
    string? Id(int index);
    /// <summary>File path on disk for a catalog entry, or null.</summary>
    string? FilePath(int index);
    /// <summary>Format name (e.g. VST3, VST, AudioUnit) for a catalog entry, or null.</summary>
    string? Format(int index);
    /// <summary>Plugin name for a catalog entry, or null.</summary>
    string? Name(int index);
    /// <summary>Catalog index whose identifier matches, or -1 if not installed.</summary>
    int IndexOfId(string identifier);
    /// <summary>Captures a GUI thumbnail/screenshot PNG for a plugin by identifier.</summary>
    bool CaptureThumbnail(string identifier, string outPngPath);

    void AddScanPath(string dir);
    void RemoveScanPath(int index);
    int ScanPathCount { get; }
    string? ScanPath(int index);
}

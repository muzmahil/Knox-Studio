// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

namespace Knox.Infrastructure;

/// <summary>IPluginCatalog over the engine's process-wide plugin host statics.</summary>
public sealed class PluginCatalog : IPluginCatalog
{
    public int Scan(string workerPath, bool validate = true) => KnoxEngine.ScanPlugins(workerPath, validate);
    public int ScanWithProgress(string workerPath, bool validate, PluginScanProgressHandler? progressHandler)
    {
        return KnoxEngine.ScanPluginsWithProgress(workerPath, validate, progressHandler != null ? (fmt, file, curr, total) => progressHandler(fmt, file, curr, total) : null);
    }
    public void SkipCurrentScan() => KnoxEngine.SkipCurrentPluginScan();
    public void CancelScan() => KnoxEngine.CancelPluginScan();
    public int Count => KnoxEngine.PluginCount;
    public string? Description(int index) => KnoxEngine.PluginDescription(index);
    public string? Id(int index) => KnoxEngine.PluginId(index);
    public string? FilePath(int index) => KnoxEngine.PluginFile(index);
    public string? Format(int index) => KnoxEngine.PluginFormat(index);
    public string? Name(int index) => KnoxEngine.PluginName(index);
    public int IndexOfId(string identifier) => KnoxEngine.PluginIndexOfId(identifier);
    public bool CaptureThumbnail(string identifier, string outPngPath) => KnoxEngine.CapturePluginThumbnail(identifier, outPngPath);
    public void AddScanPath(string dir) => KnoxEngine.AddScanPath(dir);
    public void RemoveScanPath(int index) => KnoxEngine.RemoveScanPath(index);
    public int ScanPathCount => KnoxEngine.ScanPathCount;
    public string? ScanPath(int index) => KnoxEngine.ScanPath(index);
}

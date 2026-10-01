// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Single source of truth for Nota's per-user data directory, shared by the
// settings, recovery and logging services (and mirrored by the native engine's
// audio.json / midi.json writers). Platform conventions:
//   macOS   -> ~/Library/Application Support/Nota   (unchanged from the mac-only era)
//   Windows -> %APPDATA%\Nota                       (Roaming AppData)
//   Linux   -> $XDG_CONFIG_HOME/nota or ~/.config/nota
// The native side resolves the same locations (SHGetKnownFolderPath / NSHomeDirectory)
// so config written by C++ and C# lands in one place.

namespace Knox.Infrastructure;

public static class KnoxPaths
{
    /// <summary>The per-user Knox Studio data root, created if missing.</summary>
    public static string DataDir { get; } = ResolveDataDir();

    /// <summary>User home directory.</summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Ensures and returns a subfolder of <see cref="DataDir"/> (e.g. "logs", "presets").</summary>
    public static string SubDir(string name)
    {
        var dir = Path.Combine(DataDir, name);
        try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
        return dir;
    }

    private static string ResolveDataDir()
    {
        string root;
        if (OperatingSystem.IsWindows())
        {
            // %APPDATA% == Roaming AppData; on Windows SpecialFolder.ApplicationData maps here.
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Knox Studio");
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(Home, "Library", "Application Support", "Knox Studio");
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".config") : xdg;
            root = Path.Combine(baseDir, "knox-studio");
        }

        try { Directory.CreateDirectory(root); } catch { /* best-effort */ }
        return root;
    }
}

/// <summary>Backward compatibility alias for KnoxPaths.</summary>
public static class NotaPaths
{
    public static string DataDir => KnoxPaths.DataDir;
    public static string Home => KnoxPaths.Home;
    public static string SubDir(string name) => KnoxPaths.SubDir(name);
}

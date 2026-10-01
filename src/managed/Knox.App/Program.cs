// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota/Knox). See LICENSES/ for license terms.

using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Knox.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            InitAssemblyResolver();
            AppMain(args);
        }
        catch (Exception ex)
        {
            try
            {
                var errFile = Path.Combine(AppContext.BaseDirectory, "launch_error.txt");
                File.WriteAllText(errFile, ex.ToString());
            }
            catch { }
            throw;
        }
    }

    private static void InitAssemblyResolver()
    {
        var baseDir = AppContext.BaseDirectory;
        var libDir = Path.Combine(baseDir, "lib");

        // Load managed assemblies from the lib/ subfolder
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            var candidate = Path.Combine(libDir, $"{assemblyName.Name}.dll");
            if (File.Exists(candidate))
            {
                return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };

        // Load native libraries (knox_engine, etc.) from root, lib/, or runtimes
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (libraryName, assembly, searchPath) =>
        {
            var ext = OperatingSystem.IsWindows() ? ".dll" : (OperatingSystem.IsMacOS() ? ".dylib" : ".so");
            var prefix = OperatingSystem.IsWindows() ? "" : "lib";
            var candidates = new[]
            {
                Path.Combine(baseDir, $"{libraryName}{ext}"),
                Path.Combine(baseDir, $"{prefix}{libraryName}{ext}"),
                Path.Combine(libDir, $"{libraryName}{ext}"),
                Path.Combine(libDir, $"{prefix}{libraryName}{ext}")
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c) && NativeLibrary.TryLoad(c, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AppMain(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software },
                CompositionMode = new[] { Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.LowLatencyDxgiSwapChain }
            })
            .With(new X11PlatformOptions
            {
                RenderingMode = new[] { X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software }
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

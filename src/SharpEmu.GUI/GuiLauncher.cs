// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.GUI;

/// <summary>
/// Entry point for the desktop frontend, hosted by the SharpEmu executable
/// when it is started without command-line arguments.
/// </summary>
public static class GuiLauncher
{
    public static int Run()
    {
        try
        {
            using var instance = SingleInstance.TryAcquire(out var anotherIsRunning);
            if (anotherIsRunning)
            {
                return 0;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
            return 0;
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Popups, menus and tooltips are their own top levels and don't inherit the
            // window's font, so the bundled Inter is made the app-wide default.
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();

    public static void ConfigureSplashFont()
    {
        try
        {
            using var font = new StandardAssetLoader().Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf"));
            HostVideoHost.ConfigureSplashFont(font);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] The splash font cannot be loaded: {exception.Message}");
        }
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "gui-crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Crash logging is best-effort.
        }
    }
}

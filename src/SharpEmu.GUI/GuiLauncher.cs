// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;

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
            .LogToTrace();

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "gui-crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Crash logging is best-effort.
        }
    }
}

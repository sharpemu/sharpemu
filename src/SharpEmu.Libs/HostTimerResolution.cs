// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.Libs;

public static class HostTimerResolution
{
    private const uint TargetPeriodMilliseconds = 1;

    private static int _requested;

    public static void Request()
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (TimeBeginPeriod(TargetPeriodMilliseconds) != 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Host timer resolution request rejected; " +
                    "timed waits keep the default ~15.6 ms granularity.");
            }
        }
        catch (DllNotFoundException exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Host timer resolution unavailable: {exception.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
    private static extern uint TimeBeginPeriod(uint uPeriod);
}

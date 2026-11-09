// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal static class GpuWaitSpin
{
    private const long DefaultMicroseconds = 150;

    internal static readonly long SpinTicks = ReadSpinTicks(Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_SPIN_US"));

    internal static long ReadSpinTicks(string? value)
    {
        var microseconds = long.TryParse(value, out var parsed) && parsed >= 0 ? parsed : DefaultMicroseconds;
        return microseconds * Stopwatch.Frequency / 1_000_000;
    }

    public static bool TrySpin(Func<bool> done)
    {
        if (SpinTicks <= 0)
        {
            return false;
        }

        var deadline = Stopwatch.GetTimestamp() + SpinTicks;
        do
        {
            if (done())
            {
                return true;
            }

            Thread.SpinWait(32);
        }
        while (Stopwatch.GetTimestamp() < deadline);

        return done();
    }
}

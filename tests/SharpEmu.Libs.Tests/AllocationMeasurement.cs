// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.ExceptionServices;

namespace SharpEmu.Libs.Tests;

internal static class AllocationMeasurement
{
    // Bytes allocated by the least-allocating of several rounds, measured on a fresh thread.
    // Test-runner work cannot enter the sample there, and a one-off runtime allocation
    // (tiered recompilation on a busy machine) lands in one round only, while storage the
    // measured code allocates shows in every round.
    public static long SteadyState(Action? warmup, Action round, int rounds = 5)
    {
        var least = long.MaxValue;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                warmup?.Invoke();
                for (var index = 0; index < rounds && least != 0; index++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    round();
                    least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                }
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.Start();
        thread.Join();
        failure?.Throw();
        return least;
    }
}

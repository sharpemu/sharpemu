// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementCollection
{
    public const string Name = "AllocationMeasurement";

    internal static long Measure(Action operation)
    {
        // Background collection can charge unused allocation space to the thread counter.
        // Reserve a small test budget and prevent collection only during measurement.
        Assert.True(GC.TryStartNoGCRegion(1024 * 1024), "The allocation measurement could not start.");
        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            operation();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        finally
        {
            GC.EndNoGCRegion();
        }
    }
}

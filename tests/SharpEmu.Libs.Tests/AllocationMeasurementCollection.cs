// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime;
using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurementCollection
{
    public const string Name = "AllocationMeasurement";

    internal static long Measure(Action operation)
    {
        // Background collection can charge unused allocation space to the thread counter.
        // The process-wide budget must also cover allocations from background workers.
        Assert.True(GC.TryStartNoGCRegion(64 * 1024 * 1024), "The allocation measurement could not start.");
        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            operation();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(GCSettings.LatencyMode == GCLatencyMode.NoGCRegion,
                "Collection invalidated the allocation measurement.");
            return allocated;
        }
        finally
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                GC.EndNoGCRegion();
        }
    }
}

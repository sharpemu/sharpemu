// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Tests.Memory.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class GuestMemoryProfileTests
{
    [Fact]
    public void ReportUsesThePerformanceProfileSwitch()
    {
        GuestMemoryProfile.WriteReport();
        using var output = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(output);
            using (GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.ReservationHost)) { }
            GuestMemoryProfile.WriteReport();
        }
        finally
        {
            Console.SetError(previous);
        }

        var enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
        if (enabled)
            Assert.Contains("operation=ReservationHost calls=1 inclusive_ms=", output.ToString());
        else
            Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void SnapshotReportsCountTotalAndMaximumThenClearsTheWindow()
    {
        var measurements = new GuestMemoryProfile.Measurements();
        measurements.Record(GuestMemoryProfile.Operation.MappingDrain, 7);
        measurements.Record(GuestMemoryProfile.Operation.MappingDrain, 11);

        var snapshot = measurements.TakeSnapshot();
        Assert.Equal(new GuestMemoryProfile.Measurement(2, 18, 11),
            snapshot[(int)GuestMemoryProfile.Operation.MappingDrain]);
        Assert.All(measurements.TakeSnapshot(), value => Assert.Equal(default, value));
        Assert.Equal(2, snapshot[(int)GuestMemoryProfile.Operation.MappingDrain].Calls);
    }

    [Fact]
    public void NestedOperationTotalsRemainSeparate()
    {
        var measurements = new GuestMemoryProfile.Measurements();
        measurements.Record(GuestMemoryProfile.Operation.ReservationSearch, 4);
        measurements.Record(GuestMemoryProfile.Operation.ReservationHost, 9);
        measurements.Record(GuestMemoryProfile.Operation.Reservation, 20);

        var snapshot = measurements.TakeSnapshot();
        Assert.Equal(4, snapshot[(int)GuestMemoryProfile.Operation.ReservationSearch].Ticks);
        Assert.Equal(9, snapshot[(int)GuestMemoryProfile.Operation.ReservationHost].Ticks);
        Assert.Equal(20, snapshot[(int)GuestMemoryProfile.Operation.Reservation].Ticks);
    }

    [Fact]
    public void ConcurrentRecordsAndReportsDoNotLoseCalls()
    {
        var measurements = new GuestMemoryProfile.Measurements();
        long calls = 0;
        long ticks = 0;
        Parallel.For(0, 1000, iteration =>
        {
            measurements.Record(GuestMemoryProfile.Operation.MappingApply, 3);
            if (iteration % 10 != 0)
                return;
            var value = measurements.TakeSnapshot()[(int)GuestMemoryProfile.Operation.MappingApply];
            Interlocked.Add(ref calls, value.Calls);
            Interlocked.Add(ref ticks, value.Ticks);
        });
        var remaining = measurements.TakeSnapshot()[(int)GuestMemoryProfile.Operation.MappingApply];
        Assert.Equal(1000, calls + remaining.Calls);
        Assert.Equal(3000, ticks + remaining.Ticks);
    }
}

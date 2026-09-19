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
    public void ReadbackDetailsSeparateCallersForTheSameRange()
    {
        var measurements = new GuestMemoryProfile.ReadbackMeasurements();
        var commandRange = new GuestMemoryProfile.ReadbackRange(0x200000, 0x4000, false,
            GuestMemoryProfile.ReadbackSource.CommandMemoryRead);
        var shaderRange = commandRange with { Source = GuestMemoryProfile.ReadbackSource.ShaderResourceRead };
        measurements.Record(commandRange, 608, 11);
        measurements.Record(shaderRange, 780, 7);
        measurements.Record(commandRange, 608, 3);

        var snapshot = measurements.TakeSnapshot();
        Assert.Equal(2, snapshot.Ranges.Length);
        Assert.Equal(new GuestMemoryProfile.ReadbackMeasurement(2, 2, 1216, 14, 11),
            snapshot.Ranges.Single(entry => entry.Key == commandRange).Value);
        Assert.Equal(new GuestMemoryProfile.ReadbackMeasurement(1, 1, 780, 7, 7),
            snapshot.Ranges.Single(entry => entry.Key == shaderRange).Value);
        Assert.Empty(measurements.TakeSnapshot().Ranges);
    }

    [Fact]
    public void ReadbackReportRequiresBothSwitchesAndIncludesTheCaller()
    {
        GuestMemoryProfile.WriteReport();
        using var output = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(output);
            GuestMemoryProfile.RecordBufferReadback(0x200000, 0x4000, false, 608, 1,
                GuestMemoryProfile.ReadbackSource.ShaderResourceRead);
            GuestMemoryProfile.WriteReport();
        }
        finally
        {
            Console.SetError(previous);
        }

        var enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" &&
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE") == "1";
        if (enabled)
            Assert.Contains("source=ShaderResourceRead calls=1 downloads=1 bytes=608", output.ToString());
        else
            Assert.DoesNotContain("[BUFFER_READBACK]", output.ToString());
    }

    [Fact]
    public void ReadbackDetailsSeparateDownloadsFromCleanRequests()
    {
        var measurements = new GuestMemoryProfile.ReadbackMeasurements();
        var range = new GuestMemoryProfile.ReadbackRange(0x200000, 0x80000, false);
        measurements.Record(range, 4096, 11);
        measurements.Record(range, 0, 3);
        measurements.Record(range with { CpuWrite = true }, 8192, 7);

        var snapshot = measurements.TakeSnapshot();
        Assert.Equal(new GuestMemoryProfile.ReadbackMeasurement(2, 1, 4096, 14, 11),
            snapshot.Ranges.Single(entry => entry.Key == range).Value);
        Assert.Equal(2, snapshot.Ranges.Length);
        Assert.Equal(0, snapshot.UntrackedCalls);
        Assert.Empty(measurements.TakeSnapshot().Ranges);
    }

    [Fact]
    public void ReadbackDetailsBoundDistinctRangesAndReportOverflow()
    {
        var measurements = new GuestMemoryProfile.ReadbackMeasurements();
        for (var index = 0; index <= GuestMemoryProfile.ReadbackMeasurements.Capacity; index++)
            measurements.Record(new GuestMemoryProfile.ReadbackRange((ulong)index * 0x80000, 0x80000, false), 1, 1);
        measurements.Record(new GuestMemoryProfile.ReadbackRange(0, 0x80000, false), 1, 1);

        var snapshot = measurements.TakeSnapshot();
        Assert.Equal(GuestMemoryProfile.ReadbackMeasurements.Capacity, snapshot.Ranges.Length);
        Assert.Equal(1, snapshot.UntrackedCalls);
        Assert.Equal(2, snapshot.Ranges.Single(entry => entry.Key.Address == 0).Value.Calls);
        Assert.Equal(0, measurements.TakeSnapshot().UntrackedCalls);
    }

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

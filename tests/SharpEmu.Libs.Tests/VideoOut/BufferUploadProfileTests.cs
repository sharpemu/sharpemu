// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class BufferUploadProfileTests
{
    [Fact]
    public void CollectionRequiresBothProfileSwitchesAndReportsOnlyOnce()
    {
        BufferUploadProfile.Report(TextWriter.Null);
        var expectedEnabled = (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ||
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1") &&
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE") == "1";
        Assert.Equal(expectedEnabled, BufferUploadProfile.Enabled);
        using (BufferUploadProfile.BeginSweep(11, 22, 0))
            BufferUploadProfile.Record(0x1000, 4096, 1, 4096, 1024, Stopwatch.Frequency);
        using var writer = new StringWriter();
        BufferUploadProfile.Report(writer);
        if (expectedEnabled)
        {
            Assert.Contains("[PERF][BUFFER_SYNC] source=device-address", writer.ToString());
            Assert.Contains("bytes=4096 hot_bytes=1024 sync_ms=1000.00", writer.ToString());
        }
        else
            Assert.Equal(string.Empty, writer.ToString());
        writer.GetStringBuilder().Clear();
        BufferUploadProfile.Report(writer);
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void NestedScopesRestoreSourceAfterException()
    {
        BufferUploadProfile.Report(TextWriter.Null);
        using (BufferUploadProfile.BeginSweep(11, 22, 0))
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var innerScope = BufferUploadProfile.BeginSweep(0, 0, 33);
                BufferUploadProfile.Record(0x1000, 4096, 0, 0, 0, 1);
                throw new InvalidOperationException();
            }));
            BufferUploadProfile.Record(0x2000, 4096, 0, 0, 0, 2);
        }
        BufferUploadProfile.Record(0x3000, 4096, 0, 0, 0, 3);
        using var writer = new StringWriter();
        BufferUploadProfile.Report(writer);
        if (!BufferUploadProfile.Enabled)
        {
            Assert.Equal(string.Empty, writer.ToString());
            return;
        }
        var lines = writer.ToString().Split(Environment.NewLine);
        Assert.Contains(lines, line => line.Contains("compute=0x0000000000000021 address=0x0000000000001000"));
        Assert.Contains(lines, line => line.Contains("vertex=0x000000000000000B pixel=0x0000000000000016 compute=0x0000000000000000 address=0x0000000000002000"));
        Assert.Contains(lines, line => line.Contains("source=other") && line.Contains("address=0x0000000000003000"));
    }

    [Fact]
    public void ReportUsesInvariantDecimalSeparator()
    {
        BufferUploadProfile.Report(TextWriter.Null);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            BufferUploadProfile.Record(0x1000, 4096, 1, 4096, 0, Stopwatch.Frequency);
            using var writer = new StringWriter();
            BufferUploadProfile.Report(writer);
            if (BufferUploadProfile.Enabled)
            {
                Assert.Contains("sync_ms=1000.00", writer.ToString());
                Assert.DoesNotContain("sync_ms=1000,", writer.ToString());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            BufferUploadProfile.Report(TextWriter.Null);
        }
    }

    [Fact]
    public void SlowSamplesRetainLateCallsAndCleanChecksAfterRangeOverflow()
    {
        var counters = new BufferUploadProfile.Counters();
        var source = new BufferUploadProfile.Source(true, 0, 0, 9);
        for (uint index = 0; index < BufferUploadProfile.Counters.Capacity + 20; index++)
            counters.Record(source, index * 4096, 4096, 1, 4096, 0, index, recordSlowSample: true);
        counters.Record(source, 0xF00000, 4096, 0, 0, 0, 1000, recordSlowSample: true);

        Assert.Equal(BufferUploadProfile.Counters.Capacity, counters.Uploads.Count);
        Assert.Equal(BufferUploadProfile.Counters.SlowSampleCapacity, counters.SlowSampleCount);
        Assert.Equal(0xF00000ul, counters.SlowSamples[0].Key.Address);
        Assert.Equal(0, counters.SlowSamples[0].Copies);
        Assert.True(counters.SlowSamples[0].Timestamp > 0);
        for (var index = 1; index < counters.SlowSampleCount; index++)
        {
            Assert.Equal(148 - index, counters.SlowSamples[index].Ticks);
            Assert.Equal((ulong)(148 - index) * 4096, counters.SlowSamples[index].Key.Address);
        }
    }

    [Fact]
    public void SlowSamplesRequireDetailedCollectionAndKeepDurationOrder()
    {
        var counters = new BufferUploadProfile.Counters();
        counters.Record(default, 0x1000, 4096, 0, 0, 0, 999);
        Assert.Equal(0, counters.SlowSampleCount);
        foreach (var ticks in new long[] { 2, 5, 1, 3, 4, 5, 0, 9, 8, 7, 6 })
            counters.Record(default, 0x2000, 4096, 0, 0, 0, ticks, recordSlowSample: true);
        Assert.Equal(new long[] { 9, 8, 7, 6, 5, 5, 4, 3 }, counters.SlowSamples.Select(sample => sample.Ticks));
    }

    [Fact]
    public void SlowSampleUpdatesDoNotAllocateAfterCounterInitialization()
    {
        var counters = new BufferUploadProfile.Counters();
        for (var index = 0; index < 100; index++)
            counters.Record(default, 0x1000, 4096, 0, 0, 0, index, recordSlowSample: true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 100; index < 1100; index++)
            counters.Record(default, 0x1000, 4096, 0, 0, 0, index, recordSlowSample: true);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void CountersSeparateSweepAndOtherUploads()
    {
        var counters = new BufferUploadProfile.Counters();
        var source = new BufferUploadProfile.Source(true, 11, 22, 0);
        counters.FindSource(source).Sweeps++;
        counters.Record(source, 0x1000, 4096, 0, 0, 0, 10);
        counters.Record(source, 0x1000, 4096, 2, 2048, 1024, 30);
        counters.Record(default, 0x1000, 4096, 1, 4096, 0, 50);
        var statistics = counters.Sources[source];
        Assert.Equal(1, statistics.Sweeps);
        Assert.Equal(2, statistics.Checks);
        Assert.Equal(1, statistics.Uploads);
        Assert.Equal(2, statistics.Copies);
        Assert.Equal(2048ul, statistics.Bytes);
        Assert.Equal(1024ul, statistics.HotBytes);
        Assert.Equal(40, statistics.Ticks);
        Assert.Equal(2, counters.Uploads.Count);
        Assert.Equal(4096ul, counters.Sources[default].Bytes);
    }

    [Fact]
    public void CapacityOverflowKeepsAllTotals()
    {
        var counters = new BufferUploadProfile.Counters();
        for (uint index = 0; index < BufferUploadProfile.Counters.Capacity + 3; index++)
            counters.Record(new(true, index, 0, 0), index * 4096, 4096, 1, 4096, 1024, 5);
        Assert.Equal(BufferUploadProfile.Counters.Capacity, counters.Sources.Count);
        Assert.Equal(BufferUploadProfile.Counters.Capacity, counters.Uploads.Count);
        Assert.Equal(3, counters.OtherSources.Checks);
        Assert.Equal(3 * 4096ul, counters.OtherSources.Bytes);
        Assert.Equal(3 * 1024ul, counters.OtherUploads.HotBytes);
        var total = new BufferUploadProfile.Statistics();
        foreach (var statistics in counters.Uploads.Values) total.Merge(statistics);
        total.Merge(counters.OtherUploads);
        Assert.Equal(BufferUploadProfile.Counters.Capacity + 3, total.Uploads);
        Assert.Equal((ulong)(BufferUploadProfile.Counters.Capacity + 3) * 4096, total.Bytes);
    }
}

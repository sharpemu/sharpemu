// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class MipStatisticsTests
{
    private const uint CounterEnabled = 1u << 25;

    private static TextureDescriptorWords Texture(uint slot, uint address = 0x1000, uint counter = CounterEnabled) =>
        new([address, 0, 0, 0, 0, counter, slot, 0]);

    private static (uint ClampedSamples, uint Slot, uint UnclampedSamples, uint FinestMip) Counter(ReadOnlySpan<byte> report, int slot)
    {
        var counter = report.Slice((int)MipStatistics.FirstCounterOffset + slot * 8, 8);
        var first = BinaryPrimitives.ReadUInt32LittleEndian(counter);
        var second = BinaryPrimitives.ReadUInt32LittleEndian(counter[4..]);
        return (first & 0xFFFFFF, first >> 24, second & 0xFFFFFF, (second >> 24) & 0xF);
    }

    [Fact]
    public void Accessors_DecodeTheCounterFields()
    {
        var texture = Texture(slot: 0xAB);

        Assert.True(texture.MipStatsEnabled);
        Assert.Equal(0xABu, texture.MipStatsSlot);
        Assert.False(Texture(slot: 0xAB, counter: 0).MipStatsEnabled);
    }

    [Fact]
    public void Report_AsksForTheFinestMipOfASlotWhoseTexturesWereSampled()
    {
        var statistics = new MipStatistics();
        statistics.NoteSampled(Texture(slot: 7));
        statistics.NoteSampled(Texture(slot: 7));
        var report = new byte[MipStatistics.ReportBytes];

        Assert.True(statistics.TryWriteReport(report));

        Assert.Equal((0x20000u, 7u, 0x20000u, 0u), Counter(report, 7));
    }

    [Fact]
    public void Report_MarksASlotWithoutSampledTexturesAsNotSampled()
    {
        var statistics = new MipStatistics();
        statistics.NoteSampled(Texture(slot: 7));
        statistics.NoteSampled(Texture(slot: 8, counter: 0));
        statistics.NoteSampled(Texture(slot: 9, address: 0));
        var report = new byte[MipStatistics.ReportBytes];

        statistics.TryWriteReport(report);

        Assert.Equal((0u, 8u, 0u, MipStatistics.NoMipSampled), Counter(report, 8));
        Assert.Equal((0u, 9u, 0u, MipStatistics.NoMipSampled), Counter(report, 9));
        Assert.Equal((0u, 255u, 0u, MipStatistics.NoMipSampled), Counter(report, 255));
    }

    [Fact]
    public void Report_SaturatesTheSampleCounts()
    {
        var statistics = new MipStatistics();
        for (var binding = 0; binding < 300; binding++)
        {
            statistics.NoteSampled(Texture(slot: 1));
        }

        var report = new byte[MipStatistics.ReportBytes];
        statistics.TryWriteReport(report);

        Assert.Equal((0xFFFFFFu, 1u, 0xFFFFFFu, 0u), Counter(report, 1));
    }

    [Fact]
    public void Report_StartsANewCountingPeriod()
    {
        var statistics = new MipStatistics();
        statistics.NoteSampled(Texture(slot: 3));
        statistics.TryWriteReport(new byte[MipStatistics.ReportBytes]);
        var report = new byte[MipStatistics.ReportBytes];

        statistics.TryWriteReport(report);

        Assert.Equal((0u, 3u, 0u, MipStatistics.NoMipSampled), Counter(report, 3));
    }

    [Fact]
    public void Report_LeavesTheHeaderAndLaterCounterBlocksToTheCaller()
    {
        var statistics = new MipStatistics();
        statistics.NoteSampled(Texture(slot: 0));
        var report = new byte[MipStatistics.ReportBytes + 2048];

        statistics.TryWriteReport(report);

        Assert.All(report[..(int)MipStatistics.FirstCounterOffset], static value => Assert.Equal(0, value));
        Assert.All(report[MipStatistics.ReportBytes..], static value => Assert.Equal(0, value));
    }

    [Fact]
    public void Report_IsRefusedWhenTheBufferCannotHoldEveryCounter()
    {
        var statistics = new MipStatistics();
        statistics.NoteSampled(Texture(slot: 3));
        var report = new byte[MipStatistics.ReportBytes - 1];

        Assert.False(statistics.TryWriteReport(report));

        Assert.All(report, static value => Assert.Equal(0, value));
    }
}

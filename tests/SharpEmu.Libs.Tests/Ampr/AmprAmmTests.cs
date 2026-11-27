// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

// The AMM commands run against a real guest address space, the way the title's submit does. The cases follow the
// AMM behaviour AnyPS5 tests: auto and direct maps, remaps and multimaps, protection changes, and PRT ranges.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AmprAmmTests
{
    private const ulong Page = 0x4000;
    private const ulong AreaStart = 0x80_0000_0000UL;
    private const int CpuReadWrite = 0x03;
    private const int CpuGpuReadWrite = 0x33;
    private const int CpuRead = 0x01;
    private const int GpuRead = 0x10;
    private const int Einval = unchecked((int)0x80020016);
    private const int Eagain = unchecked((int)0x80020023);
    private const int Eperm = unchecked((int)0x80020001);
    private const int Efault = unchecked((int)0x8002000E);
    private const int Esrch = unchecked((int)0x80020003);
    private const ulong AmmRecordSize = 0x20;

    [Fact]
    public void AllocatePaForPrtBacksTheRangeSoItCanBeWrittenAndRead()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x40_0000, auto: true);
        var prt = AreaStart + 0x400_0000;
        amm.Record(c => c.MapAsPrt(prt, 0x20 * Page));
        amm.Submit();

        amm.Record(c => c.AllocatePaForPrt(prt, 0x10 * Page, 0, CpuReadWrite));
        amm.Submit();

        amm.Write(prt, 0x1122);
        amm.Write(prt + 0x10 * Page - 8, 0x3344);
        Assert.Equal(0x1122UL, amm.Read(prt));
        Assert.Equal(0x3344UL, amm.Read(prt + 0x10 * Page - 8));
    }

    // The failing title call: a 400 MiB range at 0x8400000000 followed by a copy into it.
    [Fact]
    public void AllocatePaForPrtBacksARangeWithoutAPrecedingMapAsPrt()
    {
        using var amm = new AmmHarness(backingBytes: 0x1000_0000);
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x800_0000, auto: true);
        var range = 0x84_0000_0000UL;
        var size = 0x400_0000UL;

        amm.Record(c => c.AllocatePaForPrt(range, size, 0, CpuReadWrite));
        amm.Submit();

        amm.Write(range, 0xAB);
        amm.Write(range + size - 8, 0xCD);
        Assert.Equal(0xABUL, amm.Read(range));
        Assert.Equal(0xCDUL, amm.Read(range + size - 8));
    }

    [Fact]
    public void AllocatePaForPrtFailsAsAWholeWhenThePoolIsTooSmall()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var prt = AreaStart + 0x500_0000;
        amm.Record(c => c.MapAsPrt(prt, 0x400 * Page));
        amm.Submit();

        amm.Record(c => c.AllocatePaForPrt(prt, 0x200 * Page, 0, CpuReadWrite));
        Assert.Equal(Eagain, amm.Submit(expect: Eagain));

        Assert.False(amm.TryRead(prt, out _));
        amm.Record(c => c.AllocatePaForPrt(prt, 0x80 * Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(prt, 7);
        Assert.Equal(7UL, amm.Read(prt));
    }

    [Fact]
    public void UnmapToPrtReturnsBackedPagesToThePoolAndKeepsTheRangePrt()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x40_0000, auto: true);
        var prt = AreaStart + 0x600_0000;
        amm.Record(c => c.MapAsPrt(prt, 0x100 * Page));
        amm.Record(c => c.AllocatePaForPrt(prt, 0x100 * Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(prt, 1);

        amm.Record(c => c.UnmapToPrt(prt, 0x100 * Page));
        amm.Submit();
        Assert.False(amm.TryRead(prt, out _));

        // The pool has its pages back, and the range is still PRT so it can be backed again.
        amm.Record(c => c.AllocatePaForPrt(prt + 0x100 * Page, 0x100 * Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(prt + 0x100 * Page, 2);
        Assert.Equal(2UL, amm.Read(prt + 0x100 * Page));
        amm.Record(c => c.AllocatePaForPrt(prt, 0x100 * Page, 0, CpuReadWrite));
        Assert.Equal(Eagain, amm.Submit(expect: Eagain));
    }

    [Fact]
    public void UnmapToPrtOnARangeThatIsNotPrtFails()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var range = AreaStart + 0x700_0000;
        amm.Record(c => c.Map(range, 4 * Page, 0, CpuReadWrite));
        amm.Submit();

        amm.Record(c => c.UnmapToPrt(range, 4 * Page));

        Assert.Equal(Einval, amm.Submit(expect: Einval));
    }

    [Fact]
    public void AnAllocateOverBackedPagesChangesTheirProtectionAndBacksTheRest()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var prt = AreaStart + 0x800_0000;
        amm.Record(c => c.MapAsPrt(prt, 8 * Page));
        amm.Record(c => c.AllocatePaForPrt(prt + 2 * Page, 2 * Page, 0, CpuRead));
        amm.Submit();
        amm.Record(c => c.AllocatePaForPrt(prt, 6 * Page, 0, CpuReadWrite));
        amm.Submit();

        for (ulong i = 0; i < 6; i++)
        {
            amm.Write(prt + i * Page, 0x10 + i);
        }

        for (ulong i = 0; i < 6; i++)
        {
            Assert.Equal(0x10 + i, amm.Read(prt + i * Page));
        }

        Assert.False(amm.TryRead(prt + 6 * Page, out _));
    }

    [Fact]
    public void RemapIntoPrtMovesTheSourcePagesIntoAPrtRange()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var prt = AreaStart + 0x900_0000;
        var source = AreaStart + 0x910_0000;
        amm.Record(c => c.MapAsPrt(prt, 4 * Page));
        amm.Record(c => c.Map(source, Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(source, 0x88);

        amm.Record(c => c.RemapIntoPrt(prt + 3 * Page, source, Page, CpuReadWrite));
        amm.Submit();

        Assert.Equal(0x88UL, amm.Read(prt + 3 * Page));
        Assert.False(amm.TryRead(source, out _));
        // The destination is still PRT, so UnmapToPrt accepts it.
        amm.Record(c => c.UnmapToPrt(prt + 3 * Page, Page));
        amm.Submit();
    }

    [Fact]
    public void RemapIntoPrtNeedsAPrtDestination()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var dest = AreaStart + 0xA00_0000;
        var source = AreaStart + 0xA10_0000;
        amm.Record(c => c.Map(source, Page, 0, CpuReadWrite));
        amm.Submit();

        amm.Record(c => c.RemapIntoPrt(dest, source, Page, CpuReadWrite));

        Assert.Equal(Einval, amm.Submit(expect: Einval));
        amm.Write(source, 5);
    }

    [Fact]
    public void RemapMovesPagesAndKeepsTheirContent()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var source = AreaStart + 0xB00_0000;
        var dest = AreaStart + 0xB10_0000;
        amm.Record(c => c.Map(source, 2 * Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(source, 0xA1);
        amm.Write(source + Page, 0xA2);

        amm.Record(c => c.Remap(dest, source, 2 * Page, CpuReadWrite));
        amm.Submit();

        Assert.Equal(0xA1UL, amm.Read(dest));
        Assert.Equal(0xA2UL, amm.Read(dest + Page));
        Assert.False(amm.TryRead(source, out _));
        // Moving keeps the pool as it was: nothing was freed or taken.
        amm.Record(c => c.Map(AreaStart + 0xB40_0000, 0x7E * Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(AreaStart + 0xB40_0000 + 0x7D * Page, 3);
    }

    [Fact]
    public void RemapOntoItselfChangesTheProtectionOnly()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var range = AreaStart + 0xB80_0000;
        amm.Record(c => c.Map(range, Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(range, 0x42);

        amm.Record(c => c.Remap(range, range, Page, CpuRead));
        amm.Submit();

        Assert.Equal(0x42UL, amm.Read(range));
        Assert.Equal(CpuRead, amm.Protection(range));
    }

    [Fact]
    public void MultiMapMapsTheSamePagesAtASecondAddress()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var source = AreaStart + 0xC00_0000;
        var alias = AreaStart + 0xC10_0000;
        amm.Record(c => c.Map(source, Page, 0, CpuReadWrite));
        amm.Record(c => c.MultiMap(alias, source, Page, CpuReadWrite));
        amm.Submit();

        amm.Write(source, 0xA1);

        Assert.Equal(0xA1UL, amm.Read(alias));
        amm.Write(alias, 0xB1);
        Assert.Equal(0xB1UL, amm.Read(source));
    }

    // The pages go back to the pool only when the last view of them is unmapped.
    [Fact]
    public void AMultiMappedPageStaysOutOfThePoolUntilEveryViewIsUnmapped()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var source = AreaStart + 0xD00_0000;
        var alias = AreaStart + 0xD10_0000;
        var fill = AreaStart + 0xD20_0000;
        // All but one page of the pool is taken by the map below.
        amm.Record(c => c.Map(fill, 0x7F * Page, 0, CpuReadWrite));
        amm.Record(c => c.Map(source, Page, 0, CpuReadWrite));
        amm.Record(c => c.MultiMap(alias, source, Page, CpuReadWrite));
        amm.Submit();
        amm.Write(source, 0xC1);

        amm.Record(c => c.Unmap(source, Page));
        amm.Submit();
        amm.Record(c => c.Map(AreaStart + 0xD80_0000, Page, 0, CpuReadWrite));
        Assert.Equal(Eagain, amm.Submit(expect: Eagain));
        Assert.Equal(0xC1UL, amm.Read(alias));

        amm.Record(c => c.Unmap(alias, Page));
        amm.Record(c => c.Map(AreaStart + 0xD80_0000, Page, 0, CpuReadWrite));
        amm.Submit();
    }

    [Fact]
    public void MultiMapOverItsOwnSourceIsRejected()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var source = AreaStart + 0xE00_0000;
        amm.Record(c => c.Map(source, 2 * Page, 0, CpuReadWrite));
        amm.Submit();

        amm.Record(c => c.MultiMap(source + Page, source, 2 * Page, CpuReadWrite));

        Assert.Equal(Einval, amm.Submit(expect: Einval));
    }

    [Fact]
    public void ModifyProtectReplacesOnlyTheMaskedBits()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var range = AreaStart + 0xF00_0000;
        amm.Record(c => c.Map(range, 4 * Page, 0, CpuGpuReadWrite));
        amm.Submit();
        amm.Write(range, 9);

        // Clearing the CPU write bit leaves the GPU bits and the CPU read access.
        amm.Record(c => c.ModifyProtect(range + Page, 2 * Page, 0x00, 0x02));
        amm.Submit();

        Assert.Equal(CpuGpuReadWrite, amm.Protection(range));
        Assert.Equal(0x31, amm.Protection(range + Page));
        Assert.Equal(0x31, amm.Protection(range + 2 * Page));
        Assert.True(amm.TryRead(range + Page, out _));
        Assert.Equal(CpuGpuReadWrite, amm.Protection(range + 3 * Page));
    }

    [Fact]
    public void ModifyMtypeProtectChangesTheProtectionOfAMappedRange()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var range = AreaStart + 0xF80_0000;
        amm.Record(c => c.Map(range, 2 * Page, 0, CpuReadWrite));
        amm.Submit();

        amm.Record(c => c.ModifyMtypeProtect(range, 2 * Page, 3, CpuRead, 0x03));
        amm.Submit();

        Assert.True(amm.TryRead(range, out _));
        Assert.Equal(CpuRead, amm.Protection(range));
        Assert.Equal(CpuRead, amm.Protection(range + Page));
    }

    [Fact]
    public void ModifyProtectOnAnUnmappedRangeFails()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var range = AreaStart + 0xFC0_0000;
        amm.Record(c => c.Map(range, Page, 0, CpuReadWrite));
        amm.Submit();

        amm.Record(c => c.ModifyProtect(range, 2 * Page, CpuRead, 0x03));

        Assert.Equal(Einval, amm.Submit(expect: Einval));
    }

    [Fact]
    public void MapDirectMapsTheNamedDirectMemoryAndUnmapReleasesIt()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        var direct = amm.Give(0x20_0000, auto: false);
        var range = AreaStart + 0x1000_0000;
        amm.Record(c => c.MapDirect(range, direct, 2 * Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(range, 0x77);
        Assert.Equal(0x77UL, amm.Read(range));

        amm.Record(c => c.Unmap(range, 2 * Page));
        amm.Submit();

        Assert.False(amm.TryRead(range, out _));
    }

    [Fact]
    public void AMapOverMappedPagesReplacesThemAndReturnsTheirPages()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var range = AreaStart + 0x1100_0000;
        amm.Record(c => c.Map(range, 0x80 * Page, 0, CpuReadWrite));
        amm.Submit();

        // The second map needs the same pages, so the first has to return them.
        amm.Record(c => c.Map(range, 0x80 * Page, 0, CpuReadWrite));
        amm.Submit();

        amm.Write(range + 0x7F * Page, 6);
    }

    [Fact]
    public void AnUnmapInTheMiddleOfAMapKeepsTheRest()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var range = AreaStart + 0x1200_0000;
        amm.Record(c => c.Map(range, 6 * Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(range, 1);
        amm.Write(range + 5 * Page, 5);

        amm.Record(c => c.Unmap(range + 2 * Page, 2 * Page));
        amm.Submit();

        Assert.Equal(1UL, amm.Read(range));
        Assert.Equal(5UL, amm.Read(range + 5 * Page));
        Assert.False(amm.TryRead(range + 2 * Page, out _));
        Assert.False(amm.TryRead(range + 3 * Page, out _));
    }

    [Fact]
    public void EveryRecordedCommandTakesOneRecordAndTheMeasureCallsAgree()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var a = AreaStart + 0x1300_0000;
        var b = AreaStart + 0x1310_0000;
        var recorders = new Action<AmmHarness.Commands>[]
        {
            c => c.Map(a, Page, 0, CpuReadWrite),
            c => c.MapWithGpuMaskId(a, Page, 0, CpuReadWrite, 1),
            c => c.MapDirect(a, 0, Page, 0, CpuReadWrite),
            c => c.MapDirectWithGpuMaskId(a, 0, Page, 0, CpuReadWrite),
            c => c.Unmap(a, Page),
            c => c.Remap(b, a, Page, CpuReadWrite),
            c => c.RemapWithGpuMaskId(b, a, Page, CpuReadWrite, 1),
            c => c.MultiMap(b, a, Page, CpuReadWrite),
            c => c.MultiMapWithGpuMaskId(b, a, Page, CpuReadWrite, 1),
            c => c.ModifyProtect(a, Page, CpuRead, 3),
            c => c.ModifyProtectWithGpuMaskId(a, Page, CpuRead, 3, 1),
            c => c.ModifyMtypeProtect(a, Page, 1, CpuRead, 3),
            c => c.ModifyMtypeProtectWithGpuMaskId(a, Page, 1, CpuRead, 3, 1),
            c => c.MapAsPrt(a, Page),
            c => c.AllocatePaForPrt(a, Page, 0, CpuReadWrite),
            c => c.RemapIntoPrt(b, a, Page, CpuReadWrite),
            c => c.UnmapToPrt(a, Page),
        };
        foreach (var record in recorders)
        {
            var before = amm.Offset;
            var commands = amm.Count;
            amm.Record(record);
            Assert.Equal(AmmRecordSize, amm.Offset - before);
            Assert.Equal(commands + 1, amm.Count);
        }

        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeMap, a, Page, 0, CpuReadWrite));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeMapWithGpuMaskId, a, Page, 0, CpuReadWrite, 1));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeMapDirect, a, 0, Page, 0, CpuReadWrite));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeMapDirectWithGpuMaskId, a, 0, Page, 0, CpuReadWrite, 1));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeUnmap, a, Page));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeRemap, b, a, Page, CpuReadWrite));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeRemapWithGpuMaskId, b, a, Page, CpuReadWrite, 1));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeMultiMap, b, a, Page, CpuReadWrite));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeMultiMapWithGpuMaskId, b, a, Page, CpuReadWrite, 1));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeModifyProtect, a, Page, CpuRead, 3));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeModifyProtectWithGpuMaskId, a, Page, CpuRead, 3, 1));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeModifyMtypeProtect, a, Page, 1, CpuRead, 3));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeModifyMtypeProtectWithGpuMaskId, a, Page, 1, CpuRead, 3, 1));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeMapAsPrt, a, Page));
        Assert.Equal((int)AmmRecordSize, amm.Measure(AmprExports.AmmMeasureCommandSizeAllocatePaForPrt, a, Page, 0, CpuReadWrite));
    }

    [Fact]
    public void MeasureCallsRejectWhatTheCommandsWouldReject()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        var a = AreaStart + 0x1400_0000;
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeMap, a, Page, 0, 0x04));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeMap, a, Page, 0, 0x400));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeMapWithGpuMaskId, a + 8, Page, 0, CpuReadWrite, 0));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeMapDirect, a, 8, Page, 0, CpuReadWrite));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeUnmap, a, 0));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeUnmap, 0, Page));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeMapAsPrt, a, Page + 8));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeAllocatePaForPrt, a, Page, 0, 0x08));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeRemap, a, a + 8, Page, CpuReadWrite));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeMultiMap, a, a + Page, Page, 0x04));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeModifyProtect, a, Page, CpuRead, 0x04));
        Assert.Equal(Einval, amm.Measure(AmprExports.AmmMeasureCommandSizeModifyMtypeProtect, a, Page, 1, 0x04, 3));
    }

    [Fact]
    public void InvalidArgumentsAreRejectedWithoutRecording()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        var a = AreaStart + 0x1500_0000;
        var offset = amm.Offset;
        Assert.Equal(Einval, amm.Record(c => c.MapAsPrt(a + 8, Page), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.MapAsPrt(a, 0), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.MapAsPrt(0x1000_0000, Page), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.AllocatePaForPrt(a, Page, 0, 0x04), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.AllocatePaForPrt(a, Page, 0, 0x404), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.AllocatePaForPrt(a, 0, 0, CpuReadWrite), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.RemapIntoPrt(a, a + Page + 8, Page, CpuReadWrite), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.RemapIntoPrt(a, a + Page, Page, 0x04), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.UnmapToPrt(a, Page + 8), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.Remap(a, a + Page, 0, CpuReadWrite), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.ModifyProtect(a, Page, 0x04, 3), allowFailure: true));
        Assert.Equal(Einval, amm.Record(c => c.ModifyProtect(a, Page, CpuRead, 0x08), allowFailure: true));
        Assert.Equal(offset, amm.Offset);
        Assert.Equal(0UL, amm.Count);
    }

    [Fact]
    public void AnUnboundBufferIsPermissionDenied()
    {
        using var amm = new AmmHarness(bindBuffer: false);
        if (!amm.AreaAvailable)
        {
            return;
        }

        var a = AreaStart + 0x1600_0000;
        Assert.Equal(Eperm, amm.Record(c => c.MapAsPrt(a, Page), allowFailure: true));
        Assert.Equal(Eperm, amm.Record(c => c.AllocatePaForPrt(a, Page, 0, CpuReadWrite), allowFailure: true));
        Assert.Equal(Eperm, amm.Record(c => c.UnmapToPrt(a, Page), allowFailure: true));
        Assert.Equal(Eperm, amm.Record(c => c.RemapIntoPrt(a, a + Page, Page, CpuReadWrite), allowFailure: true));
    }

    [Fact]
    public void AmmUsageStatsReportTheEmptyPagePool()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        Assert.Equal(Einval, amm.UsageStats(0));
        Assert.Equal(Efault, amm.UsageStats(0x10));
        var stats = amm.Scratch;
        amm.Context.TryWriteUInt64(stats, 0x19);
        Assert.Equal(Einval, amm.UsageStats(stats));

        amm.Context.TryWriteUInt64(stats, 0x18);
        amm.Context.TryWriteUInt64(stats + 8, 0xDEADBEEF);
        amm.Context.TryWriteUInt64(stats + 16, 0xDEADBEEF);
        Assert.Equal(0, amm.UsageStats(stats));
        Assert.Equal(0x18UL, amm.Read(stats));
        Assert.Equal(512UL, amm.Read(stats + 8) & 0xFFFF);
        Assert.Equal(0UL, amm.Read(stats + 8) >> 16);
        Assert.Equal(7UL, amm.Read(stats + 16) & 0xFFFFFFFF);

        // A smaller structure is written only as far as it reaches.
        amm.Context.TryWriteUInt64(stats, 0x8);
        amm.Context.TryWriteUInt64(stats + 8, 0xDEADBEEF);
        Assert.Equal(0, amm.UsageStats(stats));
        Assert.Equal(0x8UL, amm.Read(stats));
        Assert.Equal(0xDEADBEEFUL, amm.Read(stats + 8));
    }

    [Fact]
    public void SubmitVariantsReportAnIdThatWaitAccepts()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var range = AreaStart + 0x1700_0000;
        amm.Record(c => c.Map(range, Page, 0, CpuReadWrite));

        var id = amm.SubmitWithId(variant: 3);
        Assert.NotEqual(0u, id);
        Assert.Equal(0, amm.Wait(id));
        Assert.Equal(Esrch, amm.Wait(id + 1000));
        Assert.Equal(Esrch, amm.Wait(0));

        amm.Write(range, 4);
        var second = amm.SubmitWithId(variant: 2);
        Assert.True(second > id);
        Assert.Equal(0, amm.Wait(second));
        Assert.Equal(0, amm.PreviousSubmitResult);

        Assert.Equal(Efault, amm.SubmitWithBadId(variant: 3));
        Assert.Equal(Efault, amm.SubmitWithBadId(variant: 2));
    }

    [Fact]
    public void AFailedCommandIsReportedBySubmitVariant2()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        amm.Record(c => c.UnmapToPrt(AreaStart + 0x1800_0000, Page));

        amm.SubmitWithId(variant: 2);

        Assert.Equal(Einval, amm.PreviousSubmitResult);
    }

    [Fact]
    public void SetPageTablePoolThresholdSucceeds()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Context[CpuRegister.Rdi] = 50;
        Assert.Equal(0, AmprExports.AmmSetPageTablePoolOccupancyNotificationThreshold(amm.Context));
    }

    // The aliasing refcount must not lose pages when a title maps, aliases and unmaps in different orders.
    [Fact]
    public void RemapKeepsPoolReferencesWhenSourceAndAliasMove()
    {
        using var amm = new AmmHarness();
        if (!amm.AreaAvailable)
        {
            return;
        }

        amm.Give(0x20_0000, auto: true);
        var source = AreaStart + 0x1900_0000;
        var alias = AreaStart + 0x1910_0000;
        var fill = AreaStart + 0x1920_0000;
        amm.Record(c => c.Map(fill, 0x7F * Page, 0, CpuReadWrite));
        amm.Record(c => c.Map(source, Page, 0, CpuReadWrite));
        amm.Record(c => c.MultiMap(alias, source, Page, CpuReadWrite));
        amm.Submit();
        amm.Write(source, 0xA1);

        // Moving the source onto the alias leaves one view of the page.
        amm.Record(c => c.Remap(alias, source, Page, CpuReadWrite));
        amm.Submit();
        Assert.Equal(0xA1UL, amm.Read(alias));
        Assert.False(amm.TryRead(source, out _));
        amm.Record(c => c.Map(source, Page, 0, CpuReadWrite));
        Assert.Equal(Eagain, amm.Submit(expect: Eagain));

        amm.Record(c => c.Unmap(alias, Page));
        amm.Record(c => c.Map(source, Page, 0, CpuReadWrite));
        amm.Submit();
        amm.Write(source, 0xC1);
        Assert.Equal(0xC1UL, amm.Read(source));
    }

    // The AMM range is 256 GiB at 0x80_0000_0000 and the multimap range 32 GiB after it, as on AnyPS5. The
    // query takes four pointers: AMM start and end, multimap start and end.
    [Fact]
    public void GetVirtualAddressRangesWritesFourSeparatePointers()
    {
        using var amm = new AmmHarness();
        var ctx = amm.Context;
        var start = amm.Scratch + 0x100;
        var end = amm.Scratch + 0x200;
        var multimapStart = amm.Scratch + 0x300;
        var multimapEnd = amm.Scratch + 0x400;
        foreach (var slot in new[] { start, end, multimapStart, multimapEnd })
        {
            amm.Write(slot, 0xDEAD_BEEF_DEAD_BEEFUL);
            amm.Write(slot + 8, 0xDEAD_BEEF_DEAD_BEEFUL);
        }

        ctx[CpuRegister.Rdi] = start;
        ctx[CpuRegister.Rsi] = end;
        ctx[CpuRegister.Rdx] = multimapStart;
        ctx[CpuRegister.Rcx] = multimapEnd;
        Assert.Equal(0, AmprExports.AmmGetVirtualAddressRanges(ctx));

        Assert.Equal(0x80_0000_0000UL, amm.Read(start));
        Assert.Equal(0xC0_0000_0000UL, amm.Read(end));
        Assert.Equal(0xC0_0000_0000UL, amm.Read(multimapStart));
        Assert.Equal(0xC8_0000_0000UL, amm.Read(multimapEnd));
        // Nothing beyond each output qword is written.
        foreach (var slot in new[] { start, end, multimapStart, multimapEnd })
        {
            Assert.Equal(0xDEAD_BEEF_DEAD_BEEFUL, amm.Read(slot + 8));
        }
    }

    [Fact]
    public void GetVirtualAddressRangesRejectsANullOutputPointer()
    {
        using var amm = new AmmHarness();
        var ctx = amm.Context;
        var slots = new[] { amm.Scratch + 0x100, amm.Scratch + 0x200, amm.Scratch + 0x300, amm.Scratch + 0x400 };
        for (var nullIndex = 0; nullIndex < 4; nullIndex++)
        {
            var pointers = (ulong[])slots.Clone();
            pointers[nullIndex] = 0;
            ctx[CpuRegister.Rdi] = pointers[0];
            ctx[CpuRegister.Rsi] = pointers[1];
            ctx[CpuRegister.Rdx] = pointers[2];
            ctx[CpuRegister.Rcx] = pointers[3];
            Assert.Equal(Einval, AmprExports.AmmGetVirtualAddressRanges(ctx));
        }
    }

    // The failing title reserves 0xF0_0000_0000 and 0xFB_1C3A_0000 with MAP_FIXED | NO_OVERWRITE after AMM set up
    // its range; both used to fail because the AMM reservation reached 0xFC_0000_0000. Whether the host has those
    // addresses free depends on what else runs in the process (a GPU driver may hold them), so this checks that the
    // AMM reservation ends below them rather than reserving them for real.
    [Fact]
    public void TheAmmReservationEndsBelowTheTopOfTheUserRange()
    {
        using var amm = new AmmHarness();
        Assert.True(amm.AreaAvailable, "the AMM area could not be reserved");
        Assert.True(AmprExports.ContainsAmmRange(0xC0_0000_0000UL - Page, Page));
        Assert.False(AmprExports.ContainsAmmRange(0xC0_0000_0000UL, Page));
        Assert.False(AmprExports.ContainsAmmRange(0xF0_0000_0000UL, 0xD0_0000UL));
        Assert.False(AmprExports.ContainsAmmRange(0xFB_1C3A_0000UL, 0xE3C6_0000UL));
    }

    private sealed class AmmHarness : IDisposable
    {
        private const ulong CommandBufferObject = 0x100;
        private readonly BackedKernelMemory _kernel;
        private readonly ulong _scratch;
        private readonly ulong _backingBytes;
        private readonly StringWriter _err = new();
        private readonly TextWriter _oldErr;

        public AmmHarness(ulong backingBytes = 0x800_0000, bool bindBuffer = true)
        {
            AmprExports.ResetAmmStateForTests();
            _backingBytes = backingBytes;
            _oldErr = Console.Error;
            Console.SetError(_err);
            _kernel = new BackedKernelMemory(backingBytes);
            Context = _kernel.Context;
            _scratch = _kernel.Memory.AllocateAt(0, 0x10000, false);
            Assert.True(_scratch != 0);
            CommandBuffer = _scratch + CommandBufferObject;
            var records = _scratch + 0x1000;
            Context[CpuRegister.Rdi] = CommandBuffer;
            Assert.Equal(0, AmprExports.AmmCommandBufferConstructor(Context));
            if (bindBuffer)
            {
                Context[CpuRegister.Rdi] = CommandBuffer;
                Context[CpuRegister.Rsi] = records;
                Context[CpuRegister.Rdx] = 0x8000;
                Assert.Equal(0, AmprExports.CommandBufferSetBuffer(Context));
            }
        }
        private bool TryReserveArea()
        {
            Context[CpuRegister.Rdi] = Scratch + 0x400;
            Context[CpuRegister.Rsi] = Scratch + 0x408;
            Context[CpuRegister.Rdx] = Scratch + 0x410;
            Context[CpuRegister.Rcx] = Scratch + 0x418;
            Assert.Equal(0, AmprExports.AmmGetVirtualAddressRanges(Context));
            return AmprExports.ContainsAmmRange(AreaStart, Page);
        }


        // The AMM area is a fixed 256 GiB range with a 32 GiB multimap range after it. A process whose other tests or runtime already hold memory
        // inside it cannot reserve it, and the cases then have nothing to run against.
        public bool AreaAvailable => _areaAvailable ??= TryReserveArea();

        private bool? _areaAvailable;

        public CpuContext Context { get; }

        public ulong CommandBuffer { get; }

        public ulong Scratch => _scratch + 0x9000;

        public int PreviousSubmitResult { get; private set; }

        public ulong Offset
        {
            get
            {
                Assert.True(Context.TryReadUInt32(CommandBuffer + 4, out var offset));
                return offset;
            }
        }

        public ulong Count
        {
            get
            {
                Assert.True(Context.TryReadUInt32(CommandBuffer + 8, out var count));
                return count;
            }
        }

        // Gives direct memory to the mapper and returns its offset.
        public ulong Give(ulong size, bool auto)
        {
            Context[CpuRegister.Rdi] = 0;
            Context[CpuRegister.Rsi] = _backingBytes;
            Context[CpuRegister.Rdx] = size;
            Context[CpuRegister.Rcx] = 0x20_0000;
            Context[CpuRegister.R8] = auto ? 1UL : 0UL;
            Context[CpuRegister.R9] = Scratch;
            var giveResult = AmprExports.AmmGiveDirectMemory(Context);
            Assert.True(giveResult == 0, "give=" + giveResult.ToString("X") + " err=" + _err);
            Assert.True(Context.TryReadUInt64(Scratch, out var offset));
            return offset;
        }

        public int Record(Action<Commands> record, bool allowFailure = false)
        {
            var commands = new Commands(Context, CommandBuffer);
            record(commands);
            if (!allowFailure)
            {
                Assert.Equal(0, commands.Result);
            }

            return commands.Result;
        }

        // Submits the recorded commands and returns the first failure the buffer reported, or the submit result.
        public int Submit(int expect = 0)
        {
            Context[CpuRegister.Rdi] = CommandBuffer;
            Context[CpuRegister.Rsi] = Offset;
            Context[CpuRegister.Rdx] = 0;
            Context[CpuRegister.Rcx] = Scratch + 0x100;
            Context[CpuRegister.R8] = Scratch + 0x108;
            Assert.Equal(0, AmprExports.AmmSubmitCommandBuffer2(Context));
            Assert.True(Context.TryReadUInt32(Scratch + 0x100, out var result));
            Assert.Equal(expect, unchecked((int)result));
            ClearRecorded();
            return unchecked((int)result);
        }

        public uint SubmitWithId(int variant)
        {
            Context[CpuRegister.Rdi] = CommandBuffer;
            Context[CpuRegister.Rsi] = Offset;
            Context[CpuRegister.Rdx] = 0;
            Context.TryWriteUInt64(Scratch + 0x100, ulong.MaxValue);
            Context[CpuRegister.Rcx] = variant == 3 ? Scratch + 0x108 : Scratch + 0x100;
            Context[CpuRegister.R8] = Scratch + 0x108;
            Assert.Equal(0, variant == 3 ? AmprExports.AmmSubmitCommandBuffer3(Context) : AmprExports.AmmSubmitCommandBuffer2(Context));
            Assert.True(Context.TryReadUInt32(Scratch + 0x108, out var id));
            Assert.True(Context.TryReadUInt32(Scratch + 0x100, out var result));
            PreviousSubmitResult = variant == 3 ? 0 : unchecked((int)result);
            ClearRecorded();
            return id;
        }

        public int SubmitWithBadId(int variant)
        {
            Context[CpuRegister.Rdi] = CommandBuffer;
            Context[CpuRegister.Rsi] = 0;
            Context[CpuRegister.Rdx] = 0;
            Context[CpuRegister.Rcx] = variant == 3 ? 0x10UL : Scratch + 0x100;
            Context[CpuRegister.R8] = 0x10;
            return variant == 3 ? AmprExports.AmmSubmitCommandBuffer3(Context) : AmprExports.AmmSubmitCommandBuffer2(Context);
        }

        public int Wait(uint id)
        {
            Context[CpuRegister.Rdi] = id;
            return AmprExports.AmmWaitCommandBufferCompletion(Context);
        }

        public int UsageStats(ulong stats)
        {
            Context[CpuRegister.Rdi] = stats;
            return AmprExports.AmmGetUsageStatsData(Context);
        }

        public int Measure(Func<CpuContext, int> measure, params ulong[] arguments)
        {
            var registers = new[]
            {
                CpuRegister.Rdi, CpuRegister.Rsi, CpuRegister.Rdx, CpuRegister.Rcx, CpuRegister.R8, CpuRegister.R9,
            };
            for (var i = 0; i < arguments.Length; i++)
            {
                Context[registers[i]] = arguments[i];
            }

            return measure(Context);
        }

        // The protection the kernel records for the page at the address.
        public int Protection(ulong address)
        {
            Context[CpuRegister.Rdi] = address;
            Context[CpuRegister.Rsi] = 0;
            Context[CpuRegister.Rdx] = Scratch + 0x200;
            Context[CpuRegister.Rcx] = 0x48;
            Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(Context));
            Assert.True(Context.TryReadUInt32(Scratch + 0x218, out var protection));
            return unchecked((int)protection);
        }

        public void Write(ulong address, ulong value) => Assert.True(Context.TryWriteUInt64(address, value));

        public ulong Read(ulong address)
        {
            Assert.True(Context.TryReadUInt64(address, out var value));
            return value;
        }

        public bool TryRead(ulong address, out ulong value) => Context.TryReadUInt64(address, out value);

        public bool TryWrite(ulong address, ulong value) => Context.TryWriteUInt64(address, value);

        // A submitted buffer is reused from its start, as a title resets it.
        private void ClearRecorded()
        {
            Context[CpuRegister.Rdi] = CommandBuffer;
            Assert.Equal(0, AmprExports.CommandBufferClearBuffer(Context));
            Context[CpuRegister.Rdi] = CommandBuffer;
            Context[CpuRegister.Rsi] = _scratch + 0x1000;
            Context[CpuRegister.Rdx] = 0x8000;
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(Context));
        }

        public void Dispose()
        {
            Console.SetError(_oldErr);
            _kernel.Dispose();
            AmprExports.ResetAmmStateForTests();
        }

        public sealed class Commands(CpuContext context, ulong commandBuffer)
        {
            public int Result { get; private set; }

            private static readonly CpuRegister[] Registers =
            [
                CpuRegister.Rdi, CpuRegister.Rsi, CpuRegister.Rdx, CpuRegister.Rcx, CpuRegister.R8, CpuRegister.R9,
            ];

            private int Call(Func<CpuContext, int> export, params ulong[] arguments)
            {
                context[CpuRegister.Rdi] = commandBuffer;
                for (var i = 0; i < arguments.Length; i++)
                {
                    context[Registers[i + 1]] = arguments[i];
                }

                return Result = export(context);
            }

            public void Map(ulong address, ulong size, int type, int protection) =>
                Call(AmprExports.AmmCommandBufferMap, address, size, unchecked((ulong)type), unchecked((ulong)protection));

            public void MapWithGpuMaskId(ulong address, ulong size, int type, int protection, int mask) =>
                Call(AmprExports.AmmCommandBufferMapWithGpuMaskId, address, size, unchecked((ulong)type), unchecked((ulong)protection), unchecked((ulong)mask));

            public void MapDirect(ulong address, ulong directOffset, ulong size, int type, int protection) =>
                Call(AmprExports.AmmCommandBufferMapDirect, address, directOffset, size, unchecked((ulong)type), unchecked((ulong)protection));

            public void MapDirectWithGpuMaskId(ulong address, ulong directOffset, ulong size, int type, int protection) =>
                Call(AmprExports.AmmCommandBufferMapDirectWithGpuMaskId, address, directOffset, size, unchecked((ulong)type), unchecked((ulong)protection));

            public void Unmap(ulong address, ulong size) =>
                Call(AmprExports.AmmCommandBufferUnmap, address, size);

            public void Remap(ulong address, ulong source, ulong size, int protection) =>
                Call(AmprExports.AmmCommandBufferRemap, address, source, size, unchecked((ulong)protection));

            public void RemapWithGpuMaskId(ulong address, ulong source, ulong size, int protection, int mask) =>
                Call(AmprExports.AmmCommandBufferRemapWithGpuMaskId, address, source, size, unchecked((ulong)protection), unchecked((ulong)mask));

            public void MultiMap(ulong address, ulong source, ulong size, int protection) =>
                Call(AmprExports.AmmCommandBufferMultiMap, address, source, size, unchecked((ulong)protection));

            public void MultiMapWithGpuMaskId(ulong address, ulong source, ulong size, int protection, int mask) =>
                Call(AmprExports.AmmCommandBufferMultiMapWithGpuMaskId, address, source, size, unchecked((ulong)protection), unchecked((ulong)mask));

            public void ModifyProtect(ulong address, ulong size, int protection, int mask) =>
                Call(AmprExports.AmmCommandBufferModifyProtect, address, size, unchecked((ulong)protection), unchecked((ulong)mask));

            public void ModifyProtectWithGpuMaskId(ulong address, ulong size, int protection, int mask, int gpuMask) =>
                Call(AmprExports.AmmCommandBufferModifyProtectWithGpuMaskId, address, size, unchecked((ulong)protection), unchecked((ulong)mask), unchecked((ulong)gpuMask));

            public void ModifyMtypeProtect(ulong address, ulong size, int type, int protection, int mask) =>
                Call(AmprExports.AmmCommandBufferModifyMtypeProtect, address, size, unchecked((ulong)type), unchecked((ulong)protection), unchecked((ulong)mask));

            public void ModifyMtypeProtectWithGpuMaskId(ulong address, ulong size, int type, int protection, int mask, int gpuMask) =>
                Call(AmprExports.AmmCommandBufferModifyMtypeProtectWithGpuMaskId, address, size, unchecked((ulong)type), unchecked((ulong)protection), unchecked((ulong)mask));

            public void MapAsPrt(ulong address, ulong size) =>
                Call(AmprExports.AmmCommandBufferMapAsPrt, address, size);

            public void AllocatePaForPrt(ulong address, ulong size, int type, int protection) =>
                Call(AmprExports.AmmCommandBufferAllocatePaForPrt, address, size, unchecked((ulong)type), unchecked((ulong)protection));

            public void RemapIntoPrt(ulong address, ulong source, ulong size, int protection) =>
                Call(AmprExports.AmmCommandBufferRemapIntoPrt, address, source, size, unchecked((ulong)protection), 0);

            public void UnmapToPrt(ulong address, ulong size) =>
                Call(AmprExports.AmmCommandBufferUnmapToPrt, address, size);
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Memory.GuestMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelBackedMemoryTests
{
    [Fact]
    public void BatchRemapCanExtendAnUnmappedReservationWithoutOverwritingMappings()
    {
        using var test = new BackedKernelMemory();
        const ulong address = 0x102A000000;
        test.Allocate(0, 0x410000);
        test.Map(0, 0x200000, address);
        test.Map(0x200000, 0x10000, address + 0x200000);
        Assert.Equal(0, test.Unmap(address, 0x210000));

        var entriesAddress = test.Output + 0x200;
        for (var index = 0; index < 3; index++)
        {
            var entryAddress = entriesAddress + (ulong)index * 32;
            var offset = (ulong)index * 0x200000;
            Assert.True(test.Context.TryWriteUInt64(entryAddress, address + offset));
            Assert.True(test.Context.TryWriteUInt64(entryAddress + 8, offset));
            Assert.True(test.Context.TryWriteUInt64(entryAddress + 16, index == 2 ? 0x10000UL : 0x200000UL));
            Assert.True(test.Context.TryWriteUInt64(entryAddress + 24, 0xF2));
        }
        test.Context[CpuRegister.Rdi] = entriesAddress;
        test.Context[CpuRegister.Rsi] = 3;
        test.Context[CpuRegister.Rdx] = test.Output + 0x300;
        test.Context[CpuRegister.Rcx] = 0x90;
        Assert.Equal(0, KernelMemoryCompatExports.KernelBatchMap2(test.Context));
        Assert.True(test.Context.TryReadUInt32(test.Output + 0x300, out var processed));
        Assert.Equal(3u, processed);
        Assert.True(test.Memory.IsBackedRange(address, 0x410000));
        Assert.True(test.Context.TryWriteUInt64(address, 0x123456789ABCDEF0));

        Assert.Equal(unchecked((int)0x8002000C), KernelMemoryCompatExports.KernelBatchMap2(test.Context));
        Assert.True(test.Context.TryReadUInt32(test.Output + 0x300, out processed));
        Assert.Equal(0u, processed);
        Assert.True(test.Context.TryReadUInt64(address, out var value));
        Assert.Equal(0x123456789ABCDEF0UL, value);
    }

    [Fact]
    public void MacOsFlexibleMappingExcludesReservedGraphicsMemory()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var test = new BackedKernelMemory();
        var previousPool = KernelMemoryCompatExports.SetFlexibleBackingForTests(
            new FlexibleBackingPool(0x4000000, 0x4000000));
        try
        {
            // Fail on the first search in the reserved graphics memory region.
            test.Host.BeforeReserveHole = (address, size) =>
                Assert.True(address >= 0x70_0000_0000 || address + size <= 0x10_0000_0000,
                    "The allocation search entered macOS's reserved GPU window.");
            Assert.True(test.Context.TryWriteUInt64(test.Output, 0));
            test.Context[CpuRegister.Rdi] = test.Output;
            test.Context[CpuRegister.Rsi] = 20 * 1024 * 1024;
            test.Context[CpuRegister.Rdx] = 3;
            test.Context[CpuRegister.Rcx] = 0x8000;

            Assert.Equal(0, KernelMemoryCompatExports.KernelMapFlexibleMemoryInternal(test.Context));
            Assert.True(test.Context.TryReadUInt64(test.Output, out var mapped));
            Assert.True(test.Context.TryWriteUInt64(mapped, 0x123456789ABCDEF0));
            Assert.True(test.Context.TryReadUInt64(mapped, out var value));
            Assert.Equal(0x123456789ABCDEF0UL, value);
        }
        finally
        {
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
            KernelMemoryCompatExports.SetFlexibleBackingForTests(previousPool);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservationLetsPendingGpuReadsFinishBeforeTakingMappingLocks(bool replaceMapping)
    {
        using var test = new BackedKernelMemory();
        const ulong address = 0x1026C00000;
        test.Reserve(0x10000, address);
        SetPrtAperture(test, address, 0x10000);
        using var memory = new GuestGpuMemory(test.Memory);
        GuestGpuMemoryHook.Attach(memory);
        try
        {
            test.Allocate(0, 0x4000);
            test.Map(0, 0x4000, address);
            Assert.True(memory.Covers(address, 0x4000));
            var relay = new PendingReadRelay(() =>
            {
                var bytes = new byte[0x10000];
                Assert.True(KernelMemoryCompatExports.TryReadPrtBacking(test.Memory, address, bytes));
            });
            memory.AttachGpuQueue(relay, null);
            var reserved = test.Reserve(0x10000, replaceMapping ? address : 0);
            Assert.Equal(1, relay.DispatchCount);
            if (replaceMapping)
                Assert.Equal(address, reserved);
            else
                Assert.NotEqual(address, reserved);
            Assert.Equal(!replaceMapping, memory.Covers(address, 0x4000));
            Assert.Equal((reserved, reserved + 0x10000), test.Query(reserved));
        }
        finally
        {
            memory.AttachGpuQueue(null, null);
            GuestGpuMemoryHook.Attach(null);
            SetPrtAperture(test, address, 0);
        }
    }

    private sealed class PendingReadRelay(Action readPendingImage) : IGpuQueueRelay
    {
        public bool IsGpuQueueThread { get; private set; }
        public int DispatchCount { get; private set; }
        public void Post(Action work) => RunOnGpuQueue(work);
        public void RunOnGpuQueue(Action work) => _ = TryRunOnGpuQueue(work);

        public bool TryRunOnGpuQueue(Action work)
        {
            DispatchCount++;
            // A blocked read must fail the test, not leave its host waiting forever.
            Task.Run(readPendingImage).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            IsGpuQueueThread = true;
            try { work(); }
            finally { IsGpuQueueThread = false; }
            return true;
        }
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("flexible")]
    [InlineData("unmap")]
    [InlineData("release")]
    [InlineData("checked-release")]
    [InlineData("reset")]
    [InlineData("batch")]
    public void MappingChangesLetPendingGpuReadsFinish(string operation)
    {
        using var test = new BackedKernelMemory();
        var previousPool = KernelMemoryCompatExports.SetFlexibleBackingForTests(new FlexibleBackingPool(0x4000000, 0x4000000));
        const ulong address = 0x1026C00000;
        test.Reserve(0x10000, address);
        SetPrtAperture(test, address, 0x10000);
        using var memory = new GuestGpuMemory(test.Memory);
        GuestGpuMemoryHook.Attach(memory);
        try
        {
            test.Allocate(0, 0x8000);
            test.Map(0, 0x4000, address);
            var relay = new PendingReadRelay(() =>
                Assert.True(KernelMemoryCompatExports.TryReadPrtBacking(test.Memory, address, new byte[0x10000])));
            memory.AttachGpuQueue(relay, null);
            switch (operation)
            {
                case "direct":
                    Assert.Equal(address, test.Map(0x4000, 0x4000, address));
                    break;
                case "flexible":
                    Assert.Equal(address, test.Flexible(0x4000, address));
                    break;
                case "unmap":
                    Assert.Equal(0, test.Unmap(address, 0x4000));
                    break;
                case "release":
                    test.Context[CpuRegister.Rdi] = 0;
                    test.Context[CpuRegister.Rsi] = 0x8000;
                    Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(test.Context));
                    break;
                case "checked-release":
                    Assert.Equal(0, test.Release(0, 0x8000));
                    break;
                case "reset":
                    KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
                    break;
                case "batch":
                    var entryAddress = test.Output + 0x200;
                    Assert.True(test.Context.TryWriteUInt64(entryAddress, address));
                    Assert.True(test.Context.TryWriteUInt64(entryAddress + 8, 0x4000));
                    Assert.True(test.Context.TryWriteUInt64(entryAddress + 16, 0x4000));
                    Assert.True(test.Context.TryWriteUInt64(entryAddress + 24, 0x33));
                    test.Context[CpuRegister.Rdi] = entryAddress;
                    test.Context[CpuRegister.Rsi] = 1;
                    test.Context[CpuRegister.Rdx] = test.Output + 0x240;
                    Assert.Equal(0, KernelMemoryCompatExports.KernelBatchMap(test.Context));
                    Assert.True(test.Context.TryReadUInt32(test.Output + 0x240, out var processed));
                    Assert.Equal(1u, processed);
                    break;
            }
            Assert.Equal(1, relay.DispatchCount);
            Assert.Equal(operation is "direct" or "flexible" or "batch", memory.Covers(address, 0x4000));
        }
        finally
        {
            memory.AttachGpuQueue(null, null);
            GuestGpuMemoryHook.Attach(null);
            SetPrtAperture(test, address, 0);
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
            KernelMemoryCompatExports.SetFlexibleBackingForTests(previousPool);
        }
    }

    [Fact]
    public void SparseImageReadCopiesResidentRangesAndClearsReservedGaps()
    {
        using var test = new BackedKernelMemory();
        const ulong address = 0x1026C00000;
        const int size = 0x10000;
        test.Reserve(size, address);
        test.Allocate(0, 0x8000);
        test.Map(0, 0x4000, address);
        test.Map(0x4000, 0x4000, address + 0xC000);
        SetPrtAperture(test, address, size);
        try
        {
            Assert.True(test.Memory.TryWriteBacking(address, Enumerable.Repeat((byte)0x35, 0x4000).ToArray()));
            Assert.True(test.Memory.TryWriteBacking(address + 0xC000, Enumerable.Repeat((byte)0x72, 0x4000).ToArray()));
            var result = Enumerable.Repeat((byte)0xFF, size).ToArray();
            Assert.False(test.Memory.TryReadBacking(address, result));
            Assert.True(KernelMemoryCompatExports.TryReadPrtBacking(test.Memory, address, result));
            Assert.All(result[..0x4000], value => Assert.Equal(0x35, value));
            Assert.All(result[0x4000..0xC000], value => Assert.Equal(0, value));
            Assert.All(result[0xC000..], value => Assert.Equal(0x72, value));
            Assert.True(KernelMemoryCompatExports.TryReadPrtBacking(test.Memory, address + 0x3FF0, result.AsSpan(0, 32)));
            Assert.All(result[..16], value => Assert.Equal(0x35, value));
            Assert.All(result[16..32], value => Assert.Equal(0, value));
        }
        finally
        {
            SetPrtAperture(test, address, 0);
        }
    }

    [Fact]
    public void SparseImageReadRejectsUnreservedAndOutOfApertureRanges()
    {
        using var test = new BackedKernelMemory();
        const ulong address = 0x1026C00000;
        test.Reserve(0x10000, address);
        SetPrtAperture(test, address, 0x20000);
        try
        {
            var result = Enumerable.Repeat((byte)0xAB, 0x20000).ToArray();
            Assert.False(KernelMemoryCompatExports.TryReadPrtBacking(test.Memory, address, result));
            Assert.All(result, value => Assert.Equal(0xAB, value));
            Assert.False(KernelMemoryCompatExports.TryReadPrtBacking(test.Memory, ulong.MaxValue, result));
            Assert.False(KernelMemoryCompatExports.TryReadPrtBacking(test.Memory, address, Span<byte>.Empty));
            SetPrtAperture(test, address, 0);
            Assert.False(KernelMemoryCompatExports.TryReadPrtBacking(test.Memory, address, result.AsSpan(0, 0x4000)));
            Assert.All(result, value => Assert.Equal(0xAB, value));
        }
        finally
        {
            SetPrtAperture(test, address, 0);
        }
    }

    private static void SetPrtAperture(BackedKernelMemory test, ulong address, ulong size)
    {
        test.Context[CpuRegister.Rdi] = 0;
        test.Context[CpuRegister.Rsi] = address;
        test.Context[CpuRegister.Rdx] = size;
        Assert.Equal(0, KernelRuntimeCompatExports.KernelSetPrtAperture(test.Context));
    }

    [Fact]
    public void NonFixedReservationHintDoesNotReuseAnExistingReservation()
    {
        using var test = new BackedKernelMemory();
        const ulong size = 0x4000000;
        var first = test.Reserve(size);
        Assert.True(test.Context.TryWriteUInt64(test.Output, first));
        test.Context[CpuRegister.Rdi] = test.Output;
        test.Context[CpuRegister.Rsi] = 0x10000;
        test.Context[CpuRegister.Rdx] = 0;
        test.Context[CpuRegister.Rcx] = 0;

        Assert.Equal(0, KernelRuntimeCompatExports.KernelReserveVirtualRange(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output, out var second));
        Assert.True(second >= first + size);
        Assert.Equal((first, first + size), test.Query(first));
        Assert.Equal((second, second + 0x10000), test.Query(second));
    }

    [Fact]
    public void AddressSearchUsesTheFullReservationExtent()
    {
        using var test = new BackedKernelMemory();
        const ulong size = 0x40000000;
        var start = test.Reserve(size);
        var query = typeof(KernelMemoryCompatExports).GetMethod("GetMappingSlices",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var regions = (Array)query.Invoke(null, [start + 0x10000, 0x10000UL, false])!;
        var region = Assert.Single(regions.Cast<object>());
        Assert.Equal(start, (ulong)region.GetType().GetProperty("Address")!.GetValue(region)!);
        Assert.Equal(size, (ulong)region.GetType().GetProperty("Length")!.GetValue(region)!);
        test.Allocate(0, 0x10000);
        Assert.True(test.Map(0, 0x10000) >= start + size);
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(0x4000UL, 0UL)]
    [InlineData(0UL, 0x8000UL)]
    public void NonFixedDirectMappingReusesAReservedHint(ulong hintOffset, ulong splitOffset)
    {
        using var test = new BackedKernelMemory();
        var start = test.Reserve(0x40000);
        if (splitOffset != 0)
            test.Reserve(splitOffset, start);
        test.Allocate(0, 0x10000);
        Assert.True(test.Context.TryWriteUInt64(test.Output, start + hintOffset));
        test.Context[CpuRegister.Rdi] = test.Output;
        test.Context[CpuRegister.Rsi] = 0x10000;
        test.Context[CpuRegister.Rdx] = 0x33;
        test.Context[CpuRegister.Rcx] = 0;
        test.Context[CpuRegister.R8] = 0;
        test.Context[CpuRegister.R9] = 0;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output, out var address));
        Assert.Equal(start + hintOffset, address);
        Assert.True(test.Memory.IsBackedRange(address, 0x10000));
    }

    [Fact]
    public void NonFixedDirectMappingSkipsAllReservationsTouchedByACandidate()
    {
        using var test = new BackedKernelMemory();
        var start = test.Reserve(0x40000);
        Assert.Equal(0, test.Unmap(start, 0x40000));
        test.Reserve(0x4000, start);
        test.Reserve(0x4000, start + 0x8000);
        test.Allocate(0, 0x10000);
        Assert.True(test.Context.TryWriteUInt64(test.Output, start));
        test.Context[CpuRegister.Rdi] = test.Output;
        test.Context[CpuRegister.Rsi] = 0x10000;
        test.Context[CpuRegister.Rdx] = 0x33;
        test.Context[CpuRegister.Rcx] = 0;
        test.Context[CpuRegister.R8] = 0;
        test.Context[CpuRegister.R9] = 0;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output, out var address));
        Assert.Equal(start + 0xC000, address);
        Assert.Equal((start, start + 0x4000), test.Query(start));
        Assert.Equal((start + 0x8000, start + 0xC000), test.Query(start + 0x8000));
    }

    [Fact]
    public void ReadRangeDiagnosticsReportViewsAndReservedGaps()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x8000);
        var address = test.Reserve(0x10000);
        test.Map(0, 0x4000, address);
        test.Map(0x4000, 0x4000, address + 0xC000);

        var result = test.Memory.DescribeReadRange(address, 0x10000);
        Assert.Contains("guest=backed backing=True host=Committed", result);
        Assert.Contains("guest=unmapped backing=False host=Reserved", result);
        Assert.Contains($"0x{address + 0x4000:X16}..0x{address + 0xC000:X16}", result);
        Assert.False(test.Memory.CanRead(address, 0x10000));
        Assert.True(test.Memory.IsBackedRange(address, 0x4000));
        Assert.True(test.Memory.IsBackedRange(address + 0xC000, 0x4000));
        Assert.Contains("guest=private", test.Memory.DescribeReadRange(test.Output, 16));
        Assert.Equal("The diagnostic memory range is invalid.", test.Memory.DescribeReadRange(ulong.MaxValue, 2));
    }

    [Fact]
    public void TransientProtectionSkipsReservedGapsBetweenViews()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x8000);
        var address = test.Reserve(0x10000);
        test.Map(0, 0x4000, address);
        test.Map(0x4000, 0x4000, address + 0xC000);

        Assert.True(test.Memory.TryProtect(address, 0x10000, GuestPageProtection.Read));
        Assert.True(test.Memory.TryProtect(address + 0x4000, 0x8000, GuestPageProtection.Read));
        Assert.True(test.Memory.TryProtect(address, 0x10000, GuestPageProtection.Read | GuestPageProtection.Write));
    }

    [Fact]
    public void TransientProtectionReportsMappedViewFailure()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x4000);
        var address = test.Map(0, 0x4000);
        test.Host.FailNext(FailingHostViews.Op.ChangeAccess);
        Assert.False(test.Memory.TryProtect(address, 0x4000, GuestPageProtection.Read));
    }

    [Fact]
    public void DirectAliasesKeepContentsAcrossUnmapAndReleaseTogether()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0xC000);
        var first = test.Map(0, 0xC000);
        var second = test.Map(0, 0xC000);
        Assert.True(test.Context.TryWriteUInt64(first + 0x4000, 0x12345678));
        Assert.True(test.Context.TryReadUInt64(second + 0x4000, out var value));
        Assert.Equal(0x12345678UL, value);
        Assert.Equal(0, test.Unmap(first + 0x4000, 0x4000));
        Assert.False(test.Memory.IsBackedView(first + 0x4000));
        Assert.Equal(first + 0x4000, test.Map(0x4000, 0x4000, first + 0x4000));
        Assert.True(test.Context.TryReadUInt64(first + 0x4000, out value));
        Assert.Equal(0x12345678UL, value);
        Assert.Equal(0, test.Release(0, 0xC000));
        Assert.False(test.Memory.IsBackedView(first));
        Assert.False(test.Memory.IsBackedView(second));
    }

    [Fact]
    public void FailedSecondAliasUnmapRestoresViewsAndGpuRegistration()
    {
        using var test = new BackedKernelMemory();
        using var gpu = new GuestGpuMemory(test.Memory);
        GuestGpuMemoryHook.Attach(gpu);
        try
        {
            test.Allocate(0, 0x4000);
            var first = test.Map(0, 0x4000);
            var second = test.Map(0, 0x4000);
            Assert.True(test.Context.TryWriteUInt64(first, 99));
            test.Host.FailNext(FailingHostViews.Op.UnmapView, afterCalls: 1);
            Assert.Equal(unchecked((int)0x80020002), test.Release(0, 0x4000));
            Assert.True(test.Memory.IsBackedView(first));
            Assert.True(test.Memory.IsBackedView(second));
            Assert.True(gpu.Covers(first, 0x4000));
            Assert.True(gpu.Covers(second, 0x4000));
            Assert.True(test.Context.TryReadUInt64(first, out var value));
            Assert.Equal(99UL, value);
            Assert.Equal(0, test.Release(0, 0x4000));
            Assert.False(gpu.Covers(first, 0x4000));
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }

    [Fact]
    public void FailedMapKeepsReservationAndUnmapKeepsHole()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x4000);
        var address = test.Reserve(0x10000);
        test.Host.FailNext(FailingHostViews.Op.MapView);
        Assert.Equal(unchecked((int)0x8002000C), test.MapResult(0, 0x4000, address));
        Assert.True(test.Memory.TryHoldRange(address, 0x10000));
        Assert.Equal((address, address + 0x10000), test.Query(address));
        Assert.Equal(address, test.Map(0, 0x4000, address));
        Assert.Equal(0, test.Unmap(address, 0x4000));
        Assert.Equal(address, test.Reserve(0x10000, address));
    }

    [Fact]
    public void VirtualQueryDistinguishesCommittedMappingFromReservedTail()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x10000);
        Assert.Equal(0u, test.QueryStateFlags(address) & 0x10u);

        test.Allocate(0, 0x4000);
        Assert.Equal(address, test.Map(0, 0x4000, address));
        Assert.Equal(0x10u, test.QueryStateFlags(address) & 0x10u);
        Assert.Equal(0u, test.QueryStateFlags(address + 0x4000) & 0x10u);
    }

    [Fact]
    public void FixedReplacementRestoresOldViewsWhenUnmapFails()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x8000);
        var address = test.Map(0, 0x4000);
        Assert.True(test.Context.TryWriteUInt64(address, 77));
        test.Host.FailNext(FailingHostViews.Op.UnmapView);
        Assert.Equal(unchecked((int)0x8002000C), test.MapResult(0x4000, 0x4000, address));
        Assert.True(test.Context.TryReadUInt64(address, out var value));
        Assert.Equal(77UL, value);
        Assert.Equal(address, test.Map(0x4000, 0x4000, address));
        Assert.True(test.Context.TryReadUInt64(address, out value));
        Assert.Equal(0UL, value);
    }

    [Fact]
    public void ProtectionUsesTheViewOwnerAndLeavesAliasWritable()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x4000);
        var address = test.Map(0, 0x4000);
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rsi] = 0x4000;
        test.Context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMprotect(test.Context));
        Assert.Contains(FailingHostViews.Op.ChangeAccess, test.Host.Log);
        Assert.True(test.Context.TryWriteUInt64(address, 100));
        Assert.True(test.Context.TryReadUInt64(address, out var value));
        Assert.Equal(100UL, value);
        test.Context[CpuRegister.Rdx] = 0x20;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMprotect(test.Context));
    }

    [Fact]
    public void ClearResetsPhysicalStateBeforeTheSameMemoryMapsAgain()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x4000);
        var address = test.Map(0, 0x4000);
        Assert.True(test.Context.TryWriteUInt64(address, 55));
        KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
        test.Memory.Clear();
        test.ResetOutput();
        test.Allocate(0, 0x4000);
        Assert.Equal(address, test.Map(0, 0x4000, address));
        Assert.True(test.Context.TryReadUInt64(address, out var value));
        Assert.Equal(55UL, value);
    }

    [Fact]
    public void UnalignedUnmapIsRefusedByTheOwnerAsAccessDenied()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x4000);
        var address = test.Map(0, 0x4000);
        Assert.Equal(unchecked((int)0x8002000D), test.Unmap(address + 0x1000, 0x2000));
        Assert.True(test.Memory.IsBackedView(address));
        Assert.Equal(0, test.Unmap(address, 0x4000));
    }

    [Fact]
    public void ReleaseCanCrossContiguousPhysicalAllocations()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x4000);
        test.Allocate(0x4000, 0x4000);
        var address = test.Map(0, 0x8000);
        Assert.Equal(0, test.Release(0, 0x8000));
        Assert.False(test.Memory.IsBackedView(address));
    }

    [Fact]
    public void PartialPhysicalReleaseRemovesBothAliasesAndReusesOnlyTheReleasedRange()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0xC000);
        var first = test.Map(0, 0xC000);
        var second = test.Map(0, 0xC000);
        Assert.True(test.Context.TryWriteUInt64(first, 123));
        Assert.True(test.Context.TryWriteUInt64(first + 0x8000, 456));
        Assert.Equal(0, test.Release(0x4000, 0x4000));
        foreach (var address in new[] { first, second })
        {
            Assert.True(test.Memory.IsBackedView(address));
            Assert.False(test.Memory.IsBackedView(address + 0x4000));
            Assert.True(test.Memory.IsBackedView(address + 0x8000));
        }
        Assert.Equal(unchecked((int)0x8002000C), test.MapResult(0x4000, 0x4000));
        test.Allocate(0x4000, 0x4000);
        Assert.Equal(first + 0x4000, test.Map(0x4000, 0x4000, first + 0x4000));
        Assert.False(test.Memory.IsBackedView(second + 0x4000));
        Assert.True(test.Context.TryReadUInt64(second, out var left));
        Assert.True(test.Context.TryReadUInt64(second + 0x8000, out var right));
        Assert.Equal(123UL, left);
        Assert.Equal(456UL, right);
    }

    [Fact]
    public void PhysicalQueryKeepsContainingAndNextRulesAfterPartialRelease()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0xC000);
        Assert.Equal(0, test.Release(0x4000, 0x4000));

        int Query(ulong offset, ulong flags)
        {
            test.Context[CpuRegister.Rdi] = offset;
            test.Context[CpuRegister.Rsi] = flags;
            test.Context[CpuRegister.Rdx] = test.Output;
            test.Context[CpuRegister.Rcx] = 24;
            return KernelMemoryCompatExports.KernelDirectMemoryQuery(test.Context);
        }

        Assert.Equal(unchecked((int)0x8002000D), Query(0x4000, 0));
        Assert.Equal(0, Query(0x4000, 1));
        Assert.True(test.Context.TryReadUInt64(test.Output, out var start));
        Assert.True(test.Context.TryReadUInt64(test.Output + 8, out var end));
        Assert.Equal((0x8000UL, 0xC000UL), (start, end));
        Assert.Equal(0, Query(0x1000, 1));
        Assert.True(test.Context.TryReadUInt64(test.Output, out start));
        Assert.True(test.Context.TryReadUInt64(test.Output + 8, out end));
        Assert.Equal((0UL, 0x4000UL), (start, end));
        Assert.Equal(unchecked((int)0x8002000D), Query(0xC000, 1));
    }

    [Fact]
    public void FailedMultiPieceUnmapKeepsTheRemainingPieceMapped()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x8000);
        var address = test.Reserve(0x8000);
        test.Map(0, 0x4000, address);
        test.Map(0x4000, 0x4000, address + 0x4000);
        test.Host.FailNext(FailingHostViews.Op.UnmapView, afterCalls: 1);
        Assert.Equal(unchecked((int)0x8002000D), test.Unmap(address, 0x8000));
        Assert.False(test.Memory.IsBackedView(address));
        Assert.True(test.Memory.IsBackedView(address + 0x4000));
        Assert.Equal((address + 0x4000, address + 0x8000), test.Query(address + 0x4000));
        Assert.Equal(0, test.Release(0, 0x8000));
    }

    [Fact]
    public void ClearAlsoResetsAllocatedButUnmappedPhysicalMemory()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x4000);
        KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
        test.Memory.Clear();
        test.ResetOutput();
        test.Allocate(0, 0x4000);
    }

    [Fact]
    public void MissingPhysicalAllocationAndUnalignedReleaseHaveDistinctErrors()
    {
        using var test = new BackedKernelMemory();
        Assert.Equal(unchecked((int)0x8002000C), test.MapResult(0, 0x4000));
        Assert.Equal(unchecked((int)0x80020016), test.Release(1, 0x4000));
        Assert.Equal(unchecked((int)0x80020002), test.Release(0, 0x4000));
        test.Context[CpuRegister.Rdi] = 0;
        test.Context[CpuRegister.Rsi] = 0x4000;
        Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(test.Context));
    }

    [Fact]
    public void MemoryWithoutBackingCannotUseThePrivateAllocationFallback()
    {
        using var memory = new PhysicalVirtualMemory();
        try
        {
            var context = new CpuContext(memory, Generation.Gen5);
            var output = memory.AllocateAt(0, 0x4000, false);
            Assert.True(context.TryWriteUInt64(output, 0));
            context[CpuRegister.Rdi] = output;
            context[CpuRegister.Rsi] = 0x4000;
            context[CpuRegister.Rdx] = 3;
            Assert.Equal(unchecked((int)0x8002000C), KernelMemoryCompatExports.KernelMapFlexibleMemory(context));
        }
        finally
        {
            KernelMemoryCompatExports.ResetBackingMappings(memory);
        }
    }

    [Fact]
    public void OccupiedApertureFailsWithoutReplacingPrivateMemory()
    {
        using var test = new BackedKernelMemory();
        const ulong address = 0x10_0000_0000;
        Assert.Equal(address, test.Memory.AllocateAt(address, 0x10000, false, false));
        Assert.True(test.Context.TryWriteUInt64(address, 321));
        test.Allocate(0, 0x4000);
        Assert.Equal(unchecked((int)0x8002000C), test.MapResult(0, 0x4000, address));
        Assert.True(test.Context.TryReadUInt64(address, out var value));
        Assert.Equal(321UL, value);
    }

    [Fact]
    public void FlexibleMappingsAreZeroedOnEveryAllocation()
    {
        var previous = KernelMemoryCompatExports.SetFlexibleBackingForTests(new FlexibleBackingPool(0x4000000, 0x4000000));
        try
        {
            using var test = new BackedKernelMemory();
            var address = test.Flexible(0x8000);
            Assert.True(test.Context.TryWriteUInt64(address, 123));
            Assert.Equal(0, test.Unmap(address, 0x8000));
            Assert.Equal(address, test.Flexible(0x8000, address));
            Assert.True(test.Context.TryReadUInt64(address, out var value));
            Assert.Equal(0UL, value);
        }
        finally
        {
            KernelMemoryCompatExports.SetFlexibleBackingForTests(previous);
        }
    }
}

internal sealed class BackedKernelMemory : IDisposable
{
    public FailingHostViews Host { get; } = new(HostViewMemory.Create());
    public PhysicalVirtualMemory Memory { get; }
    public CpuContext Context { get; }
    public ulong Output { get; private set; }

    public BackedKernelMemory(ulong bytes = 128UL * 1024 * 1024)
    {
        Memory = new PhysicalVirtualMemory(viewHost: Host, backingBytes: bytes);
        Context = new CpuContext(Memory, Generation.Gen5);
        ResetOutput();
    }

    public void ResetOutput() => Output = Memory.AllocateAt(0, 0x4000, false);

    public void Allocate(ulong start, ulong size)
    {
        Context[CpuRegister.Rdi] = start;
        Context[CpuRegister.Rsi] = start + size;
        Context[CpuRegister.Rdx] = size;
        Context[CpuRegister.Rcx] = 0x4000;
        Context[CpuRegister.R8] = 0;
        Context[CpuRegister.R9] = Output;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(Context));
        Assert.True(Context.TryReadUInt64(Output, out var result));
        Assert.Equal(start, result);
    }

    public int MapResult(ulong offset, ulong size, ulong address = 0)
    {
        Assert.True(Context.TryWriteUInt64(Output, address));
        Context[CpuRegister.Rdi] = Output;
        Context[CpuRegister.Rsi] = size;
        Context[CpuRegister.Rdx] = 0x33;
        Context[CpuRegister.Rcx] = address == 0 ? 0UL : 0x10UL;
        Context[CpuRegister.R8] = offset;
        Context[CpuRegister.R9] = 0;
        return KernelMemoryCompatExports.KernelMapDirectMemory(Context);
    }

    public ulong Map(ulong offset, ulong size, ulong address = 0)
    {
        Assert.Equal(0, MapResult(offset, size, address));
        Assert.True(Context.TryReadUInt64(Output, out var result));
        return result;
    }

    public ulong Reserve(ulong size, ulong address = 0)
    {
        Assert.True(Context.TryWriteUInt64(Output, address));
        Context[CpuRegister.Rdi] = Output;
        Context[CpuRegister.Rsi] = size;
        Context[CpuRegister.Rdx] = address == 0 ? 0UL : 0x10UL;
        Context[CpuRegister.Rcx] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.KernelReserveVirtualRange(Context));
        Assert.True(Context.TryReadUInt64(Output, out var result));
        return result;
    }

    public ulong Flexible(ulong size, ulong address = 0)
    {
        Assert.True(Context.TryWriteUInt64(Output, address));
        Context[CpuRegister.Rdi] = Output;
        Context[CpuRegister.Rsi] = size;
        Context[CpuRegister.Rdx] = 0x33;
        Context[CpuRegister.Rcx] = address == 0 ? 0UL : 0x10UL;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMapFlexibleMemory(Context));
        Assert.True(Context.TryReadUInt64(Output, out var result));
        return result;
    }

    public int Unmap(ulong address, ulong size)
    {
        Context[CpuRegister.Rdi] = address;
        Context[CpuRegister.Rsi] = size;
        return KernelMemoryCompatExports.KernelMunmap(Context);
    }

    public int Release(ulong offset, ulong size)
    {
        Context[CpuRegister.Rdi] = offset;
        Context[CpuRegister.Rsi] = size;
        return KernelMemoryCompatExports.KernelCheckedReleaseDirectMemory(Context);
    }

    public (ulong Start, ulong End) Query(ulong address)
    {
        Context[CpuRegister.Rdi] = address;
        Context[CpuRegister.Rsi] = 0;
        Context[CpuRegister.Rdx] = Output + 0x100;
        Context[CpuRegister.Rcx] = 0x48;
        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(Context));
        Assert.True(Context.TryReadUInt64(Output + 0x100, out var start));
        Assert.True(Context.TryReadUInt64(Output + 0x108, out var end));
        return (start, end);
    }

    public uint QueryStateFlags(ulong address)
    {
        Context[CpuRegister.Rdi] = address;
        Context[CpuRegister.Rsi] = 0;
        Context[CpuRegister.Rdx] = Output + 0x100;
        Context[CpuRegister.Rcx] = 0x48;
        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(Context));
        Assert.True(Context.TryReadUInt32(Output + 0x120, out var stateFlags));
        return stateFlags;
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.ResetBackingMappings(Memory);
        Memory.Dispose();
    }
}

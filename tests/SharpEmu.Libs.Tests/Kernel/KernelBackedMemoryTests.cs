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
        using var gpu = new GuestGpuMemory(test.Memory, new IdleBufferStore(), new IdleImageStore());
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

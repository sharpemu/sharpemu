// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Globalization;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(KernelMemoryCompatStateCollection.Name, DisableParallelization = true)]
public sealed class KernelMemoryCompatStateCollection
{
    public const string Name = "KernelMemoryCompatState";
}

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMemoryCompatExportsTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong AllocationOutAddress = GuestMemoryBase + 0x100;
    private const ulong SpanStartOutAddress = GuestMemoryBase + 0x108;
    private const ulong SpanSizeOutAddress = GuestMemoryBase + 0x110;

    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_FragmentedRangeReturnsLargestAlignedSpan()
    {
        const ulong firstAllocationStart = 0x0020_0000;
        const ulong firstAllocationLength = 0x0020_0000;
        const ulong secondAllocationStart = 0x00C0_0000;
        const ulong secondAllocationLength = 0x0040_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, firstAllocationStart, firstAllocationLength);
            AllocateDirectMemory(context, secondAllocationStart, secondAllocationLength);

            QueryAvailableDirectMemory(context, 0, 0x0100_0000, 0x4000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0040_0000UL, spanStart);
            Assert.Equal(0x0080_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, firstAllocationStart, firstAllocationLength);
            ReleaseDirectMemory(context, secondAllocationStart, secondAllocationLength);
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_AppliesAlignmentBeforeComparingSpans()
    {
        const ulong allocationStart = 0x0070_0000;
        const ulong allocationLength = 0x0010_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, allocationStart, allocationLength);

            QueryAvailableDirectMemory(context, 0x0010_0000, 0x00C0_0000, 0x0040_0000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0080_0000UL, spanStart);
            Assert.Equal(0x0040_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, allocationStart, allocationLength);
        }
    }

    private static void AllocateDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = start + length;
        context[CpuRegister.Rdx] = length;
        context[CpuRegister.Rcx] = 0x4000;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = AllocationOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(AllocationOutAddress, out var allocatedAddress));
        Assert.Equal(start, allocatedAddress);
    }

    private static void QueryAvailableDirectMemory(
        CpuContext context,
        ulong searchStart,
        ulong searchEnd,
        ulong alignment)
    {
        context[CpuRegister.Rdi] = searchStart;
        context[CpuRegister.Rsi] = searchEnd;
        context[CpuRegister.Rdx] = alignment;
        context[CpuRegister.Rcx] = SpanStartOutAddress;
        context[CpuRegister.R8] = SpanSizeOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAvailableDirectMemorySize(context));
    }

    private static void ReleaseDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = length;

        Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AmprAmmExportsTests
{
    private const int AmmInvalidArgument = unchecked((int)0x80020016);
    private const int AmmNoSuchProcess = unchecked((int)0x80020003);
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong OutputAddress = GuestMemoryBase + 0x100;

    public AmprAmmExportsTests()
    {
        AmprExports.ResetRuntimeState();
        KernelAprCompatExports.ResetRuntimeState();
    }

    [Fact]
    public void GeneralAmmSurfaceRegistersWithExactNames()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        var expected = new Dictionary<string, string>
        {
            ["EDq5bqCqYpA"] = "sceAmprAmmCommandBufferConstructor",
            ["pvUFDOHilnE"] = "sceAmprAmmCommandBufferDestructor",
            ["JEVYGhDc97M"] = "sceAmprAmmCommandBufferMap",
            ["ojBkmG7+CgE"] = "sceAmprAmmCommandBufferMapWithGpuMaskId",
            ["8TBE+9XCZbI"] = "sceAmprAmmCommandBufferMapDirect",
            ["kOfZlhbVAkc"] = "sceAmprAmmCommandBufferMapDirectWithGpuMaskId",
            ["M-VFI2DJWQA"] = "sceAmprAmmCommandBufferUnmap",
            ["lwS-7y3jcBI"] = "sceAmprAmmSubmitCommandBuffer",
            ["OJf3vCckPAM"] = "sceAmprAmmSubmitCommandBuffer2",
            ["NnKhlMJtIsI"] = "sceAmprAmmSubmitCommandBuffer3",
            ["HXymib4T8gc"] = "sceAmprAmmWaitCommandBufferCompletion",
        };

        foreach (var (nid, name) in expected)
        {
            Assert.True(manager.TryGetExport(nid, out var export), nid);
            Assert.Equal(name, export.Name);
            Assert.Equal("libSceAmpr", export.LibraryName);
        }
    }

    [Fact]
    public void GetVirtualAddressRangesReportsExtendedApertureAndEmptyMultimapRange()
    {
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = GuestMemoryBase + 0x100;
        context[CpuRegister.Rsi] = GuestMemoryBase + 0x108;
        context[CpuRegister.Rdx] = GuestMemoryBase + 0x110;
        context[CpuRegister.Rcx] = GuestMemoryBase + 0x118;

        Assert.Equal(0, AmprExports.AmmGetVirtualAddressRanges(context));
        Assert.True(context.TryReadUInt64(GuestMemoryBase + 0x100, out var vaStart));
        Assert.True(context.TryReadUInt64(GuestMemoryBase + 0x108, out var vaEnd));
        Assert.True(context.TryReadUInt64(GuestMemoryBase + 0x110, out var multimapStart));
        Assert.True(context.TryReadUInt64(GuestMemoryBase + 0x118, out var multimapEnd));
        Assert.Equal(GuestMemoryLayout.GuestExtendedAddressStart, vaStart);
        Assert.Equal(GuestMemoryLayout.GuestExtendedAddressLimit, vaEnd);
        Assert.Equal(vaEnd, multimapStart);
        Assert.Equal(vaEnd, multimapEnd);
    }

    [Theory]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressStart, 0x4000UL, 0)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressLimit - 0x4000, 0x4000UL, 0)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressStart - 0x4000, 0x4000UL, AmmInvalidArgument)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressLimit, 0x4000UL, AmmInvalidArgument)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressLimit - 0x4000, 0x8000UL, AmmInvalidArgument)]
    [InlineData(GuestMemoryLayout.GuestUserAddressStart, 0x4000UL, AmmInvalidArgument)]
    public void AutomaticMapCommandsRequireTheExtendedAperture(
        ulong address,
        ulong size,
        int expectedResult)
    {
        using var test = new BackedKernelMemory();
        var commandBuffer = test.Output + 0x100;
        var backingBuffer = test.Output + 0x1000;

        try
        {
            InitializeCommandBuffer(test, commandBuffer, backingBuffer);

            test.Context[CpuRegister.Rdi] = commandBuffer;
            test.Context[CpuRegister.Rsi] = address;
            test.Context[CpuRegister.Rdx] = size;
            test.Context[CpuRegister.Rcx] = 0x0C;
            test.Context[CpuRegister.R8] = 0x80;
            Assert.Equal(expectedResult, AmprExports.AmmCommandBufferMap(test.Context));

            test.Context[CpuRegister.Rdi] = commandBuffer;
            test.Context[CpuRegister.Rsi] = address;
            test.Context[CpuRegister.Rdx] = size;
            test.Context[CpuRegister.Rcx] = 0x0C;
            test.Context[CpuRegister.R8] = 0x80;
            test.Context[CpuRegister.R9] = 1;
            Assert.Equal(expectedResult, AmprExports.AmmCommandBufferMapWithGpuMaskId(test.Context));
        }
        finally
        {
            test.Context[CpuRegister.Rdi] = commandBuffer;
            _ = AmprExports.CommandBufferDestructor(test.Context);
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
        }
    }

    [Fact]
    public void GiveDirectMemoryUsesAmmBlockRulesAndSharedKernelAllocator()
    {
        const ulong size = 0x20_0000;
        var searchEnd = GuestMemoryLayout.DirectBytes;
        var searchStart = searchEnd - (16UL * size);
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);
        ulong allocation = 0;

        try
        {
            context[CpuRegister.Rdi] = searchStart;
            context[CpuRegister.Rsi] = searchEnd;
            context[CpuRegister.Rdx] = size;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 1;
            context[CpuRegister.R9] = OutputAddress;

            Assert.Equal(0, AmprExports.AmmGiveDirectMemory(context));
            Assert.True(context.TryReadUInt64(OutputAddress, out allocation));
            Assert.InRange(allocation, searchStart, searchEnd - size);
            Assert.Equal(0UL, allocation & (size - 1));
        }
        finally
        {
            if (allocation != 0)
            {
                context[CpuRegister.Rdi] = allocation;
                context[CpuRegister.Rsi] = size;
                Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
            }
        }
    }

    [Theory]
    [InlineData(0x4000UL, 0UL, 0)]
    [InlineData(0x20_0000UL, 0x4000UL, 0)]
    [InlineData(0x20_0000UL, 0UL, 2)]
    public void GiveDirectMemoryRejectsNonAmmBlockArguments(ulong size, ulong alignment, int usage)
    {
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = GuestMemoryLayout.DirectBytes;
        context[CpuRegister.Rdx] = size;
        context[CpuRegister.Rcx] = alignment;
        context[CpuRegister.R8] = unchecked((ulong)(long)usage);
        context[CpuRegister.R9] = OutputAddress;

        Assert.Equal(AmmInvalidArgument, AmprExports.AmmGiveDirectMemory(context));
    }

    [Fact]
    public void MapCommandMaterializesReservedMemoryOnlyWhenSubmitted()
    {
        const ulong mapSize = 0x1_0000;
        const ulong automaticGrantSize = 0x20_0000;
        using var test = new BackedKernelMemory();
        const ulong target = GuestMemoryLayout.GuestExtendedAddressStart + 0x0100_0000;
        var commandBuffer = test.Output + 0x100;
        var backingBuffer = test.Output + 0x1000;
        var grantOutput = test.Output + 0x80;

        try
        {
            test.Context[CpuRegister.Rdi] = 0;
            test.Context[CpuRegister.Rsi] = automaticGrantSize;
            test.Context[CpuRegister.Rdx] = automaticGrantSize;
            test.Context[CpuRegister.Rcx] = 0;
            test.Context[CpuRegister.R8] = 1;
            test.Context[CpuRegister.R9] = grantOutput;
            Assert.Equal(0, AmprExports.AmmGiveDirectMemory(test.Context));

            Assert.Equal(target, test.Reserve(mapSize, target));
            Assert.False(test.Memory.IsBackedRange(target, mapSize));

            test.Context[CpuRegister.Rdi] = commandBuffer;
            Assert.Equal(0, AmprExports.CommandBufferConstructor(test.Context));
            test.Context[CpuRegister.Rdi] = commandBuffer;
            test.Context[CpuRegister.Rsi] = backingBuffer;
            test.Context[CpuRegister.Rdx] = 0x100;
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(test.Context));

            test.Context[CpuRegister.Rdi] = commandBuffer;
            test.Context[CpuRegister.Rsi] = target;
            test.Context[CpuRegister.Rdx] = mapSize;
            test.Context[CpuRegister.Rcx] = 0x0C;
            test.Context[CpuRegister.R8] = 0x80; // AMPR write, normalized to CPU read/write at execution.
            Assert.Equal(0, AmprExports.AmmCommandBufferMap(test.Context));
            Assert.True(test.Context.TryReadUInt32(commandBuffer + 4, out var writeOffset));
            Assert.Equal(0x20U, writeOffset);
            Assert.False(test.Memory.IsBackedRange(target, mapSize));

            var record = new byte[0x20];
            Assert.True(test.Context.Memory.TryRead(backingBuffer, record));
            Assert.All(record, value => Assert.Equal(0, value));

            test.Context[CpuRegister.Rdi] = commandBuffer;
            Assert.Equal(0, AmprExports.CommandBufferGetBufferBaseAddress(test.Context));
            Assert.Equal(backingBuffer, test.Context[CpuRegister.Rax]);

            test.Context[CpuRegister.Rdi] = backingBuffer;
            test.Context[CpuRegister.Rsi] = ulong.MaxValue; // ABI field is ignored, not a byte limit.
            test.Context[CpuRegister.Rdx] = 1;
            Assert.Equal(0, AmprExports.AmmSubmitCommandBuffer(test.Context));
            Assert.True(test.Memory.IsBackedRange(target, mapSize));
            Assert.True(test.Context.TryWriteUInt64(target, 0x1234_5678_9ABC_DEF0));
            Assert.True(test.Context.TryReadUInt64(target, out var observed));
            Assert.Equal(0x1234_5678_9ABC_DEF0UL, observed);
        }
        finally
        {
            test.Context[CpuRegister.Rdi] = commandBuffer;
            _ = AmprExports.CommandBufferDestructor(test.Context);
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
        }
    }

    [Fact]
    public void DelayedMapDoesNotReplaceANewerRangeOwnerAndStillCompletesFollowingRecords()
    {
        const ulong mapSize = 0x1_0000;
        const ulong automaticGrantSize = 0x20_0000;
        const ulong newerDirectOffset = automaticGrantSize;
        const ulong sentinel = 0x0123_4567_89AB_CDEF;
        const ulong completionValue = 0xA55A_5AA5_C33C_3CC3;
        using var test = new BackedKernelMemory();
        const ulong target = GuestMemoryLayout.GuestExtendedAddressStart + 0x0200_0000;
        var commandBuffer = test.Output + 0x100;
        var backingBuffer = test.Output + 0x1000;
        var grantOutput = test.Output + 0x80;
        var completionAddress = test.Output + 0x300;

        try
        {
            test.Context[CpuRegister.Rdi] = 0;
            test.Context[CpuRegister.Rsi] = automaticGrantSize;
            test.Context[CpuRegister.Rdx] = automaticGrantSize;
            test.Context[CpuRegister.Rcx] = 0;
            test.Context[CpuRegister.R8] = 1;
            test.Context[CpuRegister.R9] = grantOutput;
            Assert.Equal(0, AmprExports.AmmGiveDirectMemory(test.Context));

            test.Allocate(newerDirectOffset, mapSize);
            Assert.Equal(target, test.Reserve(mapSize, target));
            InitializeCommandBuffer(test, commandBuffer, backingBuffer);

            test.Context[CpuRegister.Rdi] = commandBuffer;
            test.Context[CpuRegister.Rsi] = target;
            test.Context[CpuRegister.Rdx] = mapSize;
            test.Context[CpuRegister.Rcx] = 0x0C;
            test.Context[CpuRegister.R8] = 0x80;
            Assert.Equal(0, AmprExports.AmmCommandBufferMap(test.Context));

            test.Context[CpuRegister.Rdi] = commandBuffer;
            test.Context[CpuRegister.Rsi] = completionAddress;
            test.Context[CpuRegister.Rdx] = completionValue;
            Assert.Equal(0, AmprExports.CommandBufferWriteAddressOnCompletion(test.Context));

            Assert.Equal(target, test.Map(newerDirectOffset, mapSize, target));
            Assert.True(test.Context.TryWriteUInt64(target, sentinel));
            Submit(test, backingBuffer);

            Assert.True(test.Context.TryReadUInt64(completionAddress, out var completed));
            Assert.Equal(completionValue, completed);
            Assert.True(test.Context.TryReadUInt64(target, out var observed));
            Assert.Equal(sentinel, observed);
        }
        finally
        {
            test.Context[CpuRegister.Rdi] = commandBuffer;
            _ = AmprExports.CommandBufferDestructor(test.Context);
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
        }
    }

    [Fact]
    public void UsageStatsAndMeasureExportsUseNativeLayoutsAndSizes()
    {
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(OutputAddress, 0x18));
        context[CpuRegister.Rdi] = OutputAddress;

        Assert.Equal(0, AmprExports.AmmGetUsageStatsData(context));
        var stats = new byte[0x18];
        Assert.True(memory.TryRead(OutputAddress, stats));
        Assert.Equal(0x18UL, BinaryPrimitives.ReadUInt64LittleEndian(stats));
        Assert.Equal(512, BinaryPrimitives.ReadUInt16LittleEndian(stats.AsSpan(8)));
        Assert.Equal(0x7U, BinaryPrimitives.ReadUInt32LittleEndian(stats.AsSpan(0x10)));

        Assert.Equal(0x20, AmprExports.MeasureAmmCommandSizeMap(context));
        Assert.Equal(0x20, AmprExports.MeasureAmmCommandSizeMapWithGpuMaskId(context));
        Assert.Equal(0x30, AmprExports.MeasureAmmCommandSizeMapDirect(context));
        Assert.Equal(0x30, AmprExports.MeasureAmmCommandSizeMapDirectWithGpuMaskId(context));
        Assert.Equal(0x20, AmprExports.MeasureAmmCommandSizeUnmap(context));

        Assert.True(context.TryWriteUInt64(OutputAddress, 0x19));
        Assert.Equal(AmmInvalidArgument, AmprExports.AmmGetUsageStatsData(context));
    }

    [Fact]
    public void DirectMapAndUnmapCommandsExecuteInSubmissionOrder()
    {
        const ulong mapSize = 0x1_0000;
        using var test = new BackedKernelMemory();
        test.Allocate(0, mapSize);
        var target = SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport.ProbeGuestAddress(
            test.Host,
            mapSize);
        var commandBuffer = test.Output + 0x100;
        var backingBuffer = test.Output + 0x1000;

        Assert.Equal(target, test.Reserve(mapSize, target));
        InitializeCommandBuffer(test, commandBuffer, backingBuffer);
        test.Context[CpuRegister.Rdi] = commandBuffer;
        test.Context[CpuRegister.Rsi] = target;
        test.Context[CpuRegister.Rdx] = 0;
        test.Context[CpuRegister.Rcx] = mapSize;
        test.Context[CpuRegister.R8] = 0;
        test.Context[CpuRegister.R9] = 0x03;
        Assert.Equal(0, AmprExports.AmmCommandBufferMapDirect(test.Context));
        Submit(test, backingBuffer);
        Assert.True(test.Memory.IsBackedRange(target, mapSize));

        test.Context[CpuRegister.Rdi] = commandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferDestructor(test.Context));
        InitializeCommandBuffer(test, commandBuffer, backingBuffer);
        test.Context[CpuRegister.Rdi] = commandBuffer;
        test.Context[CpuRegister.Rsi] = target;
        test.Context[CpuRegister.Rdx] = mapSize;
        Assert.Equal(0, AmprExports.AmmCommandBufferUnmap(test.Context));
        Submit(test, backingBuffer);
        Assert.False(test.Memory.IsBackedRange(target, mapSize));
    }

    [Fact]
    public void ResultSubmissionReportsExecutionFailureAndWaitRetiresItsId()
    {
        const ulong mapSize = 0x1_0000;
        using var test = new BackedKernelMemory();
        const ulong target = GuestMemoryLayout.GuestExtendedAddressStart + 0x0300_0000;
        var commandBuffer = test.Output + 0x100;
        var backingBuffer = test.Output + 0x1000;
        var resultAddress = test.Output + 0x200;
        var idAddress = test.Output + 0x210;

        Assert.Equal(target, test.Reserve(mapSize, target));
        InitializeCommandBuffer(test, commandBuffer, backingBuffer);
        test.Context[CpuRegister.Rdi] = commandBuffer;
        test.Context[CpuRegister.Rsi] = target;
        test.Context[CpuRegister.Rdx] = mapSize;
        test.Context[CpuRegister.Rcx] = 0;
        test.Context[CpuRegister.R8] = 0x03;
        Assert.Equal(0, AmprExports.AmmCommandBufferMap(test.Context));

        test.Context[CpuRegister.Rdi] = backingBuffer;
        test.Context[CpuRegister.Rsi] = ulong.MaxValue;
        test.Context[CpuRegister.Rdx] = 2;
        test.Context[CpuRegister.Rcx] = resultAddress;
        test.Context[CpuRegister.R8] = idAddress;
        Assert.Equal(0, AmprExports.AmmSubmitCommandBufferAndGetResult(test.Context));
        Assert.True(test.Context.TryReadUInt32(idAddress, out var submissionId));
        Assert.NotEqual(0U, submissionId);
        Assert.True(test.Context.TryReadUInt32(resultAddress, out var executionResult));
        Assert.Equal(unchecked((uint)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN), executionResult);
        Assert.True(test.Context.TryReadUInt32(resultAddress + 4, out var errorOffset));
        Assert.Equal(0U, errorOffset);

        test.Context[CpuRegister.Rdi] = submissionId;
        Assert.Equal(0, AmprExports.AmmWaitCommandBufferCompletion(test.Context));
        Assert.Equal(AmmNoSuchProcess, AmprExports.AmmWaitCommandBufferCompletion(test.Context));
    }

    private static void InitializeCommandBuffer(
        BackedKernelMemory test,
        ulong commandBuffer,
        ulong backingBuffer)
    {
        test.Context[CpuRegister.Rdi] = commandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(test.Context));
        test.Context[CpuRegister.Rdi] = commandBuffer;
        test.Context[CpuRegister.Rsi] = backingBuffer;
        test.Context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(test.Context));
    }

    private static void Submit(BackedKernelMemory test, ulong backingBuffer)
    {
        test.Context[CpuRegister.Rdi] = backingBuffer;
        test.Context[CpuRegister.Rsi] = ulong.MaxValue;
        test.Context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, AmprExports.AmmSubmitCommandBuffer(test.Context));
    }
}

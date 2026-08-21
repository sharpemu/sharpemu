// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// The kernel event-queue registry is process-wide static state; serialize against
// other suites that register graphics events.
[CollectionDefinition(AgcCommandBufferChainCollection.Name, DisableParallelization = true)]
public sealed class AgcCommandBufferChainCollection
{
    public const string Name = "AgcCommandBufferChainState";
}

// A submission is one link of a chain, not always the whole command stream. When a
// title's command arena fills mid-frame it continues in a fresh buffer, links the two
// with an INDIRECT_BUFFER packet and submits only the first link, so a parser that
// stops at the end of the submitted window silently drops the rest of that frame --
// including its flip and the end-of-frame labels the guest waits on.
[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcCommandBufferChainTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x14000;

    private const ulong HandleOutAddress = BaseAddress + 0x100;
    private const ulong EventsAddress = BaseAddress + 0x200;
    private const ulong OutCountAddress = BaseAddress + 0x300;
    private const ulong TimeoutAddress = BaseAddress + 0x400;
    private const ulong SubmitPacketAddress = BaseAddress + 0x500;
    private const ulong StackAddress = BaseAddress + 0x600;

    private const ulong CommandBufferAddress = BaseAddress + 0x800;
    private const ulong FirstLinkAddress = BaseAddress + 0x1000;
    private const ulong SecondLinkAddress = BaseAddress + 0x2000;
    private const ulong WaitLabelAddress = BaseAddress + 0x3000;

    // Graphics-queue completions land on ident 0, so its absence is how a suspended
    // queue is observed without standing up a GPU backend.
    private const ulong GraphicsCompletionIdent = 0;

    [Fact]
    public void SubmittedDcb_FollowsIndirectBufferIntoTheChainedBuffer()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        try
        {
            RegisterGraphicsCompletion(equeue);

            // The chained buffer parks on a label that never reaches its reference,
            // so reaching it at all suspends the queue.
            var secondLinkDwords = WriteUnsatisfiedWait(ctx, memory, SecondLinkAddress);
            var firstLinkDwords = WriteChain(
                ctx,
                memory,
                FirstLinkAddress,
                SecondLinkAddress,
                secondLinkDwords);

            SubmitDcb(ctx, memory, FirstLinkAddress, firstLinkDwords);

            Assert.NotEqual(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                WaitEqueue(ctx, memory, equeue));
        }
        finally
        {
            DeleteEqueue(ctx, equeue);
        }
    }

    // Titles emit a zeroed INDIRECT_BUFFER as padding for a branch they decided not to
    // take. Treating that as a redirect truncates the frame it appears in.
    [Fact]
    public void SubmittedDcb_KeepsParsingPastAnEmptyIndirectBuffer()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        try
        {
            RegisterGraphicsCompletion(equeue);

            var paddingDwords = WriteChain(ctx, memory, FirstLinkAddress, target: 0, targetDwords: 0);
            var waitDwords = WriteUnsatisfiedWait(
                ctx,
                memory,
                FirstLinkAddress + (paddingDwords * sizeof(uint)));

            SubmitDcb(ctx, memory, FirstLinkAddress, paddingDwords + waitDwords);

            Assert.NotEqual(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                WaitEqueue(ctx, memory, equeue));
        }
        finally
        {
            DeleteEqueue(ctx, equeue);
        }
    }

    [Fact]
    public void DcbJump_EncodesModeCachePolicyAndSize()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);

        PointCommandBufferAt(memory, FirstLinkAddress);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0; // call
        ctx[CpuRegister.Rdx] = 2; // bypass
        ctx[CpuRegister.Rcx] = SecondLinkAddress;
        ctx[CpuRegister.R8] = 0x123;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbJump(ctx));
        Assert.Equal(FirstLinkAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(unchecked((uint)SecondLinkAddress), ReadUInt32(memory, FirstLinkAddress + 4));
        Assert.Equal(0x2F20_0123u, ReadUInt32(memory, FirstLinkAddress + 12));
    }

    [Fact]
    public void AcbJump_AlwaysEncodesChainMode()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);

        PointCommandBufferAt(memory, FirstLinkAddress);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = SecondLinkAddress;
        ctx[CpuRegister.Rdx] = 0x123;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.AcbJump(ctx));
        Assert.Equal(FirstLinkAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(unchecked((uint)SecondLinkAddress), ReadUInt32(memory, FirstLinkAddress + 4));
        Assert.Equal(0x0F30_0123u, ReadUInt32(memory, FirstLinkAddress + 12));
    }

    [Fact]
    public void SubmittedDcb_ReturnsToParentAfterIndirectCall()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        try
        {
            RegisterGraphicsCompletion(equeue);
            WriteUInt32(memory, SecondLinkAddress, 0x8000_0000u);
            var callDwords = WriteJump(
                ctx,
                memory,
                FirstLinkAddress,
                mode: 0,
                SecondLinkAddress,
                targetDwords: 1);
            var waitDwords = WriteUnsatisfiedWait(
                ctx,
                memory,
                FirstLinkAddress + (callDwords * sizeof(uint)));

            SubmitDcb(ctx, memory, FirstLinkAddress, callDwords + waitDwords);

            Assert.NotEqual(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                WaitEqueue(ctx, memory, equeue));
        }
        finally
        {
            DeleteEqueue(ctx, equeue);
        }
    }

    [Fact]
    public void SubmittedDcb_IndirectCallRestoresParentRingBase()
    {
        GpuWaitRegistry.Clear();
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        try
        {
            WriteUInt32(memory, SecondLinkAddress, 0x8000_0000u);
            var continuation = FirstLinkAddress + 0x10000;
            _ = WriteUnsatisfiedWait(ctx, memory, continuation);
            var callDwords = WriteJump(
                ctx,
                memory,
                FirstLinkAddress,
                mode: 0,
                SecondLinkAddress,
                targetDwords: 1);
            var sentinelDwords = WriteChain(
                ctx,
                memory,
                FirstLinkAddress + (callDwords * sizeof(uint)),
                target: 1,
                targetDwords: 0);

            SubmitDcb(
                ctx,
                memory,
                FirstLinkAddress,
                callDwords + sentinelDwords);

            Assert.NotNull(GpuWaitRegistry.SnapshotAt(memory, WaitLabelAddress));
        }
        finally
        {
            GpuWaitRegistry.Clear();
        }
    }

    // A chain whose target is never satisfied must not be mistaken for a completed
    // submission: without the redirect the empty first link completes immediately.
    [Fact]
    public void SubmittedDcb_WithoutAChain_CompletesImmediately()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        try
        {
            RegisterGraphicsCompletion(equeue);

            var paddingDwords = WriteChain(ctx, memory, FirstLinkAddress, target: 0, targetDwords: 0);
            SubmitDcb(ctx, memory, FirstLinkAddress, paddingDwords);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                WaitEqueue(ctx, memory, equeue));
            Assert.Equal(GraphicsCompletionIdent, ReadUInt64(memory, EventsAddress + 0x00));
        }
        finally
        {
            DeleteEqueue(ctx, equeue);
        }
    }

    private static void RegisterGraphicsCompletion(ulong equeue) =>
        Assert.True(KernelEventQueueCompatExports.RegisterEvent(
            equeue,
            GraphicsCompletionIdent,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData: 0));

    private static uint WriteChain(
        CpuContext ctx,
        FakeCpuMemory memory,
        ulong linkAddress,
        ulong target,
        uint targetDwords)
        => WriteJump(ctx, memory, linkAddress, 1, target, targetDwords);

    private static uint WriteJump(
        CpuContext ctx,
        FakeCpuMemory memory,
        ulong linkAddress,
        uint mode,
        ulong target,
        uint targetDwords)
    {
        PointCommandBufferAt(memory, linkAddress);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = mode;
        ctx[CpuRegister.Rdx] = 0; // LRU
        ctx[CpuRegister.Rcx] = target;
        ctx[CpuRegister.R8] = targetDwords;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbJump(ctx));
        Assert.Equal(linkAddress, ctx[CpuRegister.Rax]);
        return 4;
    }

    private static uint WriteUnsatisfiedWait(CpuContext ctx, FakeCpuMemory memory, ulong linkAddress)
    {
        PointCommandBufferAt(memory, linkAddress);
        WriteUInt32(memory, WaitLabelAddress, 0);
        ctx[CpuRegister.Rsp] = StackAddress;
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0;      // 32-bit compare
        ctx[CpuRegister.Rdx] = 3;      // equal
        ctx[CpuRegister.Rcx] = 0;      // ME wait
        ctx[CpuRegister.R8] = 2;
        ctx[CpuRegister.R9] = WaitLabelAddress;
        WriteUInt64(memory, StackAddress + 8, 1);           // reference the label never reaches
        WriteUInt64(memory, StackAddress + 16, 0xFFFF_FFFF); // mask
        WriteUInt32(memory, StackAddress + 24, 0x10);        // poll interval
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbWaitRegMem(ctx));
        return 7;
    }

    private static void PointCommandBufferAt(FakeCpuMemory memory, ulong linkAddress)
    {
        WriteUInt64(memory, CommandBufferAddress + 0x10, linkAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, linkAddress + 0x400);
    }

    private static void SubmitDcb(
        CpuContext ctx,
        FakeCpuMemory memory,
        ulong commandAddress,
        uint dwordCount)
    {
        WriteUInt64(memory, SubmitPacketAddress, commandAddress);
        WriteUInt32(memory, SubmitPacketAddress + 8, dwordCount);
        ctx[CpuRegister.Rdi] = SubmitPacketAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverSubmitDcb(ctx));
    }

    private static ulong CreateEqueue(CpuContext ctx, FakeCpuMemory memory)
    {
        ctx[CpuRegister.Rdi] = HandleOutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        return ReadUInt64(memory, HandleOutAddress);
    }

    private static void DeleteEqueue(CpuContext ctx, ulong equeue)
    {
        ctx[CpuRegister.Rdi] = equeue;
        _ = KernelEventQueueCompatExports.KernelDeleteEqueue(ctx);
    }

    private static int WaitEqueue(CpuContext ctx, FakeCpuMemory memory, ulong equeue)
    {
        WriteUInt64(memory, TimeoutAddress, 0);
        ctx[CpuRegister.Rdi] = equeue;
        ctx[CpuRegister.Rsi] = EventsAddress;
        ctx[CpuRegister.Rdx] = 4;
        ctx[CpuRegister.Rcx] = OutCountAddress;
        ctx[CpuRegister.R8] = TimeoutAddress;
        return KernelEventQueueCompatExports.KernelWaitEqueue(ctx);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

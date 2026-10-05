// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelAioWaitTests
{
    private const ulong MemoryBase = 0x10000;
    private const ulong RequestAddress = MemoryBase + 0x100;
    private const ulong IdAddress = MemoryBase + 0x200;
    private const ulong StateAddress = MemoryBase + 0x300;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaitSingleReportsSubmittedCompletionWithoutChangingTimeout(bool withTimeout)
    {
        var context = CreateSubmission(out var submitId);
        try
        {
            context[CpuRegister.Rdi] = submitId;
            context[CpuRegister.Rsi] = StateAddress;
            context[CpuRegister.Rdx] = withTimeout ? StateAddress + 8 : 0;
            Assert.True(context.TryWriteUInt64(StateAddress + 8, 12345));
            Assert.Equal(0, KernelMemoryCompatExports.KernelAioWaitRequest(context));
            Assert.Equal(3u, ReadState(context, StateAddress));
            Assert.Equal(12345u, ReadState(context, StateAddress + 8));
        }
        finally { Delete(context, submitId); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrayCompletionUsesCountThenStatePointer(bool wait)
    {
        var context = CreateSubmission(out var submitId);
        try
        {
            context[CpuRegister.Rdi] = IdAddress;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = StateAddress;
            context[CpuRegister.Rcx] = 1;
            var result = wait ? KernelMemoryCompatExports.KernelAioWaitRequests(context)
                : KernelMemoryCompatExports.KernelAioPollRequests(context);
            Assert.Equal(0, result);
            Assert.Equal(3u, ReadState(context, StateAddress));
        }
        finally { Delete(context, submitId); }
    }

    [Fact]
    public void WaitRejectsDeletedRequestWithoutWritingState()
    {
        var context = CreateSubmission(out var submitId);
        Delete(context, submitId);
        context[CpuRegister.Rdi] = submitId;
        context[CpuRegister.Rsi] = StateAddress;
        Assert.NotEqual(0, KernelMemoryCompatExports.KernelAioWaitRequest(context));
        Assert.Equal(0u, ReadState(context, StateAddress));
    }

    [Fact]
    public void WaitRejectsUnmappedState()
    {
        var context = CreateSubmission(out var submitId);
        try
        {
            context[CpuRegister.Rdi] = submitId;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                KernelMemoryCompatExports.KernelAioWaitRequest(context));
        }
        finally { Delete(context, submitId); }
    }

    private static CpuContext CreateSubmission(out uint submitId)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = RequestAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = IdAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAioSubmitReadCommands(context));
        submitId = ReadState(context, IdAddress);
        return context;
    }

    private static uint ReadState(CpuContext context, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(context.Memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static void Delete(CpuContext context, uint submitId)
    {
        context[CpuRegister.Rdi] = submitId;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAioDeleteRequest(context));
    }
}

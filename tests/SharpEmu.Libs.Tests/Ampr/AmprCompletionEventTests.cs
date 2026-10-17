// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

public sealed class AmprCompletionEventTests
{
    [Fact]
    public void EventFiltersUseTheRequiredAbiValues()
    {
        Assert.Equal(-25, KernelEventQueueCompatExports.KernelEventFilterAmpr);
        Assert.Equal(-30, KernelEventQueueCompatExports.KernelEventFilterAmprSystem);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CompletionUsesAmprRegistrationAndPreservesPayload(bool completionCommand, bool userRegistrationOnly)
    {
        const ulong memoryBase = 0x100000000;
        const ulong commandBuffer = memoryBase + 0x100;
        const ulong eventIdentifier = 29954;
        const ulong eventData = 0x10000000000003E8;
        const ulong registeredUserData = 0x12345678;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase;
        Assert.Equal(0, KernelEventQueueCompatExports.KernelCreateEqueue(context));
        Assert.True(context.TryReadUInt64(memoryBase, out var queue));
        try
        {
            context[CpuRegister.Rdi] = queue;
            context[CpuRegister.Rsi] = eventIdentifier;
            Assert.Equal(0, KernelEventQueueCompatExports.KernelAddUserEvent(context));
            if (!userRegistrationOnly)
            {
                context[CpuRegister.Rdx] = registeredUserData;
                Assert.Equal(0, KernelEventQueueCompatExports.KernelAddAmprEvent(context));
            }

            context[CpuRegister.Rdi] = commandBuffer;
            context[CpuRegister.Rsi] = memoryBase + 0x200;
            context[CpuRegister.Rdx] = 0x100;
            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));
            context[CpuRegister.Rsi] = queue;
            context[CpuRegister.Rdx] = eventIdentifier;
            context[CpuRegister.Rcx] = eventData;
            Assert.Equal(0, completionCommand
                ? AmprExports.CommandBufferWriteKernelEventQueueOnCompletion(context)
                : AmprExports.CommandBufferWriteKernelEventQueue0400(context));
            Assert.Equal(0, AmprExports.CompleteCommandBuffer(context, commandBuffer, out var result, out var errorOffset));
            Assert.Equal(userRegistrationOnly ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND : 0, result);
            Assert.Equal(0u, errorOffset);

            context[CpuRegister.Rdi] = queue;
            context[CpuRegister.Rsi] = memoryBase + 0x400;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = memoryBase + 0x500;
            context[CpuRegister.R8] = memoryBase + 0x600;
            var waitResult = KernelEventQueueCompatExports.KernelWaitEqueue(context);
            if (userRegistrationOnly)
            {
                Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, waitResult);
                return;
            }

            Assert.Equal(0, waitResult);
            Span<byte> eventBytes = stackalloc byte[32];
            Assert.True(memory.TryRead(memoryBase + 0x400, eventBytes));
            Assert.Equal(eventIdentifier, BinaryPrimitives.ReadUInt64LittleEndian(eventBytes));
            Assert.Equal((short)-25,
                BinaryPrimitives.ReadInt16LittleEndian(eventBytes[8..]));
            Assert.Equal(eventData, BinaryPrimitives.ReadUInt64LittleEndian(eventBytes[16..]));
            Assert.Equal(registeredUserData, BinaryPrimitives.ReadUInt64LittleEndian(eventBytes[24..]));
        }
        finally
        {
            context[CpuRegister.Rdi] = queue;
            Assert.Equal(0, KernelEventQueueCompatExports.KernelDeleteEqueue(context));
        }
    }
}

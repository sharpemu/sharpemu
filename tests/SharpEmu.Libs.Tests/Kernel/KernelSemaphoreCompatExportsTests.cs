// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSemaphoreCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong SemaphoreAddress = MemoryBase + 0x100;
    private const ulong NameAddress = MemoryBase + 0x200;

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(0, 1, -1)]
    [InlineData(1, int.MaxValue, int.MaxValue)]
    public void SignalRejectsInvalidCountWithKernelEinvalAndPreservesTokens(int initial, int maximum, int count)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var handle = CreateSemaphore(context, initial, maximum);
        try
        {
            var result = KernelSemaphoreCompatExports.KernelSignalSema(context, handle, count);
            Assert.Equal(unchecked((int)0x80020016), result);
            Assert.Equal(unchecked((ulong)result), context[CpuRegister.Rax]);
            Assert.Equal(initial == 0 ? unchecked((int)0x80020010) : 0,
                KernelSemaphoreCompatExports.KernelPollSema(context, handle, 1));
            Assert.Equal(unchecked((int)0x80020010),
                KernelSemaphoreCompatExports.KernelPollSema(context, handle, 1));
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelSignalSema(context, handle, 1));
        }
        finally
        {
            context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
        }
    }

    [Theory]
    [InlineData("wait")]
    [InlineData("poll")]
    [InlineData("signal")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public void DeletedSemaphoreReturnsKernelEsrch(string operation)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var handle = CreateSemaphore(context, 0, 1);
        context[CpuRegister.Rdi] = handle;
        Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0;

        var result = operation switch
        {
            "wait" => KernelSemaphoreCompatExports.KernelWaitSema(context),
            "poll" => KernelSemaphoreCompatExports.KernelPollSema(context, handle, 1),
            "signal" => KernelSemaphoreCompatExports.KernelSignalSema(context, handle, 1),
            "cancel" => KernelSemaphoreCompatExports.KernelCancelSema(context, handle, 0, 0),
            _ => KernelSemaphoreCompatExports.KernelDeleteSema(context),
        };
        Assert.Equal(unchecked((int)0x80020003), result);
        Assert.Equal(unchecked((ulong)result), context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData("wait")]
    [InlineData("poll")]
    [InlineData("cancel")]
    public void InvalidCountReturnsKernelEinval(string operation)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var handle = CreateSemaphore(context, 0, 1);
        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 2;
            var result = operation switch
            {
                "wait" => KernelSemaphoreCompatExports.KernelWaitSema(context),
                "poll" => KernelSemaphoreCompatExports.KernelPollSema(context, handle, 2),
                _ => KernelSemaphoreCompatExports.KernelCancelSema(context, handle, 2, 0),
            };
            Assert.Equal(unchecked((int)0x80020016), result);
        }
        finally
        {
            context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
        }
    }

    [Fact]
    public void CreateRejectsInvalidAttributesWithKernelEinval()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.R8] = 1;

        Assert.Equal(unchecked((int)0x80020016), KernelSemaphoreCompatExports.KernelCreateSema(context));
    }

    [Fact]
    public void UnmappedTimeoutReturnsKernelEfault()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var handle = CreateSemaphore(context, 0, 1);
        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = MemoryBase + 0x2000;
            Assert.Equal(unchecked((int)0x8002000E), KernelSemaphoreCompatExports.KernelWaitSema(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
        }
    }

    [Fact]
    public void KernelCreateSemaWritesPointerSizedHandleAndClearsUpperBits()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        Assert.True(context.TryWriteUInt64(SemaphoreAddress, 0xC0DE_C0DE_CAFE_BA00));
        Assert.True(context.Memory.TryWrite(NameAddress, "unity-worker\0"u8));

        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 1;
        context[CpuRegister.R9] = 0;

        Assert.Equal(0, KernelSemaphoreCompatExports.KernelCreateSema(context));
        Assert.True(context.TryReadUInt64(SemaphoreAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        Assert.Equal(0UL, handle >> 32);

        context[CpuRegister.Rdi] = handle;
        Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
    }

    private static uint CreateSemaphore(CpuContext context, int initial, int maximum)
    {
        Assert.True(context.Memory.TryWrite(NameAddress, "test-semaphore\0"u8));
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = (ulong)initial;
        context[CpuRegister.R8] = (ulong)maximum;
        context[CpuRegister.R9] = 0;
        Assert.Equal(0, KernelSemaphoreCompatExports.KernelCreateSema(context));
        Assert.True(context.TryReadUInt32(SemaphoreAddress, out var handle));
        return handle;
    }
}

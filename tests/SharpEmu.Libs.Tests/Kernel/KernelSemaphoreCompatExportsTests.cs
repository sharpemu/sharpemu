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
}

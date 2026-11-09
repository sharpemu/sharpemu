// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelOperationModeTests
{
    private const ulong BaseAddress = 0x1_0000;
    private const ulong ModeAddress = BaseAddress + 0x10;
    private const ulong SubmodeAddress = BaseAddress + 0x20;

    private static uint Read(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [Fact]
    public void ReportsThePs5BaseConsoleWithNoSubmode()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = ModeAddress;
        ctx[CpuRegister.Rsi] = SubmodeAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelExports.KernelGetOperationMode(ctx));
        Assert.Equal(2u, Read(memory, ModeAddress));
        Assert.Equal(0u, Read(memory, SubmodeAddress));
    }

    [Fact]
    public void NullOutputPointersAreSkipped()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelExports.KernelGetOperationMode(ctx));
    }

    [Fact]
    public void UnmappedOutputPointerFaults()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0xDEAD_0000;
        ctx[CpuRegister.Rsi] = SubmodeAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, KernelExports.KernelGetOperationMode(ctx));
    }
}

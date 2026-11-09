// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelWriteThrottlingTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong StatusAddress = BaseAddress + 0x100;

    [Fact]
    public void Status_WritesBandwidthAndClearsReservedFields()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Span<byte> initial = stackalloc byte[KernelWriteThrottlingCompatExports.StatusSize + 1];
        initial.Fill(0xA5);
        Assert.True(memory.TryWrite(StatusAddress, initial));

        ctx[CpuRegister.Rdi] = StatusAddress;
        var result = KernelWriteThrottlingCompatExports.KernelWriteThrottlingStatus(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Span<byte> status = stackalloc byte[KernelWriteThrottlingCompatExports.StatusSize + 1];
        Assert.True(memory.TryRead(StatusAddress, status));
        Assert.Equal(
            KernelWriteThrottlingCompatExports.WriteBandwidth64KiBUnits,
            BinaryPrimitives.ReadUInt64LittleEndian(status));
        Assert.All(status[8..KernelWriteThrottlingCompatExports.StatusSize].ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(0xA5, status[KernelWriteThrottlingCompatExports.StatusSize]);
    }

    [Fact]
    public void Status_RejectsNullAddress()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;

        var result = KernelWriteThrottlingCompatExports.KernelWriteThrottlingStatus(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Status_RejectsUnmappedAddress()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress + 0xFF0;

        var result = KernelWriteThrottlingCompatExports.KernelWriteThrottlingStatus(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }
}

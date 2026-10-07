// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcInitTests
{
    private const ulong BaseAddress = 0x1_0000_0000;

    [Fact]
    public void Init_Version12_WritesGraphicsState()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var stateAddress = BaseAddress + 0x100;
        ctx[CpuRegister.Rdi] = stateAddress;
        ctx[CpuRegister.Rsi] = 12;

        var result = AgcExports.Init(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Span<byte> state = stackalloc byte[8];
        Assert.True(memory.TryRead(stateAddress, state));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(state));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(state[4..]));
    }

    [Fact]
    public void Init_InvalidStateAddress_ReturnsMemoryFault()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress + 0x2000;
        ctx[CpuRegister.Rsi] = 12;

        var result = AgcExports.Init(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Fact]
    public void Init_UnsupportedVersion_DoesNotWriteState()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var stateAddress = BaseAddress + 0x100;
        ctx[CpuRegister.Rdi] = stateAddress;
        ctx[CpuRegister.Rsi] = 14;

        var result = AgcExports.Init(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Span<byte> state = stackalloc byte[8];
        Assert.True(memory.TryRead(stateAddress, state));
        Assert.True(state.SequenceEqual(stackalloc byte[8]));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcPrimStateHullVariantTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CxRegistersAddress = BaseAddress + 0x100;
    private const ulong UcRegistersAddress = BaseAddress + 0x200;
    private const ulong HullStateAddress = BaseAddress + 0x300;
    private const ulong GeometryShaderAddress = BaseAddress + 0x400;
    private const ulong SpecialsAddress = BaseAddress + 0x500;

    // Tessellation pipelines pass a non-null hull-state block; the
    // geometry-derived register writes must still happen instead of an
    // INVALID_ARGUMENT that leaves the caller's register storage as garbage.
    [Fact]
    public void CreatePrimState_AcceptsHullStateBlock()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        memory.TryWrite(GeometryShaderAddress + 0x5A, new byte[] { 2 });
        WriteUInt64(memory, GeometryShaderAddress + 0x28, SpecialsAddress);

        // Specials: {register, value} pairs at GeCntl 0x00, StagesEn 0x08,
        // GsOutPrimType 0x20, GeUserVgprEn 0x28.
        WriteUInt64(memory, SpecialsAddress + 0x00, 0x0000_0111_0000_0222UL);
        WriteUInt64(memory, SpecialsAddress + 0x08, 0x0000_0333_0000_0444UL);
        WriteUInt64(memory, SpecialsAddress + 0x20, 0x0000_0555_0000_0666UL);
        WriteUInt64(memory, SpecialsAddress + 0x28, 0x0000_0777_0000_0888UL);

        ctx[CpuRegister.Rdi] = CxRegistersAddress;
        ctx[CpuRegister.Rsi] = UcRegistersAddress;
        ctx[CpuRegister.Rdx] = HullStateAddress;
        ctx[CpuRegister.Rcx] = GeometryShaderAddress;
        ctx[CpuRegister.R8] = 0x11;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.CreatePrimState(ctx));

        Assert.NotEqual(0u, ReadUInt32(memory, CxRegistersAddress));
        Assert.Equal(0x11u, ReadUInt32(memory, UcRegistersAddress + 20));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcHullShaderCreateTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong HeaderAddress = BaseAddress + 0x100;
    private const ulong ShRegistersAddress = BaseAddress + 0x200;
    private const ulong CodeAddress = BaseAddress + 0x4300;

    // Hull/tessellation stage: header type 5 with the 0x10A/0x10B program
    // register slot observed in Ghost of Yōtei; rejecting it aborts the
    // title's tessellation pipeline construction mid-way.
    [Fact]
    public void CreateShader_AcceptsHullShaderTypeAndPatchesProgramAddress()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x8000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        WriteUInt32(memory, HeaderAddress + 0x00, 0x34333231); // file header
        WriteUInt32(memory, HeaderAddress + 0x04, 0x18);       // version
        // Self-relative pointer to the SH register table; other tables absent.
        WriteUInt64(memory, HeaderAddress + 0x20, ShRegistersAddress - (HeaderAddress + 0x20));
        memory.TryWrite(HeaderAddress + 0x5A, new byte[] { 5 });
        memory.TryWrite(HeaderAddress + 0x5C, new byte[] { 2 });

        WriteUInt32(memory, ShRegistersAddress + 0x0, 0x10A);
        WriteUInt32(memory, ShRegistersAddress + 0x8, 0x10B);

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = HeaderAddress;
        ctx[CpuRegister.Rdx] = CodeAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.CreateShader(ctx));

        Assert.Equal(
            (uint)((CodeAddress >> 8) & 0xFFFF_FFFF),
            ReadUInt32(memory, ShRegistersAddress + 0x4));
        Assert.Equal(
            (uint)((CodeAddress >> 40) & 0xFF),
            ReadUInt32(memory, ShRegistersAddress + 0xC));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
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

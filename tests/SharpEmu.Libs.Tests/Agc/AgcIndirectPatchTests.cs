// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndirectPatchTests
{
    private const ulong BaseAddress = 0x2_2000_0000;
    private const ulong CommandAddress = BaseAddress + 0x100;
    private const uint SetContextRegIndirectHeader = 0xC003_9F00u;

    private const uint ItNop = 0x10;
    private const uint ItSetShRegIndirect = 0x63;
    private const uint ItSetUconfigRegIndirect = 0x64;
    private const uint ItSetContextRegIndirect = 0x9F;
    private const uint RShRegsIndirect = 0x11;
    private const uint RCxRegsIndirect = 0x12;
    private const uint RUcRegsIndirect = 0x13;

    // The three register spaces crossed with the two packet layouts every
    // patcher accepts. Each patcher derives its field offsets from the packet
    // header alone, so this is the whole matrix of shapes they must get right -
    // and the net that holds the guest-visible bytes fixed while the header
    // handling underneath changes.
    //
    // Modern: SET_{CONTEXT,SH,UCONFIG}_REG_INDIRECT, 5 dwords, address at +4,
    // count at +16 masked to 14 bits.
    // Legacy: NOP carrying the space in its sub-register, 4 dwords, count at +4
    // over the whole dword, address at +8.
    public static TheoryData<string, uint, uint, bool> Layouts() => new()
    {
        { "cx", ItSetContextRegIndirect, 0, true },
        { "sh", ItSetShRegIndirect, 0, true },
        { "uc", ItSetUconfigRegIndirect, 0, true },
        { "cx", ItNop, RCxRegsIndirect, false },
        { "sh", ItNop, RShRegsIndirect, false },
        { "uc", ItNop, RUcRegsIndirect, false },
    };

    private static uint Pm4Header(uint dwords, uint opcode, uint register = 0) =>
        0xC000_0000u | ((dwords - 2) << 16) | (opcode << 8) | ((register & 0x3Fu) << 2);

    private static Func<CpuContext, int> AddRegisters(string space) => space switch
    {
        "cx" => AgcExports.SetCxRegIndirectPatchAddRegisters,
        "sh" => AgcExports.SetShRegIndirectPatchAddRegisters,
        _ => AgcExports.SetUcRegIndirectPatchAddRegisters,
    };

    private static Func<CpuContext, int> SetAddress(string space) => space switch
    {
        "cx" => AgcExports.SetCxRegIndirectPatchSetAddress,
        "sh" => AgcExports.SetShRegIndirectPatchSetAddress,
        _ => AgcExports.SetUcRegIndirectPatchSetAddress,
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void AddRegisters_AccumulatesIntoTheLayoutsCountField(
        string space,
        uint opcode,
        uint register,
        bool modern)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var header = modern ? Pm4Header(5, opcode) : Pm4Header(4, opcode, register);
        var countOffset = modern ? 16UL : 4UL;
        WriteUInt32(memory, CommandAddress, header);
        // Bits outside the count field must survive: the modern layout masks the
        // count to 14 bits, the legacy one owns the whole dword.
        WriteUInt32(memory, CommandAddress + countOffset, modern ? 0xCAFE_0005u : 5u);

        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 11;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AddRegisters(space)(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(
            modern ? 0xCAFE_0010u : 16u,
            ReadUInt32(memory, CommandAddress + countOffset));
        Assert.Equal(header, ReadUInt32(memory, CommandAddress));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void SetAddress_WritesTheLayoutsAddressFieldAndKeepsItsLowBits(
        string space,
        uint opcode,
        uint register,
        bool modern)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var header = modern ? Pm4Header(5, opcode) : Pm4Header(4, opcode, register);
        var addressOffset = modern ? 4UL : 8UL;
        WriteUInt32(memory, CommandAddress, header);
        WriteUInt32(memory, CommandAddress + addressOffset, 3u);
        WriteUInt32(memory, CommandAddress + addressOffset + 4, 0u);

        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x0000_0002_3456_789CUL;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, SetAddress(space)(ctx));
        Assert.Equal(
            0x0000_0002_3456_789FUL,
            ReadUInt64(memory, CommandAddress + addressOffset));
    }

    // The modern count field is 14 bits, so accumulation wraps inside it and
    // leaves the dword's upper bits alone.
    [Fact]
    public void AddRegisters_WrapsWithinTheModernCountMask()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 16, 0xDEAD_3FFFu);

        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 2;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetCxRegIndirectPatchAddRegisters(ctx));
        Assert.Equal(0xDEAD_0001u, ReadUInt32(memory, CommandAddress + 16));
    }

    // A packet belonging to another register space must be refused, not patched
    // at the offsets this space would have used.
    [Fact]
    public void AddRegisters_RejectsAPacketFromADifferentRegisterSpace()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, Pm4Header(5, ItSetShRegIndirect));
        WriteUInt32(memory, CommandAddress + 16, 4u);

        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.SetCxRegIndirectPatchAddRegisters(ctx));
        Assert.Equal(4u, ReadUInt32(memory, CommandAddress + 16));
    }

    // Right opcode, wrong length: neither layout matches, so nothing is written.
    [Fact]
    public void AddRegisters_RejectsAnUnexpectedPacketLength()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, Pm4Header(7, ItSetContextRegIndirect));
        WriteUInt32(memory, CommandAddress + 16, 4u);

        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.SetCxRegIndirectPatchAddRegisters(ctx));
        Assert.Equal(4u, ReadUInt32(memory, CommandAddress + 16));
    }

    // An unreadable header is a memory fault; a readable one that matches no
    // layout is a bad argument. Reading the header once instead of three times
    // must keep those two outcomes distinguishable.
    [Theory]
    [MemberData(nameof(Layouts))]
    public void Patchers_SeparateMemoryFaultFromInvalidArgument(
        string space,
        uint opcode,
        uint register,
        bool modern)
    {
        _ = opcode;
        _ = register;
        _ = modern;
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rsi] = 1;

        foreach (var patch in new[] { AddRegisters(space), SetAddress(space) })
        {
            ctx[CpuRegister.Rdi] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                patch(ctx));

            ctx[CpuRegister.Rdi] = BaseAddress + 0x1000;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                patch(ctx));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetRegisterCount_ReplacesOnlyTheCountField(bool modern)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var header = modern
            ? Pm4Header(5, ItSetContextRegIndirect)
            : Pm4Header(4, ItNop, RCxRegsIndirect);
        var countOffset = modern ? 16UL : 4UL;
        WriteUInt32(memory, CommandAddress, header);
        WriteUInt32(memory, CommandAddress + countOffset, modern ? 0xCAFE_3FFFu : 0xFFFF_FFFFu);

        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x21;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
        Assert.Equal(
            modern ? 0xCAFE_0021u : 0x21u,
            ReadUInt32(memory, CommandAddress + countOffset));
    }

    [Fact]
    public void SetCxRegisterCount_ReplacesCountAndPreservesOtherFields()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt64(memory, CommandAddress + 4, 0x5566_7788_99AA_BBCC);
        WriteUInt32(memory, CommandAddress + 12, 0x8000_0000u);
        WriteUInt32(memory, CommandAddress + 16, 0xCAFE_0003u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x4C;

        var result = AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(SetContextRegIndirectHeader, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x5566_7788_99AA_BBCCUL, ReadUInt64(memory, CommandAddress + 4));
        Assert.Equal(0x8000_0000u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0xCAFE_004Cu, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void SetCxRegisterCount_AllowsZeroAndAddRegistersUsesNewCount()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 16, 9);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
        Assert.Equal(0u, ReadUInt32(memory, CommandAddress + 16));

        ctx[CpuRegister.Rsi] = 7;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetCxRegIndirectPatchAddRegisters(ctx));
        Assert.Equal(7u, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void SetCxAddress_PatchesNativeAddressAndPreservesLowControlBits()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 4, 3u);
        WriteUInt32(memory, CommandAddress + 8, 0u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x0000_0002_3456_789CUL;

        var result = AgcExports.SetCxRegIndirectPatchSetAddress(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x0000_0002_3456_789FUL, ReadUInt64(memory, CommandAddress + 4));
    }

    [Fact]
    public void SetCxRegisterCount_RejectsNullCommandAddress()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 4;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
    }

    [Fact]
    public void SetCxRegisterCount_ReturnsMemoryFaultForInvalidGuestMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress + 0x1000;
        ctx[CpuRegister.Rsi] = 4;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

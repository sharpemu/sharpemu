// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcShaderProgramRegisterTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong DestinationAddress = MemoryBase + 0x80;
    private const ulong HeaderAddress = MemoryBase + 0x100;
    private const ulong RegistersAddress = MemoryBase + 0x400;
    private const ulong CodeAddress = 0x0000_1234_5678_9A00;

    [Fact]
    public void CreateHullShaderPreservesResourceRegistersWithoutProgramPair()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        ConfigureShaderHeader(
            memory,
            shaderType: 5,
            (0x10A, 0xA5A5_A5A5),
            (0x10B, 0x5A5A_5A5A));
        var context = CreateContext(memory, DestinationAddress);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.CreateShader(context));
        Assert.Equal(HeaderAddress, ReadUInt64(memory, DestinationAddress));
        Assert.Equal(CodeAddress, ReadUInt64(memory, HeaderAddress + 0x10));
        Assert.Equal(0xA5A5_A5A5u, ReadUInt32(memory, RegistersAddress + 4));
        Assert.Equal(0x5A5A_5A5Au, ReadUInt32(memory, RegistersAddress + 12));
    }

    [Fact]
    public void CreateHullShaderPatchesOnlyActualProgramRegisterPair()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        ConfigureShaderHeader(
            memory,
            shaderType: 5,
            (0x10A, 0xA5A5_A5A5),
            (0x10B, 0x5A5A_5A5A),
            (0x108, 0),
            (0x109, 0));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.CreateShader(CreateContext(memory, DestinationAddress)));
        AssertProgramPairPatchedAfterPreservedResources(memory);
    }

    [Fact]
    public void CreateGeometryShaderPatchesProgramRegistersInsteadOfResourceRegisters()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        ConfigureShaderHeader(
            memory,
            shaderType: 4,
            (0x8A, 0xA5A5_A5A5),
            (0x8B, 0x5A5A_5A5A),
            (0x88, 0),
            (0x89, 0));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.CreateShader(CreateContext(memory, destinationAddress: 0)));
        AssertProgramPairPatchedAfterPreservedResources(memory);
    }

    [Fact]
    public void CreateShaderReturnsMemoryFaultForUnreadableRegisterArray()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x410);
        ConfigureShaderHeader(
            memory,
            shaderType: 5,
            (0x10A, 0xA5A5_A5A5),
            (0x10B, 0x5A5A_5A5A));
        WriteByte(memory, HeaderAddress + 0x5C, 3);
        WriteUInt64(memory, DestinationAddress, 0xDEAD_BEEF_DEAD_BEEF);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.CreateShader(CreateContext(memory, DestinationAddress)));
        Assert.Equal(0xDEAD_BEEF_DEAD_BEEFul, ReadUInt64(memory, DestinationAddress));
    }

    private static CpuContext CreateContext(FakeCpuMemory memory, ulong destinationAddress)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = HeaderAddress;
        context[CpuRegister.Rdx] = CodeAddress;
        return context;
    }

    private static void ConfigureShaderHeader(
        FakeCpuMemory memory,
        byte shaderType,
        params (uint Register, uint Value)[] registers)
    {
        WriteUInt32(memory, HeaderAddress, 0x3433_3231);
        WriteUInt32(memory, HeaderAddress + 4, 0x18);
        WriteUInt64(memory, HeaderAddress + 0x20, RegistersAddress - (HeaderAddress + 0x20));
        WriteByte(memory, HeaderAddress + 0x5A, shaderType);
        WriteByte(memory, HeaderAddress + 0x5C, checked((byte)registers.Length));
        for (var index = 0; index < registers.Length; index++)
        {
            var entryAddress = RegistersAddress + ((ulong)index * 8);
            WriteUInt32(memory, entryAddress, registers[index].Register);
            WriteUInt32(memory, entryAddress + 4, registers[index].Value);
        }
    }

    private static void AssertProgramPairPatchedAfterPreservedResources(FakeCpuMemory memory)
    {
        Assert.Equal(0xA5A5_A5A5u, ReadUInt32(memory, RegistersAddress + 4));
        Assert.Equal(0x5A5A_5A5Au, ReadUInt32(memory, RegistersAddress + 12));
        Assert.Equal(0x3456_789Au, ReadUInt32(memory, RegistersAddress + 20));
        Assert.Equal(0x12u, ReadUInt32(memory, RegistersAddress + 28));
    }

    private static void WriteByte(FakeCpuMemory memory, ulong address, byte value) =>
        Assert.True(memory.TryWrite(address, [value]));

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
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}

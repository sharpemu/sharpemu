// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpTrophy2ExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StackAddress = MemoryBase + 0x100;
    private const ulong OutCountAddress = MemoryBase + 0x200;
    private const ulong DataAddress = MemoryBase + 0x300;

    [Fact]
    public void GetTrophyInfoArray_ReadsOutCountPointerFromStackAndInitializesIt()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rsp] = StackAddress,
            [CpuRegister.Rcx] = 0,
            [CpuRegister.R8] = 0,
            [CpuRegister.R9] = 0,
        };

        WriteUInt64(memory, StackAddress + sizeof(ulong), OutCountAddress);
        WriteUInt32(memory, OutCountAddress, 0xDEADBEEF);

        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(context));
        Assert.Equal(0u, ReadUInt32(memory, OutCountAddress));
    }

    [Fact]
    public void GetTrophyInfoArray_WritesDeterministicDataRecordsAndCount()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rsp] = StackAddress,
            [CpuRegister.Rcx] = 2,
            [CpuRegister.R8] = 0,
            [CpuRegister.R9] = DataAddress,
        };

        WriteUInt64(memory, StackAddress + sizeof(ulong), OutCountAddress);
        WriteUInt32(memory, OutCountAddress, 0xDEADBEEF);

        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(context));
        Assert.Equal(2u, ReadUInt32(memory, OutCountAddress));
        Assert.Equal(0u, ReadUInt32(memory, DataAddress));
        Assert.Equal(1u, ReadUInt32(memory, DataAddress + 0x20));
        Assert.Equal(0u, ReadUInt32(memory, DataAddress + 0x04));
        Assert.Equal(0u, ReadUInt32(memory, DataAddress + 0x24));
    }

    [Fact]
    public void GetTrophyInfoArray_RejectsCapacityAboveSupportedRange()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rsp] = StackAddress,
            [CpuRegister.Rcx] = 129,
        };

        WriteUInt64(memory, StackAddress + sizeof(ulong), OutCountAddress);
        WriteUInt32(memory, OutCountAddress, 0xDEADBEEF);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(context));
        Assert.Equal(0u, ReadUInt32(memory, OutCountAddress));
    }

    [Fact]
    public void TrophyInfoArrayExport_RegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("y3zHpdZO6ME", out var export));
        Assert.Equal("sceNpTrophy2GetTrophyInfoArray", export.Name);
        Assert.Equal("libSceNpTrophy2", export.LibraryName);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
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
}

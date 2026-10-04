// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcUnityPacketExportsTests
{
    private const ulong BaseAddress = 0x2_4800_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong SourceAddress = BaseAddress + 0x800;
    private const ulong StackAddress = BaseAddress + 0xC00;

    [Theory]
    [InlineData("03RZmELWWzw", "sceAgcCbSetUcRegistersDirect")]
    [InlineData("qzMN2XKGA4k", "sceAgcAcbCopyData")]
    [InlineData("CbQh3DKMSno", "sceAgcAcbCopyDataGetSize")]
    [InlineData("T6xuVw0KUJo", "sceAgcDebugRaiseException")]
    [InlineData("Ikfdt-rIqCE", "sceAgcUnknownIkfdt")]
    public void UnityAgcImports_AreRegisteredInTheAgcLibrary(string nid, string expectedName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(expectedName, export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
    }

    [Fact]
    public void SetUcRegistersDirect_PreservesOrderAndSplitsNonContiguousRuns()
    {
        var memory = CreateMemory();
        WriteUInt32(memory, SourceAddress + 0, 2);
        WriteUInt32(memory, SourceAddress + 4, 0xAAAA_0002);
        WriteUInt32(memory, SourceAddress + 8, 3);
        WriteUInt32(memory, SourceAddress + 12, 0xAAAA_0003);
        WriteUInt32(memory, SourceAddress + 16, 1);
        WriteUInt32(memory, SourceAddress + 20, 0xAAAA_0001);
        var ctx = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = SourceAddress,
            [CpuRegister.Rdx] = 3,
        };

        Assert.Equal(0, AgcExports.CbSetUcRegistersDirect(ctx));

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC002_7900u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(2u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(0xAAAA_0002u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0xAAAA_0003u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(0xC001_7900u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 20));
        Assert.Equal(0xAAAA_0001u, ReadUInt32(memory, PacketAddress + 24));
        Assert.Equal(PacketAddress + 28, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void AcbCopyData_UsesAsyncSelectorEncoding()
    {
        var memory = CreateMemory();
        WriteUInt64(memory, StackAddress + 8, 0x1122_3344_5566_7788);
        WriteUInt64(memory, StackAddress + 16, 1);
        WriteUInt64(memory, StackAddress + 24, 1);
        var ctx = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = 3,
            [CpuRegister.Rdx] = 2,
            [CpuRegister.Rcx] = 0x0000_0002_AABB_CCD0,
            [CpuRegister.R8] = 5,
            [CpuRegister.R9] = 1,
            [CpuRegister.Rsp] = StackAddress,
        };

        Assert.Equal(0, AgcExports.AcbCopyData(ctx));

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC004_4000u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x0411_2305u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0x1122_3344u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(0xAABB_CCD0u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(2u, ReadUInt32(memory, PacketAddress + 20));

        Assert.Equal(24, AgcExports.AcbCopyDataGetSize(ctx));
        Assert.Equal(24UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void IndirectBufferPatch_ReplacesTargetPolicyAndLength()
    {
        var memory = CreateMemory();
        WriteUInt32(memory, PacketAddress, 0xC002_3F00);
        WriteUInt32(memory, PacketAddress + 4, 3);
        WriteUInt32(memory, PacketAddress + 8, 0);
        WriteUInt32(memory, PacketAddress + 12, 0xC5F0_0000);
        var ctx = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = PacketAddress,
            [CpuRegister.Rsi] = 2,
            [CpuRegister.Rdx] = 0x0000_0003_4455_6678,
            [CpuRegister.Rcx] = 0x34567,
        };

        Assert.Equal(0, AgcExports.UnknownIkfdt(ctx));

        Assert.Equal(0x4455_6678u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(3u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0xE5F3_4567u, ReadUInt32(memory, PacketAddress + 12));
    }

    private static FakeCpuMemory CreateMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x400);
        return memory;
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

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcIndirectPacketSizeTests
{
    private const ulong BaseAddress = 0x2_6100_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;

    public static TheoryData<string, string, Func<CpuContext, int>, uint> Packets => new()
    {
        { "GBCh3zCihoU", "sceAgcDcbSetCxRegistersIndirectGetSize", AgcExports.DcbSetCxRegistersIndirect, 20u },
        { "nNlUtdDDvZ0", "sceAgcDcbSetShRegistersIndirectGetSize", AgcExports.DcbSetShRegistersIndirect, 20u },
        { "UQGTw4xRlcM", "sceAgcDcbSetUcRegistersIndirectGetSize", AgcExports.DcbSetUcRegistersIndirect, 20u },
        { "w8HVkEeXPv8", "sceAgcDcbDispatchIndirectGetSize", AgcExports.DcbDispatchIndirect, 12u },
        { "cxPZ4Wgvdj8", "sceAgcDcbDrawIndirectGetSize", AgcExports.DcbDrawIndirect, 20u },
    };

    [Theory]
    [MemberData(nameof(Packets))]
    public void GetSize_ReturnsTheBytesTheWriterConsumes(
        string sizeNid,
        string sizeName,
        Func<CpuContext, int> writer,
        uint expectedBytes)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport(sizeNid, out var export));
        Assert.Equal(sizeName, export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);

        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = BaseAddress + 0x600,
            [CpuRegister.Rdx] = 4,
        };

        Assert.Equal((OrbisGen2Result)expectedBytes, manager.Dispatch(sizeNid, context));
        Assert.Equal(expectedBytes, context[CpuRegister.Rax]);

        Assert.Equal(0, writer(context));
        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + expectedBytes, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}

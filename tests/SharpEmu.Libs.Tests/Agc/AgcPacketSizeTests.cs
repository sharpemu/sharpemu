// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Every GetSize must agree with the packet its writer emits: a title reserves the reported bytes
// and then writes the packet into exactly that space.
[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcPacketSizeTests
{
    private const ulong BaseAddress = 0x2_6200_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong PredicateAddress = BaseAddress + 0x600;

    public static TheoryData<string, string, Func<CpuContext, int>, uint> ControlFlowPackets => new()
    {
        { "0ZOG0jc9nRg", "sceAgcAcbRewindGetSize", AgcExports.AcbRewind, 8u },
        { "ozKzBP4aki4", "sceAgcAcbCondExecGetSize", AgcExports.AcbCondExec, 20u },
        { "ou16V5hh5sg", "sceAgcDcbCondExecGetSize", AgcExports.DcbCondExec, 20u },
    };

    [Theory]
    [MemberData(nameof(ControlFlowPackets))]
    public void GetSize_ReturnsTheBytesItsWriterConsumes(
        string nid,
        string name,
        Func<CpuContext, int> writer,
        uint expectedBytes)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);

        var memory = CreateMemory();
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = PredicateAddress,
            [CpuRegister.Rdx] = 4,
        };

        Assert.Equal((OrbisGen2Result)expectedBytes, manager.Dispatch(nid, context));
        Assert.Equal(expectedBytes, context[CpuRegister.Rax]);

        Assert.Equal(0, writer(context));
        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + expectedBytes, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    public static TheoryData<string, string> AtomicMemPackets => new()
    {
        { "da1Sm8-QDoU", "sceAgcAcbAtomicMemGetSize" },
        { "oz6zQq1JwCE", "sceAgcDcbAtomicMemGetSize" },
    };

    // ATOMIC_MEM is the PM4 header plus the eight dwords AtomicMemoryPacket decodes, on both queues.
    [Theory]
    [MemberData(nameof(AtomicMemPackets))]
    public void AtomicMemGetSize_ReportsTheNineDwordPacket(string nid, string name)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);

        var context = new CpuContext(CreateMemory(), Generation.Gen5);
        Assert.Equal((OrbisGen2Result)(9 * sizeof(uint)), manager.Dispatch(nid, context));
        Assert.Equal(9U * sizeof(uint), context[CpuRegister.Rax]);
    }

    // The event type is the first argument of the size call and the second of the writer.
    [Theory]
    [InlineData(0x38u, 16u)]
    [InlineData(0x39u, 16u)]
    [InlineData(0x07u, 8u)]
    [InlineData(0x3Fu, 8u)]
    public void AcbEventWriteGetSize_AgreesWithTheWriter(uint eventType, uint expectedBytes)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport("Y-5vneiBtzk", out var export));
        Assert.Equal("sceAgcAcbEventWriteGetSize", export.Name);

        var memory = CreateMemory();
        var context = new CpuContext(memory, Generation.Gen5) { [CpuRegister.Rdi] = eventType };
        Assert.Equal((OrbisGen2Result)expectedBytes, manager.Dispatch("Y-5vneiBtzk", context));

        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = eventType;
        context[CpuRegister.Rdx] = PredicateAddress;
        Assert.Equal(0, AgcExports.AcbEventWrite(context));
        Assert.Equal(PacketAddress + expectedBytes, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void AcbEventWriteGetSize_IsZeroForAnInvalidEvent()
    {
        var manager = CreateManager();
        var context = new CpuContext(CreateMemory(), Generation.Gen5) { [CpuRegister.Rdi] = 0x40 };

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch("Y-5vneiBtzk", context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    private static FakeCpuMemory CreateMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        return memory;
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

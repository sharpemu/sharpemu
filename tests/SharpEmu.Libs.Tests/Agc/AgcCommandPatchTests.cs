// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcCommandPatchTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong CommandBufferAddress = MemoryBase + 0x100;
    private const ulong DataAddress = MemoryBase + 0x400;
    private const ulong StackAddress = MemoryBase + 0x800;
    private const ulong PacketAddress = MemoryBase + 0x1000;
    private const ulong RelocatedPacketAddress = MemoryBase + 0x2000;
    private const uint WriteDataHeader = 0xC0031054;
    private const uint IndexBaseHeader = 0xC0012600;
    private const uint IndexBufferSizeHeader = 0xC0001300;

    [Fact]
    public void DcbIndexBinding_EmitsIndependentBaseAndCountPackets()
    {
        var (_, context) = CreateCommandBufferContext();
        const ulong indexAddress = 0x1234_5678_9ABC_DEF0;

        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = indexAddress;
        context[CpuRegister.Rdx] = uint.MaxValue;

        Assert.Equal(0, AgcExports.DcbSetIndexBuffer(context));
        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt32(PacketAddress, out var baseHeader));
        Assert.Equal(IndexBaseHeader, baseHeader);
        Assert.True(context.TryReadUInt64(PacketAddress + sizeof(uint), out var encodedAddress));
        Assert.Equal(indexAddress, encodedAddress);

        const uint indexCount = 50_000;
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = indexCount;

        Assert.Equal(0, AgcExports.DcbSetIndexCount(context));
        Assert.Equal(PacketAddress + (3 * sizeof(uint)), context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt32(PacketAddress + (3 * sizeof(uint)), out var countHeader));
        Assert.Equal(IndexBufferSizeHeader, countHeader);
        Assert.True(context.TryReadUInt32(PacketAddress + (4 * sizeof(uint)), out var encodedCount));
        Assert.Equal(indexCount, encodedCount);
        Assert.True(context.TryReadUInt64(CommandBufferAddress + 0x10, out var cursor));
        Assert.Equal(PacketAddress + (5 * sizeof(uint)), cursor);
    }

    [Fact]
    public void DcbWriteData_ZeroDestinationEmitsPatchablePacket()
    {
        var (memory, context) = CreateWriteDataContext();

        Assert.Equal(0, AgcExports.DcbWriteData(context));
        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt32(PacketAddress, out var header));
        Assert.Equal(WriteDataHeader, header);
        Assert.True(context.TryReadUInt64(PacketAddress + 8, out var initialDestination));
        Assert.Equal(0UL, initialDestination);

        const ulong destination = 0x1234_5678_9ABC_DEF0;
        context[CpuRegister.Rdi] = PacketAddress;
        context[CpuRegister.Rsi] = destination;

        Assert.Equal(0, AgcExports.WriteDataPatchSetAddressOrOffset(context));
        Assert.True(context.TryReadUInt64(PacketAddress + 8, out var patchedDestination));
        Assert.Equal(destination, patchedDestination);
        Assert.True(context.TryReadUInt32(PacketAddress + 16, out var payload));
        Assert.Equal(0xA5A5_5A5Au, payload);
    }

    [Fact]
    public void WriteDataPatch_RelocatedHandlePatchesCopiedPacket()
    {
        var (memory, context) = CreateWriteDataContext();
        Assert.Equal(0, AgcExports.DcbWriteData(context));
        var patchHandle = context[CpuRegister.Rax];

        Span<byte> packet = stackalloc byte[5 * sizeof(uint)];
        Assert.True(memory.TryRead(PacketAddress, packet));
        Assert.True(memory.TryWrite(RelocatedPacketAddress, packet));

        const ulong destination = 0x0FED_CBA9_8765_4321;
        var relocationDelta = RelocatedPacketAddress - PacketAddress;
        context[CpuRegister.Rdi] = patchHandle + relocationDelta;
        context[CpuRegister.Rsi] = destination;

        Assert.Equal(0, AgcExports.WriteDataPatchSetAddressOrOffset(context));
        Assert.True(context.TryReadUInt64(RelocatedPacketAddress + 8, out var relocatedDestination));
        Assert.Equal(destination, relocatedDestination);
        Assert.True(context.TryReadUInt64(PacketAddress + 8, out var originalDestination));
        Assert.Equal(0UL, originalDestination);
    }

    [Fact]
    public void WriteDataPatch_InvalidPacketReturnsInvalidArgument()
    {
        var (_, context) = CreateWriteDataContext();
        context[CpuRegister.Rdi] = MemoryBase + 0x3000;
        context[CpuRegister.Rsi] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.WriteDataPatchSetAddressOrOffset(context));
    }

    [Fact]
    public void WriteDataPatch_UnwritableFieldReturnsMemoryFault()
    {
        var (_, context) = CreateWriteDataContext();
        var truncatedPacketAddress = MemoryBase + MemorySize - sizeof(uint);
        Assert.True(context.TryWriteUInt32(truncatedPacketAddress, WriteDataHeader));
        context[CpuRegister.Rdi] = truncatedPacketAddress;
        context[CpuRegister.Rsi] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.WriteDataPatchSetAddressOrOffset(context));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateWriteDataContext()
    {
        var (memory, context) = CreateCommandBufferContext();
        Assert.True(context.TryWriteUInt32(DataAddress, 0xA5A5_5A5A));
        Assert.True(context.TryWriteUInt64(StackAddress + sizeof(ulong), 0));
        Assert.True(context.TryWriteUInt64(StackAddress + (2 * sizeof(ulong)), 1));

        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 4;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = DataAddress;
        context[CpuRegister.R9] = 1;
        context[CpuRegister.Rsp] = StackAddress;
        return (memory, context);
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateCommandBufferContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(context.TryWriteUInt64(CommandBufferAddress + 0x10, PacketAddress));
        Assert.True(context.TryWriteUInt64(CommandBufferAddress + 0x18, PacketAddress + 0x800));
        Assert.True(context.TryWriteUInt64(CommandBufferAddress + 0x20, 0));
        Assert.True(context.TryWriteUInt32(CommandBufferAddress + 0x30, 0));
        return (memory, context);
    }
}

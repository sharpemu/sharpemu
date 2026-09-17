// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcSetShRegisterRangeTests
{
    private const ulong BaseAddress = 0x2_3000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong ValuesAddress = BaseAddress + 0x800;

    [Theory]
    [InlineData(1u, 20u)]
    [InlineData(2u, 24u)]
    [InlineData(64u, 272u)]
    [InlineData(0x3FFFu, 65_548u)]
    public void GetSize_ReturnsEmitterByteCount(uint valueCount, uint expectedSize)
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = valueCount;

        var result = AgcExports.CbSetShRegisterRangeDirectGetSize(ctx);

        Assert.Equal((int)expectedSize, result);
        Assert.Equal(expectedSize, ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x4000u)]
    [InlineData(uint.MaxValue)]
    public void GetSize_ReturnsZeroForUnsupportedCount(uint valueCount)
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = valueCount;

        var result = AgcExports.CbSetShRegisterRangeDirectGetSize(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetSize_EqualsBytesConsumedByEmitter()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        WriteUInt32(memory, ValuesAddress, 0x1122_3344);
        WriteUInt32(memory, ValuesAddress + 4, 0x5566_7788);

        ctx[CpuRegister.Rdi] = 2;
        var size = AgcExports.CbSetShRegisterRangeDirectGetSize(ctx);

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x40;
        ctx[CpuRegister.Rdx] = ValuesAddress;
        ctx[CpuRegister.Rcx] = 2;
        var result = AgcExports.CbSetShRegisterRangeDirect(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(PacketAddress + 8, ctx[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + (ulong)size, ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(0x1122_3344u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, PacketAddress + 20));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(64u)]
    [InlineData(0x3FFFu)]
    public void Emit_UsesOneBulkCopyAndPreservesPacket(uint valueCount)
    {
        var memory = new CopyRecordingMemory();
        var sourceAddress = BaseAddress + 0x18000;
        var context = CreateEmitterContext(memory, sourceAddress, valueCount);
        var values = Enumerable.Range(0, (int)valueCount).Select(index => (uint)index + 0x1234).ToArray();
        for (var index = 0; index < values.Length; index++)
            WriteUInt32(memory, sourceAddress + (ulong)index * 4, values[index]);
        memory.ResetCounts();

        Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(context));

        Assert.Equal(PacketAddress + 8, context[CpuRegister.Rax]);
        Assert.Equal(1, memory.CopyCalls);
        Assert.Equal((PacketAddress + 16, sourceAddress, (ulong)valueCount * 4), memory.LastCopy);
        Assert.Equal(10, memory.ReadCalls);
        Assert.Equal(6, memory.WriteCalls);
        Assert.Equal(0xC0001000u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x6875000Du, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(0xC0007600u | (valueCount << 16), ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0x40u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(PacketAddress + ((ulong)valueCount + 4) * 4, ReadUInt64(memory, CommandBufferAddress + 0x10));
        for (var index = 0; index < values.Length; index++)
            Assert.Equal(values[index], ReadUInt32(memory, PacketAddress + 16 + (ulong)index * 4));
    }

    [Fact]
    public void Emit_UsesScalarCopyWhenBackendDeclinesBulkCopy()
    {
        var memory = new CopyRecordingMemory { SupportsCopy = false };
        var context = CreateEmitterContext(memory, ValuesAddress, 2);
        WriteUInt32(memory, ValuesAddress, 17);
        WriteUInt32(memory, ValuesAddress + 4, 29);

        Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(context));

        Assert.Equal(PacketAddress + 8, context[CpuRegister.Rax]);
        Assert.Equal(1, memory.CopyCalls);
        Assert.Equal(17u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(29u, ReadUInt32(memory, PacketAddress + 20));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(64u)]
    [InlineData(0x3FFFu)]
    public void Emit_NullValuesReservePayloadWithoutAccessingIt(uint valueCount)
    {
        var memory = new CopyRecordingMemory();
        var context = CreateEmitterContext(memory, 0, valueCount);
        for (uint index = 0; index < valueCount; index++)
            WriteUInt32(memory, PacketAddress + 16 + (ulong)index * 4, 0xA5000000u + index);
        memory.FailedReadAddress = PacketAddress + 16;
        memory.FailedWriteAddress = PacketAddress + 16;
        memory.ResetCounts();

        Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(context));

        Assert.Equal(PacketAddress + 8, context[CpuRegister.Rax]);
        Assert.Equal(0, memory.CopyCalls);
        Assert.Equal(10, memory.ReadCalls);
        Assert.Equal(6, memory.WriteCalls);
        Assert.Equal(0xC0001000u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x6875000Du, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(0xC0007600u | (valueCount << 16), ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0x40u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(PacketAddress + ((ulong)valueCount + 4) * 4, ReadUInt64(memory, CommandBufferAddress + 0x10));
        memory.FailedReadAddress = 0;
        memory.FailedWriteAddress = 0;
        for (uint index = 0; index < valueCount; index++)
            Assert.Equal(0xA5000000u + index, ReadUInt32(memory, PacketAddress + 16 + (ulong)index * 4));

        WriteUInt32(memory, context[CpuRegister.Rax] + 8, 0x11223344u);
        Assert.Equal(0x11223344u, ReadUInt32(memory, PacketAddress + 16));
    }

    [Fact]
    public void Emit_OverlappingValuesKeepForwardCopyOrder()
    {
        var memory = new CopyRecordingMemory();
        var context = CreateEmitterContext(memory, PacketAddress + 12, 3);
        WriteUInt32(memory, PacketAddress + 16, 17);
        WriteUInt32(memory, PacketAddress + 20, 29);

        Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(context));

        Assert.Equal(0, memory.CopyCalls);
        Assert.Equal(PacketAddress + 8, context[CpuRegister.Rax]);
        for (var index = 0; index < 3; index++)
            Assert.Equal(0x40u, ReadUInt32(memory, PacketAddress + 16 + (ulong)index * 4));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Emit_FailedMemoryAccessPreservesScalarFailureBehavior(bool failRead)
    {
        var memory = new CopyRecordingMemory();
        var context = CreateEmitterContext(memory, ValuesAddress, 3);
        WriteUInt32(memory, ValuesAddress, 17);
        WriteUInt32(memory, ValuesAddress + 4, 29);
        if (failRead) memory.FailedReadAddress = ValuesAddress + 4;
        else memory.FailedWriteAddress = PacketAddress + 20;

        Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(context));

        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(1, memory.CopyCalls);
        Assert.Equal(17u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(0u, ReadUInt32(memory, PacketAddress + 20));
        Assert.Equal(PacketAddress + 28, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Emit_InsufficientCapacityDoesNotCopyValues(bool reservePayload)
    {
        var memory = new CopyRecordingMemory();
        var context = CreateEmitterContext(memory, reservePayload ? 0 : ValuesAddress, 2);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 8);

        Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(context));

        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, memory.CopyCalls);
        Assert.Equal(PacketAddress + 8, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    private static CpuContext CreateEmitterContext(ICpuMemory memory, ulong sourceAddress, uint valueCount)
    {
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, BaseAddress + 0x14000);
        return new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = 0x40,
            [CpuRegister.Rdx] = sourceAddress,
            [CpuRegister.Rcx] = valueCount,
        };
    }

    private sealed class CopyRecordingMemory : ICpuMemory
    {
        private readonly FakeCpuMemory _memory = new(BaseAddress, 0x30000);
        public bool SupportsCopy { get; init; } = true;
        public ulong FailedReadAddress { get; set; }
        public ulong FailedWriteAddress { get; set; }
        public int CopyCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public (ulong Destination, ulong Source, ulong Length) LastCopy { get; private set; }

        public void ResetCounts() => (ReadCalls, WriteCalls) = (0, 0);

        private static bool Includes(ulong address, ulong length, ulong target) =>
            target != 0 && target >= address && target - address < length;

        public bool TryRead(ulong address, Span<byte> destination)
        {
            ReadCalls++;
            return !Includes(address, (ulong)destination.Length, FailedReadAddress) && _memory.TryRead(address, destination);
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            WriteCalls++;
            return !Includes(address, (ulong)source.Length, FailedWriteAddress) && _memory.TryWrite(address, source);
        }

        public bool TryCopy(ulong destinationAddress, ulong sourceAddress, ulong length)
        {
            CopyCalls++;
            LastCopy = (destinationAddress, sourceAddress, length);
            if (!SupportsCopy || Includes(sourceAddress, length, FailedReadAddress) ||
                Includes(destinationAddress, length, FailedWriteAddress)) return false;
            var bytes = new byte[checked((int)length)];
            return _memory.TryRead(sourceAddress, bytes) && _memory.TryWrite(destinationAddress, bytes);
        }
    }

    private static uint ReadUInt32(ICpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(ICpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt32(ICpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(ICpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

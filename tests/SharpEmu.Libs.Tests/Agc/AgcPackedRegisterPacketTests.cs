// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// The register and draw packet exports build each packet in full and write it at once. They leave the same guest bytes,
// cursor and result as a reference emitter that allocates every packet on its own and writes it a dword at a time, in
// roomy buffers and in buffers too full for their packets (no buffer-full callback is installed).
[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcPackedRegisterPacketTests
{
    private const ulong BaseAddress = 0x2_7000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong CursorAddress = CommandBufferAddress + 0x10;
    private const ulong PacketAddress = BaseAddress + 0x1000;
    private const ulong SourceAddress = BaseAddress + 0x8000;
    private const int MemorySize = 0x10000;
    private const uint RangeMarker = 0x6875000D;

    [Theory]
    [InlineData(1, 0x2000u)]
    [InlineData(2, 0x2000u)]
    [InlineData(3, 0x40u)]
    [InlineData(4, 0x18u)]
    public void ShRegisterRangeDirectMatchesTheReferenceEmitter(int seed, uint availableBytes)
    {
        var random = new System.Random(seed);
        for (var trial = 0; trial < 32; trial++)
        {
            var offset = (uint)random.Next(1, 0x3FF);
            var values = new uint[random.Next(1, 24)];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = (uint)random.Next();
            }

            AssertMatchesReference(
                availableBytes,
                memory =>
                {
                    for (var index = 0; index < values.Length; index++)
                    {
                        WriteUInt32(memory, SourceAddress + ((ulong)index * 4), values[index]);
                    }
                },
                memory =>
                {
                    var context = new CpuContext(memory, Generation.Gen5)
                    {
                        [CpuRegister.Rdi] = CommandBufferAddress,
                        [CpuRegister.Rsi] = offset,
                        [CpuRegister.Rdx] = SourceAddress,
                        [CpuRegister.Rcx] = (ulong)values.Length,
                    };
                    AgcExports.CbSetShRegisterRangeDirect(context);
                    return context[CpuRegister.Rax];
                },
                buffer =>
                    buffer.TryEmit([PacketHeader.Make(2, PacketOpcode.Nop), RangeMarker], out _) &&
                    buffer.TryEmit([PacketHeader.Make((uint)values.Length + 2, PacketOpcode.SetShaderRegister), offset, .. values],
                        out var commandAddress)
                        ? commandAddress
                        : 0);
        }
    }

    [Theory]
    [InlineData(5, 0x4000u)]
    [InlineData(6, 0x4000u)]
    [InlineData(7, 0x30u)]
    public void ShRegistersDirectMatchesTheReferenceEmitter(int seed, uint availableBytes)
    {
        var random = new System.Random(seed);
        for (var trial = 0; trial < 32; trial++)
        {
            var entries = new (uint Offset, uint Value)[random.Next(1, 40)];
            var next = (uint)random.Next(0, 0x200);
            for (var index = 0; index < entries.Length; index++)
            {
                // Runs of consecutive registers with gaps, shuffled below; a repeated register now and then.
                next += random.Next(4) == 0 ? (uint)random.Next(2, 9) : random.Next(10) == 0 ? 0u : 1u;
                entries[index] = (next, (uint)random.Next());
            }

            random.Shuffle(entries);
            AssertMatchesReference(
                availableBytes,
                memory => WriteEntries(memory, entries),
                memory => EmitShRegisters(memory, entries.Length),
                buffer => ReferenceShRegisters(buffer, entries));
        }
    }

    [Theory]
    [InlineData(8, 0x40000000UL, 0x280u, 0x280u, 2u)]
    [InlineData(9, 0x60180707UL, 0x10Fu, 0x10Fu, 0x22u)]
    [InlineData(10, 0x4F18060DUL, 0x8Fu, 0x8Fu, 2u)]
    public void DrawIndexIndirectMatchesTheReferenceEmitter(
        int seed, ulong modifier, uint vertexRegister, uint instanceRegister, uint initiator)
    {
        var random = new System.Random(seed);
        foreach (var availableBytes in new uint[] { 0x100, 0x14, 0x10 })
        {
            var dataOffset = (uint)random.Next();
            AssertMatchesReference(
                availableBytes,
                _ => { },
                memory =>
                {
                    var context = new CpuContext(memory, Generation.Gen5)
                    {
                        [CpuRegister.Rdi] = CommandBufferAddress,
                        [CpuRegister.Rsi] = dataOffset,
                        [CpuRegister.Rdx] = modifier,
                    };
                    AgcExports.DcbDrawIndexIndirect(context);
                    return context[CpuRegister.Rax];
                },
                buffer => buffer.TryEmit(
                    [PacketHeader.Make(5, PacketOpcode.DrawIndexIndirect), dataOffset, vertexRegister, instanceRegister, initiator],
                    out var commandAddress)
                    ? commandAddress
                    : 0);
        }
    }

    [Fact]
    public void EachPacketTakesOneAllocationAndOneWrite()
    {
        (uint Offset, uint Value)[] entries = [(0x12, 3), (0x10, 1), (0x20, 4), (0x11, 2)];
        var memory = new CountingMemory(Prepare(0x1000));
        WriteEntries(memory.Inner, entries);

        Assert.Equal(PacketAddress, EmitShRegisters(memory, entries.Length));

        // One read of the entries; each of the two packets (registers 0x10-0x12, then 0x20) reads the buffer state, moves
        // the cursor and is written.
        Assert.Equal((3, 4, 2), (memory.Reads, memory.Writes, memory.CursorWrites));
        Assert.Equal(PacketAddress + 32, ReadUInt64(memory.Inner, CursorAddress));

        memory.ResetCounts();
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = 0x1F4,
            [CpuRegister.Rdx] = 0x40000000UL,
        };
        AgcExports.DcbDrawIndexIndirect(context);

        Assert.Equal(PacketAddress + 32, context[CpuRegister.Rax]);
        Assert.Equal((1, 2, 1), (memory.Reads, memory.Writes, memory.CursorWrites));
    }

    [Fact]
    public void ShRegistersDirectWithAnUnreadableEntryWritesNothing()
    {
        // The last entry's value lies past the end of guest memory.
        var registersAddress = BaseAddress + MemorySize - 20;
        var memory = new CountingMemory(Prepare(0x1000));
        WriteUInt32(memory.Inner, registersAddress, 0x10);
        WriteUInt32(memory.Inner, registersAddress + 4, 1);
        WriteUInt32(memory.Inner, registersAddress + 8, 0x11);
        WriteUInt32(memory.Inner, registersAddress + 12, 2);
        WriteUInt32(memory.Inner, registersAddress + 16, 0x12);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = registersAddress,
            [CpuRegister.Rdx] = 3,
        };

        Assert.Equal(0, AgcExports.CbSetShRegistersDirect(context));

        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, memory.Writes);
        Assert.Equal(PacketAddress, ReadUInt64(memory.Inner, CursorAddress));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void ARangeWhoseMarkerOrHeaderCannotBeWrittenKeepsItsOneAllocation(int failedByte)
    {
        // The marker and packet share one allocation: a failed marker or header write returns null without moving the
        // cursor back or allocating again.
        var memory = new CountingMemory(Prepare(0x1000)) { FailedWriteAddress = PacketAddress + (ulong)failedByte };
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = 0x40,
            [CpuRegister.Rdx] = SourceAddress,
            [CpuRegister.Rcx] = 3,
        };

        Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(context));

        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(1, memory.CursorWrites);
        Assert.Equal(PacketAddress + 28, ReadUInt64(memory.Inner, CursorAddress));
    }

    // Runs the export and the reference emitter from the same starting memory and compares the result and every byte.
    private static void AssertMatchesReference(
        uint availableBytes,
        Action<FakeCpuMemory> writeInputs,
        Func<FakeCpuMemory, ulong> export,
        Func<ReferenceBuffer, ulong> reference)
    {
        var memory = Prepare(availableBytes);
        writeInputs(memory);
        var result = export(memory);

        var expected = Prepare(availableBytes);
        writeInputs(expected);
        var expectedResult = reference(new ReferenceBuffer(expected, availableBytes));

        Assert.Equal(expectedResult, result);
        Assert.Equal(Snapshot(expected), Snapshot(memory));
    }

    // SET_SH_REG packets for (offset, value) entries: sorted by offset, one packet per run of consecutive offsets (a
    // repeated offset starts a new packet), in order; the result is the first packet's address.
    private static ulong ReferenceShRegisters(ReferenceBuffer buffer, (uint Offset, uint Value)[] entries)
    {
        var sorted = entries.ToArray();
        Array.Sort(sorted, static (left, right) => left.Offset.CompareTo(right.Offset));
        ulong firstCommandAddress = 0;
        for (var start = 0; start < sorted.Length;)
        {
            var end = start + 1;
            while (end < sorted.Length && sorted[end].Offset == sorted[end - 1].Offset + 1)
            {
                end++;
            }

            var packet = new uint[end - start + 2];
            packet[0] = PacketHeader.Make((uint)packet.Length, PacketOpcode.SetShaderRegister);
            packet[1] = sorted[start].Offset & 0xFFFFu;
            for (var index = start; index < end; index++)
            {
                packet[2 + index - start] = sorted[index].Value;
            }

            if (!buffer.TryEmit(packet, out var commandAddress))
            {
                return 0;
            }

            firstCommandAddress = firstCommandAddress == 0 ? commandAddress : firstCommandAddress;
            start = end;
        }

        return firstCommandAddress;
    }

    // A packet goes at the cursor when it fits below the down cursor; the cursor is stored, then the packet is written a
    // dword at a time. Nothing is reserved and no buffer-full callback is installed, so a packet that does not fit fails.
    private sealed class ReferenceBuffer(FakeCpuMemory memory, uint availableBytes)
    {
        private ulong _cursor = PacketAddress;

        public bool TryEmit(ReadOnlySpan<uint> packet, out ulong commandAddress)
        {
            var bytes = (ulong)packet.Length * sizeof(uint);
            if (bytes > PacketAddress + availableBytes - _cursor)
            {
                commandAddress = 0;
                return false;
            }

            commandAddress = _cursor;
            _cursor += bytes;
            WriteUInt64(memory, CursorAddress, _cursor);
            for (var index = 0; index < packet.Length; index++)
            {
                WriteUInt32(memory, commandAddress + ((ulong)index * sizeof(uint)), packet[index]);
            }

            return true;
        }
    }

    private sealed class CountingMemory(FakeCpuMemory inner) : ICpuMemory
    {
        public FakeCpuMemory Inner => inner;
        public ulong FailedWriteAddress { get; init; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int CursorWrites { get; private set; }

        public void ResetCounts() => (Reads, Writes, CursorWrites) = (0, 0, 0);

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            Reads++;
            return inner.TryRead(virtualAddress, destination);
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            Writes++;
            CursorWrites += virtualAddress == CursorAddress && source.Length == sizeof(ulong) ? 1 : 0;
            var fails = FailedWriteAddress != 0 && FailedWriteAddress >= virtualAddress &&
                FailedWriteAddress - virtualAddress < (ulong)source.Length;
            return !fails && inner.TryWrite(virtualAddress, source);
        }
    }

    private static ulong EmitShRegisters(ICpuMemory memory, int count)
    {
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = SourceAddress,
            [CpuRegister.Rdx] = (ulong)count,
        };
        AgcExports.CbSetShRegistersDirect(context);
        return context[CpuRegister.Rax];
    }

    private static void WriteEntries(FakeCpuMemory memory, (uint Offset, uint Value)[] entries)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            WriteUInt32(memory, SourceAddress + ((ulong)index * 8), entries[index].Offset);
            WriteUInt32(memory, SourceAddress + ((ulong)index * 8) + 4, entries[index].Value);
        }
    }

    private static FakeCpuMemory Prepare(uint availableBytes)
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        WriteUInt64(memory, CursorAddress, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + availableBytes);
        return memory;
    }

    private static byte[] Snapshot(FakeCpuMemory memory)
    {
        var bytes = new byte[MemorySize];
        Assert.True(memory.TryRead(BaseAddress, bytes));
        return bytes;
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

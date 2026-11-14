// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// AgcPacketWrites: marker emission keeps the ordinary write order, including write failures.
// The read optimizations have their own cases.
public sealed class AgcPacketWritesTests
{
    private const ulong BaseAddress = 0x2_4000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong ValuesAddress = BaseAddress + 0x18000;
    private const ulong StackAddress = BaseAddress + 0x20000;
    private const ulong RoomyCursorDown = BaseAddress + 0x14000;
    private const int MemorySize = 0x30000;

    private static T With<T>(bool atOnce, Func<T> action)
    {
        var saved = AgcPacketWrites.OverrideForTests;
        AgcPacketWrites.OverrideForTests = atOnce;
        try
        {
            return action();
        }
        finally
        {
            AgcPacketWrites.OverrideForTests = saved;
        }
    }

    private sealed record Emitted(int Result, ulong Rax, ulong Cursor, byte[] Bytes, int Reads, int Writes, int Copies);

    private static void AssertSame(Emitted original, Emitted atOnce)
    {
        Assert.Equal(original.Result, atOnce.Result);
        Assert.Equal(original.Rax, atOnce.Rax);
        Assert.Equal(original.Cursor, atOnce.Cursor);
        Assert.Equal(original.Bytes, atOnce.Bytes);
    }

    private static Emitted PushMarker(bool atOnce, byte[] name, ulong nameAddress, int memorySize = MemorySize, ulong failedWrite = 0)
    {
        var memory = new CountingMemory(memorySize);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, RoomyCursorDown);
        Assert.True(memory.TryWrite(nameAddress, name));
        memory.FailedWriteAddress = failedWrite;
        memory.ResetCounts();
        var ctx = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = nameAddress,
        };
        var result = With(atOnce, () => AgcExports.DcbPushMarker(ctx));
        var (reads, writes, copies) = (memory.ReadCalls, memory.WriteCalls, memory.CopyCalls);
        memory.FailedWriteAddress = 0;
        var bytes = new byte[4200];
        Assert.True(memory.TryRead(PacketAddress, bytes));
        return new Emitted(result, ctx[CpuRegister.Rax], ReadUInt64(memory, CommandBufferAddress + 0x10), bytes, reads, writes, copies);
    }

    private static byte[] Name(int length, bool terminated = true)
    {
        var name = new byte[length + (terminated ? 1 : 0)];
        for (var index = 0; index < length; index++)
            name[index] = (byte)('a' + index % 26);
        return name;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(70)]
    [InlineData(4094)]
    public void APushMarkerKeepsOrdinaryEmissionWhenPacketWritesAreEnabled(int length)
    {
        var original = PushMarker(false, Name(length), ValuesAddress);
        var atOnce = PushMarker(true, Name(length), ValuesAddress);

        AssertSame(original, atOnce);
        Assert.Equal(PacketAddress, atOnce.Rax);
        // Name reads may differ; packet allocation and every write keep their ordinary order.
        Assert.Equal(original.Writes, atOnce.Writes);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void ANameEndingAtTheEndOfReadableMemoryReadsTheSame(int length)
    {
        // The name and its terminator end at the last readable byte, so a 64-byte chunk past it cannot be read.
        var name = Name(length);
        var nameAddress = BaseAddress + MemorySize - (ulong)name.Length;
        var original = PushMarker(false, name, nameAddress);
        var atOnce = PushMarker(true, name, nameAddress);

        AssertSame(original, atOnce);
        Assert.Equal(PacketAddress, atOnce.Rax);
    }

    [Fact]
    public void ANameWithoutATerminatorFailsTheSame()
    {
        var unterminated = Name(40, terminated: false);
        var original = PushMarker(false, unterminated, BaseAddress + MemorySize - 40);
        var atOnce = PushMarker(true, unterminated, BaseAddress + MemorySize - 40);
        var tooLong = PushMarker(true, Name(4095), ValuesAddress);

        AssertSame(original, atOnce);
        Assert.Equal(0UL, atOnce.Rax);
        Assert.Equal(PacketAddress, atOnce.Cursor);
        Assert.Equal(0UL, tooLong.Rax);
    }

    [Fact]
    public void AMarkerWhoseCursorCannotMoveFailsAsTheOriginalPathDoes()
    {
        var original = PushMarker(false, Name(20), ValuesAddress, failedWrite: CommandBufferAddress + 0x10);
        var atOnce = PushMarker(true, Name(20), ValuesAddress, failedWrite: CommandBufferAddress + 0x10);

        AssertSame(original, atOnce);
        Assert.Equal(0UL, atOnce.Rax);
        Assert.Equal(PacketAddress, atOnce.Cursor);
    }

    [Fact]
    public void AMarkerWhosePacketCannotBeWrittenKeepsTheOriginalPath()
    {
        // Ordinary header/name-word writes stop at the same failing address, without a speculative block or rollback.
        var original = PushMarker(false, Name(20), ValuesAddress, failedWrite: PacketAddress + 12);
        var atOnce = PushMarker(true, Name(20), ValuesAddress, failedWrite: PacketAddress + 12);

        AssertSame(original, atOnce);
        Assert.Equal(0UL, atOnce.Rax);
    }

    private static (int Result, ulong Rax, byte[] Packet, int Reads) Release(bool atOnce, ulong stackAddress)
    {
        var memory = new CountingMemory();
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, RoomyCursorDown);
        ulong[] arguments = [2, 0x1122_3344_5566_7788UL, 0, 0, 5, 0xFFFF_FFFF];
        for (var index = 0; index < arguments.Length; index++)
        {
            var at = stackAddress + 8 + (ulong)index * 8;
            if (at + 8 <= BaseAddress + MemorySize)
                WriteUInt64(memory, at, arguments[index]);
        }
        var ctx = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.R9] = BaseAddress + 0xC08,
            [CpuRegister.Rsp] = stackAddress,
        };
        memory.ResetCounts();
        var result = With(atOnce, () => AgcExports.CbReleaseMem(ctx));
        var reads = memory.ReadCalls;
        var packet = new byte[64];
        Assert.True(memory.TryRead(PacketAddress, packet));
        return (result, ctx[CpuRegister.Rax], packet, reads);
    }

    [Fact]
    public void AReleaseReadsItsSixStackWordsInOneRead()
    {
        var original = Release(atOnce: false, StackAddress);
        var atOnce = Release(atOnce: true, StackAddress);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, original.Result);
        Assert.Equal(PacketAddress, original.Rax);
        Assert.Equal((original.Result, original.Rax), (atOnce.Result, atOnce.Rax));
        Assert.Equal(original.Packet, atOnce.Packet);
        Assert.Equal(original.Reads - 5, atOnce.Reads);
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(8)]
    public void AReleaseWhoseStackWordsCannotAllBeReadFailsAsTheOriginalPathDoes(int pastEnd)
    {
        // The last stack word, or every one, lies past readable memory.
        var stackAddress = BaseAddress + MemorySize - 56 + (ulong)pastEnd;
        var original = Release(atOnce: false, stackAddress);
        var atOnce = Release(atOnce: true, stackAddress);

        Assert.Equal(0UL, original.Rax);
        Assert.Equal((original.Result, original.Rax), (atOnce.Result, atOnce.Rax));
        Assert.Equal(original.Packet, atOnce.Packet);
    }

    private sealed record OrderedEmission(
        int Result,
        ulong Rax,
        ulong Cursor,
        byte[] Bytes,
        ulong[] CursorUpdates,
        int RefusedRollbacks);

    private static OrderedEmission EmitWithCursorHistory(bool atOnce, bool marker, ulong failedPacketByte)
    {
        var memory = new CursorHistoryMemory(failedPacketByte);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, RoomyCursorDown);
        var sentinel = new byte[96];
        Array.Fill(sentinel, (byte)0xCD);
        Assert.True(memory.TryWrite(PacketAddress, sentinel));

        if (marker)
        {
            Assert.True(memory.TryWrite(ValuesAddress, Name(20)));
        }
        else
        {
            WriteUInt32(memory, ValuesAddress, 0x1234_0000);
            WriteUInt32(memory, ValuesAddress + 4, 0x1234_0001);
            WriteUInt32(memory, ValuesAddress + 8, 0x1234_0002);
        }

        var ctx = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = marker ? ValuesAddress : 0x40,
            [CpuRegister.Rdx] = ValuesAddress,
            [CpuRegister.Rcx] = 3,
        };

        memory.Arm();
        var result = With(atOnce, () => marker
            ? AgcExports.DcbPushMarker(ctx)
            : AgcExports.CbSetShRegisterRangeDirect(ctx));
        memory.Disarm();

        var bytes = new byte[96];
        Assert.True(memory.TryRead(PacketAddress, bytes));
        return new OrderedEmission(result, ctx[CpuRegister.Rax],
            ReadUInt64(memory, CommandBufferAddress + 0x10), bytes,
            memory.CursorUpdates.ToArray(), memory.RefusedRollbacks);
    }

    private static void AssertSameOrderedEmission(OrderedEmission original, OrderedEmission candidate)
    {
        Assert.Equal(original.Result, candidate.Result);
        Assert.Equal(original.Rax, candidate.Rax);
        Assert.Equal(original.Cursor, candidate.Cursor);
        Assert.Equal(original.Bytes, candidate.Bytes);
        Assert.Equal(original.CursorUpdates, candidate.CursorUpdates);
        Assert.Equal(original.RefusedRollbacks, candidate.RefusedRollbacks);
    }

    [Fact]
    public void AMarkerWriteFailureDoesNotRetryFromAnUnrestoredCursor()
    {
        var original = EmitWithCursorHistory(atOnce: false, marker: true, failedPacketByte: PacketAddress + 20);
        var candidate = EmitWithCursorHistory(atOnce: true, marker: true, failedPacketByte: PacketAddress + 20);

        Assert.Equal(0UL, original.Rax);
        Assert.Equal(PacketAddress + 28, original.Cursor);
        Assert.Equal(new[] { PacketAddress + 28 }, original.CursorUpdates);
        Assert.Equal(0, original.RefusedRollbacks);
        AssertSameOrderedEmission(original, candidate);
    }

    private sealed class CursorHistoryMemory(ulong failedPacketByte) : ICpuMemory
    {
        private readonly FakeCpuMemory _memory = new(BaseAddress, MemorySize);
        private bool _armed;
        private bool _cursorAdvanced;
        public System.Collections.Generic.List<ulong> CursorUpdates { get; } = new();
        public int RefusedRollbacks { get; private set; }

        public void Arm() => _armed = true;
        public void Disarm() => _armed = false;

        public bool TryRead(ulong address, Span<byte> destination) => _memory.TryRead(address, destination);

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (_armed && address == CommandBufferAddress + 0x10 && source.Length == sizeof(ulong))
            {
                var next = BinaryPrimitives.ReadUInt64LittleEndian(source);
                if (_cursorAdvanced && next == PacketAddress)
                {
                    RefusedRollbacks++;
                    return false;
                }

                if (!_memory.TryWrite(address, source))
                    return false;

                CursorUpdates.Add(next);
                _cursorAdvanced |= next != PacketAddress;
                return true;
            }

            if (_armed && failedPacketByte != 0 &&
                failedPacketByte >= address && failedPacketByte - address < (ulong)source.Length)
            {
                return false;
            }

            return _memory.TryWrite(address, source);
        }

        public bool TryCopy(ulong destinationAddress, ulong sourceAddress, ulong length)
        {
            var bytes = new byte[checked((int)length)];
            return TryRead(sourceAddress, bytes) && TryWrite(destinationAddress, bytes);
        }
    }

    private sealed class CountingMemory(int size = MemorySize) : ICpuMemory
    {
        private readonly FakeCpuMemory _memory = new(BaseAddress, size);
        public ulong FailedReadAddress { get; set; }
        public ulong FailedWriteAddress { get; set; }
        public int CopyCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }

        public void ResetCounts() => (ReadCalls, WriteCalls, CopyCalls) = (0, 0, 0);

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
            if (Includes(sourceAddress, length, FailedReadAddress) || Includes(destinationAddress, length, FailedWriteAddress))
                return false;
            var bytes = new byte[checked((int)length)];
            return _memory.TryRead(sourceAddress, bytes) && _memory.TryWrite(destinationAddress, bytes);
        }
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

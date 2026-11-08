// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed unsafe class ParallelGuestCopyTests
{
    // Guest memory whose bytes follow from their addresses. Reads can be slowed down and fail at one
    // address, and each thread's reads are counted.
    private sealed class PatternMemory : ICpuMemory
    {
        public int DelayMilliseconds { get; init; }

        public ulong FailingAddress { get; init; } = ulong.MaxValue;

        public ConcurrentDictionary<int, int> ReadsByThread { get; } = new();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            ReadsByThread.AddOrUpdate(Environment.CurrentManagedThreadId, 1, (_, reads) => reads + 1);
            if (DelayMilliseconds != 0)
                Thread.Sleep(DelayMilliseconds);
            if (virtualAddress == FailingAddress)
                return false;
            for (var index = 0; index < destination.Length; index++)
                destination[index] = ByteAt(virtualAddress + (ulong)index);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => throw new NotSupportedException();
    }

    private static byte ByteAt(ulong address) => (byte)((address * 31) ^ (address >> 8));

    private static ParallelGuestCopy.Piece[] Pieces(byte* destination, ulong address, int count, int length) =>
        Enumerable.Range(0, count)
            .Select(index => new ParallelGuestCopy.Piece(address + (ulong)(index * length), (nint)(destination + index * length), length))
            .ToArray();

    [Fact]
    public void HelpersCopyPiecesBesideTheCaller()
    {
        var memory = new PatternMemory { DelayMilliseconds = 1 };
        using var copy = new ParallelGuestCopy(memory, helperCount: 3);
        var staging = new byte[64 * 256];
        fixed (byte* destination = staging)
        {
            Assert.True(copy.Run(Pieces(destination, 0x1000, 64, 256), 64, parallel: true));
        }

        for (var index = 0; index < staging.Length; index++)
            Assert.Equal(ByteAt(0x1000 + (ulong)index), staging[index]);
        Assert.Equal(64, memory.ReadsByThread.Values.Sum());
        Assert.True(memory.ReadsByThread.Count > 1);
    }

    [Fact]
    public void ASmallCopyRunsOnTheCallerAlone()
    {
        var memory = new PatternMemory();
        using var copy = new ParallelGuestCopy(memory, helperCount: 3);
        var staging = new byte[8 * 64];
        fixed (byte* destination = staging)
        {
            Assert.True(copy.Run(Pieces(destination, 0x2000, 8, 64), 8, parallel: false));
        }

        for (var index = 0; index < staging.Length; index++)
            Assert.Equal(ByteAt(0x2000 + (ulong)index), staging[index]);
        Assert.Equal(Environment.CurrentManagedThreadId, Assert.Single(memory.ReadsByThread.Keys));
    }

    [Fact]
    public void AFailedReadIsReportedToTheCaller()
    {
        var memory = new PatternMemory { FailingAddress = 0x3000 + 5 * 128 };
        using var copy = new ParallelGuestCopy(memory, helperCount: 2);
        var staging = new byte[16 * 128];
        fixed (byte* destination = staging)
        {
            Assert.False(copy.Run(Pieces(destination, 0x3000, 16, 128), 16, parallel: true));
            Assert.True(copy.Run(Pieces(destination, 0x9000, 16, 128), 16, parallel: true));
        }
    }

    // The pieces array is reused from run to run, as a sweep reuses it: a helper that wakes late for an
    // earlier run must never copy a piece of a later one, and every byte of every run must arrive.
    [Fact]
    public void RunsReuseTheHelpersAndThePieceArray()
    {
        var memory = new PatternMemory();
        using var copy = new ParallelGuestCopy(memory, helperCount: 3);
        var random = new System.Random(11);
        var pieces = new ParallelGuestCopy.Piece[32];
        var staging = new byte[32 * 96];
        fixed (byte* destination = staging)
        {
            for (var run = 0; run < 400; run++)
            {
                Array.Clear(staging);
                var count = random.Next(1, pieces.Length + 1);
                var address = 0x10000 + (ulong)random.Next(0x10000);
                for (var index = 0; index < count; index++)
                    pieces[index] = new ParallelGuestCopy.Piece(address + (ulong)(index * 96), (nint)(destination + index * 96), 96);
                Assert.True(copy.Run(pieces, count, parallel: random.Next(4) != 0));
                for (var index = 0; index < staging.Length; index++)
                    Assert.Equal(index < count * 96 ? ByteAt(address + (ulong)index) : (byte)0, staging[index]);
            }
        }
    }
}

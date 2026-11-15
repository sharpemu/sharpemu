// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Bindless heap revalidation reads sorted records in runs (VulkanVideoPresenter.HeapRecordRunEnd).
public sealed class BindlessHeapRunTests
{
    private const uint ImageBytes = BindlessHeap.ImageRecordBytes;

    [Fact]
    public void ARunKeepsRepeatedAndNearbyRecordsAndEndsAtAGap()
    {
        var gapRecords = VulkanVideoPresenter.HeapRunGapBytes / ImageBytes;
        uint[] indexes = [3, 3, 4, 4 + gapRecords + 1, 4 + gapRecords + 1 + gapRecords + 2];
        Assert.Equal(4, VulkanVideoPresenter.HeapRecordRunEnd(indexes, 0, ImageBytes, 49152));
        // 37 skips 32 records after 4 (the gap limit) and stays; 71 skips 33 after 37 and starts its own run.
        Assert.Equal(4, VulkanVideoPresenter.HeapRecordRunEnd(indexes, 3, ImageBytes, 49152));
        Assert.Equal(5, VulkanVideoPresenter.HeapRecordRunEnd(indexes, 4, ImageBytes, 49152));
    }

    [Fact]
    public void ARunEndsBeforeTheRecordsTheHeapHasNot()
    {
        uint[] indexes = [10, 11, 12, 13];
        Assert.Equal(2, VulkanVideoPresenter.HeapRecordRunEnd(indexes, 0, ImageBytes, 12));
    }

    [Fact]
    public void ARunSpansAtMostTheRunLimit()
    {
        var limitRecords = VulkanVideoPresenter.HeapRunMaxBytes / ImageBytes;
        var indexes = Enumerable.Range(0, (int)limitRecords + 5).Select(static index => (uint)index).ToArray();
        Assert.Equal((int)limitRecords, VulkanVideoPresenter.HeapRecordRunEnd(indexes, 0, ImageBytes, uint.MaxValue));
    }

    // Any sorted set of indexes splits into runs that cover it once each, and every run obeys the limits.
    [Theory]
    [InlineData(1, 16u)]
    [InlineData(2, 32u)]
    [InlineData(3, 16u)]
    [InlineData(4, 32u)]
    public void RunsPartitionSortedIndexesWithinTheLimits(int seed, uint recordBytes)
    {
        var random = new System.Random(seed);
        var indexes = Enumerable.Range(0, 3000).Select(_ => (uint)random.Next(0, 49152)).Order().ToArray();
        var start = 0;
        while (start < indexes.Length)
        {
            var end = VulkanVideoPresenter.HeapRecordRunEnd(indexes, start, recordBytes, 49152);
            Assert.True(end > start);
            Assert.True(((ulong)indexes[end - 1] - indexes[start] + 1) * recordBytes <= VulkanVideoPresenter.HeapRunMaxBytes);
            for (var at = start + 1; at < end; at++)
            {
                Assert.True((ulong)(indexes[at] - indexes[at - 1]) * recordBytes <= VulkanVideoPresenter.HeapRunGapBytes + recordBytes);
            }

            if (end < indexes.Length)
            {
                var next = indexes[end];
                Assert.True((ulong)(next - indexes[end - 1]) * recordBytes > VulkanVideoPresenter.HeapRunGapBytes + recordBytes ||
                    ((ulong)next - indexes[start] + 1) * recordBytes > VulkanVideoPresenter.HeapRunMaxBytes);
            }

            start = end;
        }
    }

    private const ulong MemoryBase = 0x10000;

    // Guest memory as a byte array from MemoryBase, and one heap in it; every visit is kept with its words.
    private sealed class FakeHeap(byte[] memory, ulong heapBase, uint records, uint[] indexes, bool sampler)
        : VulkanVideoPresenter.IHeapRunVisitor
    {
        public ulong Base = heapBase;
        public uint Records = records;
        public bool FailRunReads;
        public int RunReads;
        public int RecordReads;
        public readonly List<(int Position, uint[] Words)> Visits = new();
        // Runs on each visit; true means the visit resolved.
        public Func<int, bool> OnVisit = _ => false;
        public readonly uint RecordBytes = sampler ? BindlessHeap.SamplerRecordBytes : BindlessHeap.ImageRecordBytes;

        public BindlessHeapDescriptor Heap(bool samplerHeap) => new(samplerHeap, Base, Records, RecordBytes);

        public bool TryRead(ulong address, Span<byte> destination)
        {
            RunReads++;
            return !FailRunReads && Read(address, destination);
        }

        public int ReadRecord(int position, Span<uint> words)
        {
            RecordReads++;
            var index = indexes[position];
            var count = (int)(RecordBytes / sizeof(uint));
            return Base != 0 && index < Records &&
                Read(Base + (ulong)index * RecordBytes, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words[..count])) ? count : 0;
        }

        public bool Visit(int position, ReadOnlySpan<uint> words)
        {
            Visits.Add((position, words.ToArray()));
            return OnVisit(position);
        }

        // What one read of the record at index returns now.
        public uint[] RecordWords(uint index)
        {
            var words = new uint[RecordBytes / sizeof(uint)];
            return Base != 0 && index < Records &&
                Read(Base + (ulong)index * RecordBytes, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan())) ? words : [];
        }

        private bool Read(ulong address, Span<byte> destination)
        {
            if (address < MemoryBase || address - MemoryBase + (ulong)destination.Length > (ulong)memory.Length) return false;
            memory.AsSpan((int)(address - MemoryBase), destination.Length).CopyTo(destination);
            return true;
        }
    }

    private static byte[] Pattern(int bytes, int seed)
    {
        var memory = new byte[bytes];
        new System.Random(seed).NextBytes(memory);
        return memory;
    }

    private static void Visit(FakeHeap heap, uint[] indexes, bool sampler, byte[] buffer)
    {
        Span<uint> scratch = stackalloc uint[8];
        VulkanVideoPresenter.VisitHeapRuns(ref heap, indexes, 0, indexes.Length, sampler, buffer, scratch);
    }

    // Every entry is visited once, in order, with the words its own read returns, from far fewer reads.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunsVisitEveryEntryOnceInOrderWithItsRecordWords(bool sampler)
    {
        var memory = Pattern(0x40000, 7);
        var random = new System.Random(3);
        var indexes = Enumerable.Range(0, 600).Select(_ => (uint)random.Next(0, 4000)).Order().ToArray();
        var heap = new FakeHeap(memory, MemoryBase + 0x100, 4000, indexes, sampler);
        Visit(heap, indexes, sampler, new byte[VulkanVideoPresenter.HeapRunMaxBytes]);
        Assert.Equal(Enumerable.Range(0, indexes.Length), heap.Visits.Select(visit => visit.Position));
        for (var at = 0; at < indexes.Length; at++)
        {
            Assert.Equal(heap.RecordWords(indexes[at]), heap.Visits[at].Words);
        }

        Assert.True(heap.RunReads < indexes.Length / 4);
        Assert.Equal(0, heap.RecordReads);
    }

    // A resolve may land bytes over later records of its run: the entries after it read them after it.
    [Fact]
    public void AResolveRereadsTheRestOfItsRun()
    {
        var memory = Pattern(0x10000, 11);
        uint[] indexes = [0, 1, 2, 3];
        var heap = new FakeHeap(memory, MemoryBase, 64, indexes, sampler: false);
        heap.OnVisit = position =>
        {
            if (position != 1) return false;
            memory.AsSpan((int)(2 * heap.RecordBytes), (int)heap.RecordBytes).Fill(0xAB);
            return true;
        };
        Visit(heap, indexes, sampler: false, new byte[VulkanVideoPresenter.HeapRunMaxBytes]);
        Assert.Equal(4, heap.Visits.Count);
        Assert.All(heap.Visits[2].Words, word => Assert.Equal(0xABABABABu, word));
        Assert.Equal(heap.RecordWords(3), heap.Visits[3].Words);
        Assert.Equal(2, heap.RunReads);
    }

    // A nested revalidation inside a resolve may reuse the run buffer: no entry after the resolve reads it as it was.
    [Fact]
    public void ARunBufferOverwrittenDuringAResolveIsReadAgain()
    {
        var memory = Pattern(0x10000, 13);
        uint[] indexes = [4, 5, 6, 7];
        var buffer = new byte[VulkanVideoPresenter.HeapRunMaxBytes];
        var heap = new FakeHeap(memory, MemoryBase, 64, indexes, sampler: false);
        heap.OnVisit = position =>
        {
            if (position != 0) return false;
            buffer.AsSpan().Fill(0x5A);
            return true;
        };
        Visit(heap, indexes, sampler: false, buffer);
        for (var at = 0; at < indexes.Length; at++)
        {
            Assert.Equal(heap.RecordWords(indexes[at]), heap.Visits[at].Words);
        }
    }

    // A run that cannot be read is visited one record read at a time, each right before its visit.
    [Fact]
    public void AnUnreadableRunFallsBackToOneReadPerEntry()
    {
        var memory = Pattern(0x10000, 17);
        uint[] indexes = [1, 2, 2, 9];
        var heap = new FakeHeap(memory, MemoryBase, 64, indexes, sampler: true) { FailRunReads = true };
        heap.OnVisit = position =>
        {
            if (position != 1) return false;
            memory.AsSpan((int)(9 * heap.RecordBytes), (int)heap.RecordBytes).Fill(0x11);
            return true;
        };
        Visit(heap, indexes, sampler: true, new byte[VulkanVideoPresenter.HeapRunMaxBytes]);
        Assert.Equal(4, heap.RecordReads);
        for (var at = 0; at < indexes.Length; at++)
        {
            Assert.Equal(heap.RecordWords(indexes[at]), heap.Visits[at].Words);
        }

        Assert.All(heap.Visits[3].Words, word => Assert.Equal(0x11111111u, word));
    }

    // Records the heap does not hold, and every record of a heap at address zero, get no words.
    [Fact]
    public void RecordsPastTheHeapGetNoWords()
    {
        var memory = Pattern(0x10000, 19);
        uint[] indexes = [1, 3, 5, 9];
        var heap = new FakeHeap(memory, MemoryBase, 4, indexes, sampler: false);
        Visit(heap, indexes, sampler: false, new byte[VulkanVideoPresenter.HeapRunMaxBytes]);
        Assert.Equal(heap.RecordWords(1), heap.Visits[0].Words);
        Assert.Equal(heap.RecordWords(3), heap.Visits[1].Words);
        Assert.Empty(heap.Visits[2].Words);
        Assert.Empty(heap.Visits[3].Words);

        var unplaced = new FakeHeap(memory, 0, 64, indexes, sampler: false);
        Visit(unplaced, indexes, sampler: false, new byte[VulkanVideoPresenter.HeapRunMaxBytes]);
        Assert.All(unplaced.Visits, visit => Assert.Empty(visit.Words));
        Assert.Equal(0, unplaced.RunReads);
    }

    // A resolve that moves the heap: the entries after it read their records at the new base.
    [Fact]
    public void AMovedHeapIsReadAtItsNewBaseAfterAResolve()
    {
        var memory = Pattern(0x20000, 23);
        uint[] indexes = [0, 1, 2];
        var heap = new FakeHeap(memory, MemoryBase, 64, indexes, sampler: false);
        heap.OnVisit = position =>
        {
            if (position != 0) return false;
            heap.Base = MemoryBase + 0x10000;
            return true;
        };
        Visit(heap, indexes, sampler: false, new byte[VulkanVideoPresenter.HeapRunMaxBytes]);
        Assert.Equal(heap.RecordWords(1), heap.Visits[1].Words);
        Assert.Equal(heap.RecordWords(2), heap.Visits[2].Words);
        Assert.NotEqual(heap.Visits[1].Words, new FakeHeap(memory, MemoryBase, 64, indexes, sampler: false).RecordWords(1));
    }
}

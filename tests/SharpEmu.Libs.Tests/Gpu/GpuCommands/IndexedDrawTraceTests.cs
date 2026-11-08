// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class IndexedDrawTraceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Signature_NormalizesIndexWidth(int elementSize)
    {
        byte[] indices = new byte[3 * elementSize];
        indices[elementSize] = 1;
        indices[2 * elementSize] = 255;
        byte[] normalized = [0, 0, 0, 0, 1, 0, 0, 0, 255, 0, 0, 0];
        Assert.Equal(Convert.ToHexString(SHA256.HashData(normalized)),
            IndexedDrawTrace.ComputeSignature(indices, elementSize));
    }

    [Fact]
    public void Signature_RejectsIncompleteIndex()
    {
        Assert.Throws<ArgumentException>(() => IndexedDrawTrace.ComputeSignature(new byte[3], 2));
    }

    [Fact]
    public void FrameCounts_ReportsAbsenceAndResumesAfterTheDetailLimit()
    {
        var counts = new IndexedDrawTrace.FrameCounts();
        for (var index = 0; index < 7; index++) Assert.True(counts.Record("indices", "emitted"));
        Assert.Equal(7, Assert.Single(counts.CloseFrame()).Value);
        Assert.Equal(0, Assert.Single(counts.CloseFrame()).Value);
        counts.Record("indices", "emitted");
        Assert.Equal(1, Assert.Single(counts.CloseFrame()).Value);
    }

    [Fact]
    public void FrameCounts_BoundsDistinctPairs()
    {
        var counts = new IndexedDrawTrace.FrameCounts();
        for (var index = 0; index < 256; index++) Assert.True(counts.Record(index.ToString(), "emitted"));
        Assert.False(counts.Record("overflow", "emitted"));
        Assert.True(counts.Record("0", "emitted"));
        Assert.Equal(256, counts.CloseFrame().Length);
    }

    [Fact]
    public void SkippedBuffer_UsesLocalIndexStateAndFindsNestedDraw()
    {
        uint[] words = [PacketHeader.Make(4, PacketOpcode.IndirectBuffer), 0x1010, 0, 10,
            PacketHeader.Make(2, PacketOpcode.IndexType), 1,
            PacketHeader.Make(3, PacketOpcode.IndexBase), 0x4000, 0,
            PacketHeader.Make(5, PacketOpcode.DrawIndexOffset2), 100, 3, 12, 0];
        var memory = MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
        var unchanged = memory.ToArray();
        var draws = new List<(uint Count, uint Type, ulong Address, ulong Packet)>();
        var issues = new List<string>();
        bool Read(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || address - 0x1000 + (ulong)destination.Length > (ulong)memory.Length) return false;
            memory.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }
        SkippedIndexedDrawScanner.Scan(Read, 0x1000, 4, 0, 0,
            (count, type, address, packet) => draws.Add((count, type, address, packet)), issues.Add);
        Assert.Equal((12u, 1u, 0x400CUL, 0x1024UL), Assert.Single(draws));
        Assert.Empty(issues);
        Assert.Equal(unchanged, memory);
    }

    [Fact]
    public void SkippedBuffer_StopsAtRecursiveDepthLimit()
    {
        uint[] words = [PacketHeader.Make(4, PacketOpcode.IndirectBuffer), 0x1000, 0, 4];
        var memory = MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
        var issues = new List<string>();
        bool Read(ulong address, Span<byte> destination)
        {
            memory.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }
        SkippedIndexedDrawScanner.Scan(Read, 0x1000, 4, 0, 0,
            (_, _, _, _) => Assert.Fail("A recursive buffer has no draw."), issues.Add);
        Assert.Contains("range-or-depth-limit", issues);
    }
}

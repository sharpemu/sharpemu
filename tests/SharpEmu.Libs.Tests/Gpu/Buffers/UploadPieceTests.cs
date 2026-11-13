// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class UploadPieceTests
{
    [Fact]
    public void ACopyLargerThanAPieceIsSplitAcrossPieces()
    {
        var pieces = GuestBufferCache.SplitUploadPieces([new BufferCopy(0, 100, 5)], 2);

        Assert.Equal(3, pieces.Count);
        AssertCopies(pieces[0], (0, 100, 2));
        AssertCopies(pieces[1], (0, 102, 2));
        AssertCopies(pieces[2], (0, 104, 1));
    }

    [Fact]
    public void CopiesThatCrossAPieceBoundaryRestartTheirSourceOffsets()
    {
        var pieces = GuestBufferCache.SplitUploadPieces([new BufferCopy(0, 0, 3), new BufferCopy(3, 10, 3)], 4);

        Assert.Equal(2, pieces.Count);
        AssertCopies(pieces[0], (0, 0, 3), (3, 10, 1));
        AssertCopies(pieces[1], (0, 11, 2));
    }

    [Fact]
    public void AnExactMultipleLeavesNoEmptyPiece()
    {
        var pieces = GuestBufferCache.SplitUploadPieces([new BufferCopy(0, 8, 4)], 2);

        Assert.Equal(2, pieces.Count);
        AssertCopies(pieces[0], (0, 8, 2));
        AssertCopies(pieces[1], (0, 10, 2));
    }

    private static void AssertCopies(List<BufferCopy> piece, params (ulong Source, ulong Destination, ulong Size)[] expected)
    {
        Assert.Equal(expected.Length, piece.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Source, piece[index].SrcOffset);
            Assert.Equal(expected[index].Destination, piece[index].DstOffset);
            Assert.Equal(expected[index].Size, piece[index].Size);
        }
    }
}

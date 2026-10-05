// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class BufferDownloadBatchPlannerTests
{
    [Theory]
    [InlineData(0ul, 1ul, 0ul, 4ul)]
    [InlineData(1ul, 1ul, 0ul, 4ul)]
    [InlineData(2ul, 3ul, 0ul, 8ul)]
    [InlineData(3ul, 61ul, 0ul, 64ul)]
    [InlineData(4ul, 64ul, 4ul, 64ul)]
    [InlineData(7ul, 65ul, 4ul, 68ul)]
    public void PlacementSeparatesTransferEnvelopeFromGuestBytes(ulong sourceOffset, ulong size, ulong alignedSource, ulong transferSize)
    {
        var planner = new BufferDownloadBatchPlanner(256);
        var placement = planner.Append(sourceOffset, size, 256);
        Assert.Equal(new BufferDownloadPlacement(alignedSource, transferSize, 0, sourceOffset - alignedSource, size), placement);
        Assert.Equal(transferSize <= 64 ? 64ul : 128ul, planner.PackedSize);
        Assert.False(planner.IsFull);
    }

    [Fact]
    public void ConsecutivePiecesKeepTheirOwnPaddingAndDataOffset()
    {
        var planner = new BufferDownloadBatchPlanner(192);
        Assert.Equal(new BufferDownloadPlacement(0, 4, 0, 1, 3), planner.Append(1, 3, 128));
        Assert.Equal(new BufferDownloadPlacement(4, 8, 64, 66, 5), planner.Append(6, 5, 128));
        Assert.Equal(new BufferDownloadPlacement(12, 64, 128, 131, 61), planner.Append(15, 100, 128));
        Assert.True(planner.IsFull);
        planner.Reset();
        Assert.Equal(0ul, planner.PackedSize);
        Assert.Equal(new BufferDownloadPlacement(76, 40, 0, 0, 39), planner.Append(76, 39, 128));
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(2ul)]
    [InlineData(3ul)]
    public void SplitRangesCoverEachGuestByteOnce(ulong sourceOffset)
    {
        for (ulong size = 1; size <= 513; size++)
        {
            var planner = new BufferDownloadBatchPlanner(128);
            var position = sourceOffset;
            var remaining = size;
            var transferred = 0ul;
            while (remaining != 0)
            {
                var destination = planner.PackedSize;
                var placement = planner.Append(position, remaining, 1024);
                Assert.Equal(destination, placement.DestinationOffset);
                Assert.Equal(0ul, placement.SourceOffset % 4);
                Assert.Equal(0ul, placement.TransferSize % 4);
                Assert.Equal(0ul, placement.DestinationOffset % 64);
                Assert.Equal(position, placement.SourceOffset + placement.DataOffset - placement.DestinationOffset);
                Assert.InRange(placement.DataSize, 1ul, remaining);
                Assert.True(placement.DataOffset + placement.DataSize <= placement.DestinationOffset + placement.TransferSize);
                Assert.True(placement.DestinationOffset + placement.TransferSize <= planner.PackedSize);
                Assert.InRange(planner.PackedSize, 64ul, 128ul);
                position += placement.DataSize;
                remaining -= placement.DataSize;
                transferred += placement.DataSize;
                if (remaining != 0) Assert.True(planner.IsFull);
                if (planner.IsFull) planner.Reset();
            }
            Assert.Equal(size, transferred);
            Assert.Equal(sourceOffset + size, position);
        }
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(63ul)]
    [InlineData(65ul)]
    public void InvalidCapacityIsRejected(ulong capacity)
    {
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => new BufferDownloadBatchPlanner(capacity));
    }

    [Theory]
    [InlineData(0ul, 0ul, 64ul)]
    [InlineData(65ul, 1ul, 64ul)]
    [InlineData(63ul, 2ul, 63ul)]
    [InlineData(0ul, 3ul, 3ul)]
    [InlineData(ulong.MaxValue - 2, 1ul, ulong.MaxValue)]
    public void InvalidRangeDoesNotChangeTheBatch(ulong sourceOffset, ulong size, ulong bufferSize)
    {
        using var fatal = new FatalScope();
        var planner = new BufferDownloadBatchPlanner(64);
        Assert.Throws<SchedulerFatalException>(() => planner.Append(sourceOffset, size, bufferSize));
        Assert.Equal(0ul, planner.PackedSize);
    }

    [Fact]
    public void FullBatchRequiresResetBeforeAnotherPiece()
    {
        using var fatal = new FatalScope();
        var planner = new BufferDownloadBatchPlanner(64);
        planner.Append(0, 64, 128);
        Assert.Throws<SchedulerFatalException>(() => planner.Append(64, 4, 128));
        Assert.Equal(64ul, planner.PackedSize);
        planner.Reset();
        Assert.Equal(new BufferDownloadPlacement(64, 4, 0, 0, 4), planner.Append(64, 4, 128));
    }
}

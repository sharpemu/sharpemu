// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Completion (counters, events) and presentation (the frame is done) are independent
// responsibilities; the request leaves the table only after both, in either order.
public sealed class VideoOutFlipRequestTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;
    private readonly int _handle;

    public VideoOutFlipRequestTests()
    {
        _context = new CpuContext(_memory, Generation.Gen5);
        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = 0;
        _context[CpuRegister.Rcx] = 0;
        _handle = VideoOutExports.VideoOutOpen(_context);
        Assert.True(_handle > 0);
    }

    public void Dispose()
    {
        VideoOutExports.CancelOutstandingFlips();
        _context[CpuRegister.Rdi] = unchecked((ulong)_handle);
        _ = VideoOutExports.VideoOutClose(_context);
    }

    private ulong Reserve(long flipArg)
    {
        Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(_handle, -1, 0, flipArg, gpuQueued: true, out var requestId));
        return requestId;
    }

    private ulong FlipCount()
    {
        _context[CpuRegister.Rdi] = unchecked((ulong)_handle);
        _context[CpuRegister.Rsi] = StatusAddress;
        Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(_context));
        Span<byte> status = stackalloc byte[8];
        Assert.True(_memory.TryRead(StatusAddress, status));
        return BinaryPrimitives.ReadUInt64LittleEndian(status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RefreshEligibility_UsesDisplayIntervalsWithoutAccumulatingSubmissionDebt(int flipRate)
    {
        var openedAt = Stopwatch.Frequency;
        var interval = Stopwatch.Frequency / 60 * (flipRate + 1);
        Assert.True(VideoOutExports.IsRefreshAvailable(openedAt, -1, openedAt, 60, flipRate));
        Assert.False(VideoOutExports.IsRefreshAvailable(openedAt, openedAt, openedAt + interval - 1, 60, flipRate));
        Assert.True(VideoOutExports.IsRefreshAvailable(openedAt, openedAt, openedAt + interval, 60, flipRate));

        var latePresentation = openedAt + 100 * interval + interval / 2;
        Assert.True(VideoOutExports.IsRefreshAvailable(openedAt, openedAt, latePresentation, 60, flipRate));
        Assert.False(VideoOutExports.IsRefreshAvailable(openedAt, latePresentation, latePresentation + 1, 60, flipRate));
        Assert.True(VideoOutExports.IsRefreshAvailable(openedAt, latePresentation, openedAt + 101 * interval, 60, flipRate));
    }

    [Fact]
    public void FastSubmissionAndDiscard_DoNotDelayTheFirstPresentation()
    {
        for (var index = 0; index < 1000; index++)
        {
            var request = Reserve(index);
            Assert.True(VideoOutExports.CanPresentFlip(request, Stopwatch.GetTimestamp()));
            VideoOutExports.DiscardFlip(request);
            VideoOutExports.CompleteFlip(request);
            Assert.False(VideoOutExports.CanPresentFlip(request, Stopwatch.GetTimestamp()));
        }
        var pending = Reserve(1000);
        Assert.True(VideoOutExports.CanPresentFlip(pending, Stopwatch.GetTimestamp()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosePort_CancelsPendingPresentationAndIgnoresLateCompletion(bool completed)
    {
        var request = Reserve(123);
        if (completed)
        {
            VideoOutExports.CompleteFlip(request);
        }
        Assert.True(VideoOutExports.IsFlipPresentationPending(request));
        _context[CpuRegister.Rdi] = (ulong)_handle;
        Assert.Equal(0, VideoOutExports.VideoOutClose(_context));
        Assert.False(VideoOutExports.IsFlipPresentationPending(request));
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(request));
        VideoOutExports.CompleteFlip(request);
        VideoOutExports.MarkFlipPresented(request);
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(request));
    }

    [Fact]
    public void CompleteThenPresent_CountsTheFlipAndReleasesTheBuffer()
    {
        var before = FlipCount();
        var request = Reserve(11);
        Assert.False(VideoOutExports.IsFlipDone(_handle, -1));

        VideoOutExports.CompleteFlip(request);
        Assert.Equal(before + 1, FlipCount());
        Assert.False(VideoOutExports.IsFlipDone(_handle, -1));
        Assert.Equal(VideoOutExports.FlipOutcome.Pending, VideoOutExports.GetFlipOutcomeForTests(request));

        VideoOutExports.MarkFlipPresented(request);
        Assert.True(VideoOutExports.IsFlipDone(_handle, -1));
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(request));
    }

    // Update completion counters even if the presenter has already finished the frame.
    [Fact]
    public void PresentThenComplete_StillCountsTheFlip()
    {
        var before = FlipCount();
        var request = Reserve(12);

        VideoOutExports.MarkFlipPresented(request);
        Assert.True(VideoOutExports.IsFlipDone(_handle, -1));
        Assert.Equal(VideoOutExports.FlipOutcome.Presented, VideoOutExports.GetFlipOutcomeForTests(request));
        Assert.Equal(before, FlipCount());

        VideoOutExports.CompleteFlip(request);
        Assert.Equal(before + 1, FlipCount());
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(request));
    }

    // A discarded frame releases the buffer but its retirement is not suppressed.
    [Fact]
    public void DiscardThenComplete_ReleasesTheBufferAndStillCountsTheFlip()
    {
        var before = FlipCount();
        var discardedBefore = VideoOutExports.DiscardedFlipCount;
        var request = Reserve(13);

        VideoOutExports.DiscardFlip(request);
        Assert.True(VideoOutExports.IsFlipDone(_handle, -1));
        Assert.Equal(VideoOutExports.FlipOutcome.Discarded, VideoOutExports.GetFlipOutcomeForTests(request));
        Assert.Equal(discardedBefore + 1, VideoOutExports.DiscardedFlipCount);

        VideoOutExports.CompleteFlip(request);
        Assert.Equal(before + 1, FlipCount());
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(request));

        // A second outcome does not overwrite the first.
        var presented = Reserve(14);
        VideoOutExports.MarkFlipPresented(presented);
        VideoOutExports.DiscardFlip(presented);
        Assert.Equal(discardedBefore + 1, VideoOutExports.DiscardedFlipCount);
        Assert.Equal(VideoOutExports.FlipOutcome.Presented, VideoOutExports.GetFlipOutcomeForTests(presented));
    }

    [Fact]
    public void Cancel_ReleasesPendingFlipsAndKeepsCompletedOnesOutOfTheTable()
    {
        var pending = Reserve(15);
        var completed = Reserve(16);
        VideoOutExports.CompleteFlip(completed);
        Assert.False(VideoOutExports.IsFlipDone(_handle, -1));

        VideoOutExports.CancelOutstandingFlips();

        Assert.True(VideoOutExports.IsFlipDone(_handle, -1));
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(pending));
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(completed));
    }

    [Fact]
    public void NoBufferFlipWaitsForRetirementBeforeCountingCompletion()
    {
        var before = FlipCount();
        var request = Reserve(17);
        Assert.True(VideoOutExports.ReleaseNoBufferFlip(-1, request));
        Assert.True(VideoOutExports.IsFlipDone(_handle, -1));
        Assert.Equal(before, FlipCount());
        VideoOutExports.CompleteFlip(request);
        Assert.Equal(before + 1, FlipCount());
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(request));
    }

    [Fact]
    public void AbandonedReservationDoesNotPublishCompletion()
    {
        var before = FlipCount();
        var request = Reserve(18);
        VideoOutExports.CancelFlip(request);
        VideoOutExports.CancelFlip(request);
        VideoOutExports.CompleteFlip(request);
        Assert.Equal(before, FlipCount());
        Assert.True(VideoOutExports.IsFlipDone(_handle, -1));
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(request));
    }
}

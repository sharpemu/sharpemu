// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Completion (counters, events) and presentation (the frame is done) are independent
// responsibilities; the request leaves the table only after both, in either order.
[Collection(SchedulingStateCollection.Name)]
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
        _context[CpuRegister.Rdi] = unchecked((ulong)_handle);
        _ = VideoOutExports.VideoOutClose(_context);
    }

    [Fact]
    public void CleanupPreservesAnotherPortsPendingFlip()
    {
        var request = Reserve(42);
        using (var otherPort = new VideoOutFlipRequestTests())
        {
            otherPort.Reserve(43);
        }

        Assert.Equal(VideoOutExports.FlipOutcome.Pending, VideoOutExports.GetFlipOutcomeForTests(request));
        var before = FlipCount();
        VideoOutExports.CompleteFlip(request);
        Assert.Equal(before + 1, FlipCount());
        VideoOutExports.MarkFlipPresented(request);
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
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    public void PresentationEligibilityUsesTheRequestedFlipMode(int flipMode, bool immediatelyEligible)
    {
        Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(_handle, -1, flipMode, 21, true, out var request));
        // Fix the previous presentation time to avoid a wall-clock boundary race.
        var ports = (System.Collections.IDictionary)typeof(VideoOutExports)
            .GetField("_ports", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var port = ports[_handle]!;
        var timestamp = Stopwatch.GetTimestamp();
        port.GetType().GetField("LastPresentationTimestamp")!.SetValue(port, timestamp);
        Assert.Equal(immediatelyEligible, VideoOutExports.CanPresentFlip(request, timestamp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MultipleGpuFlipsWaitForReadinessAndShareOneVblank(int flipRate)
    {
        _context[CpuRegister.Rdi] = (ulong)_handle;
        _context[CpuRegister.Rsi] = (ulong)flipRate;
        Assert.Equal(0, VideoOutExports.VideoOutSetFlipRate(_context));
        Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(_handle, -1, 4, 31, true, out var firstRequest));
        Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(_handle, -1, 4, 32, true, out var secondRequest));

        var ports = (System.Collections.IDictionary)typeof(VideoOutExports)
            .GetField("_ports", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var port = ports[_handle]!;
        var openedAt = (long)port.GetType().GetField("OpenTimestamp")!.GetValue(port)!;
        var refreshInterval = VideoOutDisplayClock.RefreshInterval(60);
        var readyAt = openedAt + refreshInterval / 2;
        var boundary = openedAt + refreshInterval;
        Assert.False(VideoOutExports.CanPresentFlip(firstRequest, boundary + refreshInterval));
        Assert.False(VideoOutExports.CanPresentFlip(secondRequest, boundary + refreshInterval));

        var requests = (System.Collections.IDictionary)typeof(VideoOutExports)
            .GetField("_flipRequests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        foreach (var requestId in new[] { firstRequest, secondRequest })
        {
            var beforeCompletion = Stopwatch.GetTimestamp();
            VideoOutExports.CompleteFlip(requestId);
            var request = requests[requestId]!;
            var readyTimestampField = request.GetType().GetField("ReadyTimestamp")!;
            var completionTimestamp = Assert.IsType<long>(readyTimestampField.GetValue(request));
            Assert.InRange(completionTimestamp, beforeCompletion, Stopwatch.GetTimestamp());
            // Fixed readiness avoids a wall-clock race between the two completion calls.
            readyTimestampField.SetValue(request, (long?)readyAt);
            VideoOutExports.CompleteFlip(requestId);
            Assert.Equal(readyAt, Assert.IsType<long>(readyTimestampField.GetValue(request)));
            Assert.False(VideoOutExports.CanPresentFlip(requestId, boundary - 1));
            Assert.True(VideoOutExports.CanPresentFlip(requestId, boundary));
        }

        VideoOutExports.MarkFlipPresented(firstRequest);
        Assert.True(VideoOutExports.CanPresentFlip(secondRequest, boundary));
        Assert.True(VideoOutExports.CanPresentFlip(secondRequest, boundary + 10 * refreshInterval));
        _context[CpuRegister.Rdi] = (ulong)_handle;
        Assert.Equal(0, VideoOutExports.VideoOutClose(_context));
        Assert.False(VideoOutExports.CanPresentFlip(secondRequest, boundary));
    }

    [Fact]
    public async Task MultipleCpuFlipWaitReachesTheBoundaryWithoutMovingItsDeadline()
    {
        var ports = (System.Collections.IDictionary)typeof(VideoOutExports)
            .GetField("_ports", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var port = ports[_handle]!;
        var openedAt = (long)port.GetType().GetField("OpenTimestamp")!.GetValue(port)!;
        var refreshInterval = VideoOutDisplayClock.RefreshInterval(60);
        var beforeWait = Stopwatch.GetTimestamp();
        var firstBoundary = openedAt + ((beforeWait - openedAt) / refreshInterval + 1) * refreshInterval;
        var paceFlip = typeof(VideoOutExports).GetMethod("PaceFlip",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var completion = Task.Run(() => (bool)paceFlip.Invoke(null, new object[] { _handle, 4 })!);
        try
        {
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(Stopwatch.GetTimestamp() >= firstBoundary);
        }
        finally
        {
            if (!completion.IsCompleted)
            {
                _context[CpuRegister.Rdi] = (ulong)_handle;
                VideoOutExports.VideoOutClose(_context);
                await completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public void VblankStatusContainsTheLastEventProcessCounter()
    {
        _context[CpuRegister.Rdi] = (ulong)_handle;
        Assert.Equal(0, VideoOutExports.VideoOutWaitVblank(_context));
        _context[CpuRegister.Rsi] = StatusAddress;
        Assert.Equal(0, VideoOutExports.VideoOutGetVblankStatus(_context));
        Span<byte> status = stackalloc byte[0x28];
        Assert.True(_memory.TryRead(StatusAddress, status));
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(status) >= 1);
        var processCounter = BinaryPrimitives.ReadUInt64LittleEndian(status[0x18..]);
        Assert.NotEqual(0UL, processCounter);
        Assert.Equal((ulong)((UInt128)processCounter * 1_000_000 / (ulong)Stopwatch.Frequency),
            BinaryPrimitives.ReadUInt64LittleEndian(status[0x08..]));
        Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(status[0x10..]));
    }

    [Fact]
    public void VblankEventsAndWaitsUseTheSameDisplayCount()
    {
        _context[CpuRegister.Rdi] = StatusAddress;
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelEventQueueCompatExports.KernelCreateEqueue(_context));
        var bytes = new byte[8];
        Assert.True(_memory.TryRead(StatusAddress, bytes));
        var queue = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        try
        {
            _context[CpuRegister.Rdi] = queue;
            _context[CpuRegister.Rsi] = (ulong)_handle;
            _context[CpuRegister.Rdx] = 0;
            Assert.Equal(0, VideoOutExports.VideoOutAddVblankEvent(_context));
            _context[CpuRegister.Rdi] = (ulong)_handle;
            Assert.Equal(0, VideoOutExports.VideoOutWaitVblank(_context));
            var timeout = BitConverter.GetBytes(1_000_000u);
            Assert.True(_memory.TryWrite(MemoryBase + 0x400, timeout));
            _context[CpuRegister.Rdi] = queue;
            _context[CpuRegister.Rsi] = MemoryBase + 0x200;
            _context[CpuRegister.Rdx] = 1;
            _context[CpuRegister.Rcx] = MemoryBase + 0x300;
            _context[CpuRegister.R8] = MemoryBase + 0x400;
            Assert.Equal(0, KernelEventQueueCompatExports.KernelWaitEqueue(_context));
            Assert.True(_memory.TryRead(MemoryBase + 0x210, bytes));
            var eventCount = BinaryPrimitives.ReadUInt64LittleEndian(bytes) >> 16;
            Assert.True(eventCount >= 1);

            _context[CpuRegister.Rdi] = (ulong)_handle;
            _context[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetVblankStatus(_context));
            Assert.True(_memory.TryRead(StatusAddress, bytes));
            var statusCount = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            Assert.True(statusCount >= eventCount);
            var ports = (System.Collections.IDictionary)typeof(VideoOutExports)
                .GetField("_ports", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var port = ports[_handle]!;
            var openedAt = (long)port.GetType().GetField("OpenTimestamp")!.GetValue(port)!;
            var elapsedCount = (ulong)((Stopwatch.GetTimestamp() - openedAt) / VideoOutDisplayClock.RefreshInterval(60));
            Assert.True(statusCount <= elapsedCount);
        }
        finally
        {
            _context[CpuRegister.Rdi] = queue;
            _context[CpuRegister.Rsi] = (ulong)_handle;
            Assert.Equal(0, VideoOutExports.VideoOutDeleteVblankEvent(_context));
            _context[CpuRegister.Rdi] = queue;
            Assert.Equal(0, KernelEventQueueCompatExports.KernelDeleteEqueue(_context));
        }
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

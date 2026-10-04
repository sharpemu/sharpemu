// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Media;
using Xunit;
using static SharpEmu.Libs.AvPlayer.AvPlayerExports;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerFrameReuseTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(16)]
    public void FramesKeepTheirContentsAndReuseBoundedStorage(int capacity)
    {
        const int frameCount = 600;
        const int frameByteCount = 384;
        using var source = new PatternStream(frameByteCount, frameCount);
        using var queue = new VideoFrameQueue(source, frameByteCount, capacity);
        var storage = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        byte[]? lastFrame = null;
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            lastFrame = ReadReadyFrame(queue);
            Assert.Equal(frameByteCount, lastFrame.Length);
            Assert.All(lastFrame, value => Assert.Equal(unchecked((byte)frameIndex), value));
            storage.Add(lastFrame);
        }

        WaitForWorker(queue);
        Assert.Equal(VideoFrameReadResult.End, queue.TryRead(out _));
        Assert.All(lastFrame!, value => Assert.Equal(unchecked((byte)(frameCount - 1)), value));
        Assert.InRange(storage.Count, 2, capacity + 3);
    }

    [Fact]
    public void PendingReadDoesNotRecycleTheCurrentFrame()
    {
        using var source = new PatternStream(384, 3, pauseBeforeFrame: 1);
        using var queue = new VideoFrameQueue(source, 384, 2);
        var firstFrame = ReadReadyFrame(queue);
        Assert.True(source.Paused.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(VideoFrameReadResult.Pending, queue.TryRead(out var pendingFrame));
        Assert.Null(pendingFrame);
        source.Resume.Set();
        WaitForWorker(queue);
        Assert.All(firstFrame, value => Assert.Equal((byte)0, value));
        Assert.All(ReadReadyFrame(queue), value => Assert.Equal((byte)1, value));
        Assert.All(ReadReadyFrame(queue), value => Assert.Equal((byte)2, value));
    }

    [Fact]
    public void DisposingAFullQueueStopsTheWorkerAndPreservesTheDeliveredFrame()
    {
        using var source = new PatternStream(384, 100);
        using var queue = new VideoFrameQueue(source, 384, 2);
        var firstFrame = ReadReadyFrame(queue);
        Assert.True(SpinWait.SpinUntil(() => source.CompletedFrames >= 4, TimeSpan.FromSeconds(5)));
        queue.Dispose();
        queue.Dispose();
        WaitForWorker(queue);

        using var replacementSource = new PatternStream(384, 2);
        using var replacementQueue = new VideoFrameQueue(replacementSource, 384, 2);
        Assert.NotSame(firstFrame, ReadReadyFrame(replacementQueue));
        Assert.NotSame(firstFrame, ReadReadyFrame(replacementQueue));
        Assert.All(firstFrame, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void IncompleteFinalFrameIsNotPublished()
    {
        using var source = new MemoryStream(new byte[384 + 17]);
        using var queue = new VideoFrameQueue(source, 384, 2);
        WaitForWorker(queue);
        Assert.Equal(384, ReadReadyFrame(queue).Length);
        Assert.Equal(VideoFrameReadResult.End, queue.TryRead(out _));
    }

    [Fact]
    public void LateTimestampedReadSkipsBoundedStaleFramesAndRetainsFutureFrame()
    {
        using var source = new TimestampedPatternStream(384, [0, 40, 80, 120, 160]);
        using var queue = new VideoFrameQueue(source, 384, 6);
        WaitForWorker(queue);

        Assert.Equal(
            VideoFrameReadResult.Ready,
            queue.TryReadLatestAtOrBefore(
                100,
                maximumFramesToSkip: 2,
                out var frame,
                out var timestamp,
                out var skipped));
        Assert.Equal(2, skipped);
        Assert.Equal(80UL, timestamp);
        Assert.All(Assert.IsType<byte[]>(frame), value => Assert.Equal((byte)2, value));

        Assert.Equal(
            VideoFrameReadResult.Pending,
            queue.TryReadLatestAtOrBefore(
                100,
                maximumFramesToSkip: 2,
                out frame,
                out timestamp,
                out skipped));
        Assert.Null(frame);
        Assert.Null(timestamp);
        Assert.Equal(0, skipped);

        Assert.Equal(
            VideoFrameReadResult.Ready,
            queue.TryReadLatestAtOrBefore(
                120,
                maximumFramesToSkip: 2,
                out frame,
                out timestamp,
                out skipped));
        Assert.Equal(0, skipped);
        Assert.Equal(120UL, timestamp);
        Assert.All(Assert.IsType<byte[]>(frame), value => Assert.Equal((byte)3, value));
    }

    [Fact]
    public void TimestampedReadPreservesSequentialFramesWhenConsumerIsCurrent()
    {
        using var source = new TimestampedPatternStream(384, [0, 40, 80]);
        using var queue = new VideoFrameQueue(source, 384, 4);
        WaitForWorker(queue);

        for (var frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            Assert.Equal(
                VideoFrameReadResult.Ready,
                queue.TryReadLatestAtOrBefore(
                    checked((ulong)(frameIndex * 40)),
                    maximumFramesToSkip: 2,
                    out var frame,
                    out var timestamp,
                    out var skipped));
            Assert.Equal(0, skipped);
            Assert.Equal(checked((ulong)(frameIndex * 40)), timestamp);
            Assert.All(Assert.IsType<byte[]>(frame), value =>
                Assert.Equal(checked((byte)frameIndex), value));
        }

        Assert.Equal(VideoFrameReadResult.End, queue.TryReadLatestAtOrBefore(
            120,
            maximumFramesToSkip: 2,
            out _,
            out _,
            out _));
    }

    private static byte[] ReadReadyFrame(VideoFrameQueue queue)
    {
        byte[]? frame = null;
        var result = VideoFrameReadResult.Pending;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            result = queue.TryRead(out frame);
            return result != VideoFrameReadResult.Pending;
        }, TimeSpan.FromSeconds(5)), "The decoder did not supply a frame.");
        Assert.Equal(VideoFrameReadResult.Ready, result);
        return Assert.IsType<byte[]>(frame);
    }

    private static void WaitForWorker(VideoFrameQueue queue)
    {
        var worker = (Thread)typeof(VideoFrameQueue)
            .GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!;
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "The decoder worker did not finish.");
    }

    private sealed class PatternStream(int frameByteCount, int frameCount, int pauseBeforeFrame = -1) : Stream
    {
        private int _position;
        private int _completedFrames;
        public ManualResetEventSlim Paused { get; } = new();
        public ManualResetEventSlim Resume { get; } = new();
        public int CompletedFrames => Volatile.Read(ref _completedFrames);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => (long)frameByteCount * frameCount;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position == Length)
            {
                return 0;
            }
            var frameIndex = _position / frameByteCount;
            if (frameIndex == pauseBeforeFrame)
            {
                Paused.Set();
                Resume.Wait();
            }
            var byteCount = Math.Min(Math.Min(count, 37), frameByteCount - _position % frameByteCount);
            buffer.AsSpan(offset, byteCount).Fill(unchecked((byte)frameIndex));
            _position += byteCount;
            Volatile.Write(ref _completedFrames, _position / frameByteCount);
            return byteCount;
        }

        protected override void Dispose(bool disposing)
        {
            Resume.Set();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TimestampedPatternStream(
        int frameByteCount,
        IReadOnlyList<ulong> timestamps) : Stream, IVideoFrameSource
    {
        private int _frameIndex;

        public bool TryReadVideoFrame(byte[] buffer, out ulong? timestampMilliseconds)
        {
            if (_frameIndex >= timestamps.Count)
            {
                timestampMilliseconds = null;
                return false;
            }

            buffer.AsSpan(0, frameByteCount).Fill(checked((byte)_frameIndex));
            timestampMilliseconds = timestamps[_frameIndex];
            _frameIndex++;
            return true;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => (long)frameByteCount * timestamps.Count;
        public override long Position { get => (long)_frameIndex * frameByteCount; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

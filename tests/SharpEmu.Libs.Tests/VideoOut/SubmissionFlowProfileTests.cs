// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Packets;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(CommandStreamQueueStateCollection.Name)]
public sealed class SubmissionFlowProfileTests
{
    [Fact]
    public void QueueTraceKeepsSubmissionIdentityAndProcessingOrder()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        var packet = StreamRunner.Packet(PacketOpcode.NumInstances, 3);
        host.WriteWords(StreamRunner.CommandAddress, packet);
        host.WriteWords(StreamRunner.DataAddress, packet);
        SubmissionFlowProfile.StartSession();
        queue.EnqueueGraphics(StreamRunner.CommandAddress, (uint)packet.Length, 7, null);
        queue.EnqueueCompute(0x20, StreamRunner.DataAddress, (uint)packet.Length, 8, null);
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(SliceResult.NoWork, queue.ProcessOne());
        Assert.Equal(new[] { "begin 0 7", "gc", "flush", "begin 1 8", "gc", "flush" }, host.Calls);
        using var output = new StringWriter();
        SubmissionFlowProfile.WriteTrace(output);
        var events = output.ToString().Split(Environment.NewLine)
            .Where(line => line.Contains("[PERF][SUBMISSION_FLOW]"))
            .ToArray();
        if (!RenderPhaseProfile.FrameTraceEnabled)
        {
            Assert.Empty(events);
            return;
        }
        Assert.Equal(4, events.Length);
        Assert.Contains("event=Enqueued queue=0 submission=7", events[0]);
        Assert.Contains("event=Enqueued queue=1 submission=8", events[1]);
        Assert.Contains("event=ProcessingStarted queue=0 submission=7", events[2]);
        Assert.Contains("event=ProcessingStarted queue=1 submission=8", events[3]);
    }

    [Fact]
    public void BufferRetainsNewestEventsAndCountsOverwrites()
    {
        var buffer = new SubmissionFlowProfile.EventBuffer(3);
        for (var sequence = 0; sequence < 8; sequence++)
            buffer.Record(CreateEvent(sequence));

        var snapshot = buffer.Close();
        Assert.Equal(8, snapshot.TotalEvents);
        Assert.Equal(new long[] { 5, 6, 7 }, snapshot.Events.Select(record => record.Timestamp));
        Assert.Equal(CreateEvent(5), snapshot.Events[0]);
        Assert.Empty(buffer.Close().Events);
        buffer.Record(CreateEvent(99));
        Assert.Empty(buffer.Close().Events);
        buffer.Reset();
        buffer.Record(CreateEvent(100));
        snapshot = buffer.Close();
        Assert.Equal(1, snapshot.TotalEvents);
        Assert.Equal(CreateEvent(100), Assert.Single(snapshot.Events));
    }

    [Fact]
    public void ConcurrentWritersPublishCompleteRecords()
    {
        var buffer = new SubmissionFlowProfile.EventBuffer(128);
        Parallel.For(0, 10000, sequence => buffer.Record(CreateEvent(sequence)));
        var snapshot = buffer.Close();
        Assert.Equal(10000, snapshot.TotalEvents);
        Assert.Equal(128, snapshot.Events.Length);
        Assert.Equal(128, snapshot.Events.Select(record => record.Timestamp).Distinct().Count());
        Assert.All(snapshot.Events, record => Assert.Equal(CreateEvent((int)record.Timestamp), record));
    }

    [Fact]
    public void SnapshotIsIndependentOfLaterWrites()
    {
        var buffer = new SubmissionFlowProfile.EventBuffer(1);
        buffer.Record(CreateEvent(1));
        var first = buffer.Close();
        buffer.Reset();
        buffer.Record(CreateEvent(2));
        Assert.Equal(CreateEvent(1), Assert.Single(first.Events));
        Assert.Equal(CreateEvent(2), Assert.Single(buffer.Close().Events));
    }

    [Fact]
    public void ConcurrentCloseKeepsCompleteRecords()
    {
        var buffer = new SubmissionFlowProfile.EventBuffer(10000);
        SubmissionFlowProfile.Snapshot snapshot = default;
        Parallel.Invoke(() => Parallel.For(0, 10000, index => buffer.Record(CreateEvent(index))),
            () => snapshot = buffer.Close());
        Assert.Equal(snapshot.TotalEvents, snapshot.Events.Length);
        Assert.All(snapshot.Events, record => Assert.Equal(CreateEvent((int)record.Timestamp), record));
        Assert.Empty(buffer.Close().Events);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0x20u, 1)]
    [InlineData(0x57u, 56)]
    public void ReportsMappedQueueAndGuestIdentityOnlyWithDetailedTracing(uint queue, int queueId)
    {
        SubmissionFlowProfile.StartSession();
        var previousGuest = GuestThreadExecution.EnterGuestThread(0x1234);
        try
        {
            SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.BackendEntered, queue, 7, 0x1000, 12);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousGuest);
        }
        using var output = new StringWriter();
        SubmissionFlowProfile.WriteTrace(output);
        if (RenderPhaseProfile.FrameTraceEnabled)
        {
            Assert.Contains($"event=BackendEntered queue={queueId} submission=7 address=0x1000 dwords=12", output.ToString());
            Assert.Contains("guest=0x1234", output.ToString());
        }
        else
            Assert.Equal(string.Empty, output.ToString());
        var report = output.ToString();
        SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.Enqueued);
        SubmissionFlowProfile.WriteTrace(output);
        Assert.Equal(report, output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedTraceOutputStillClosesTheSession(bool disposedOutput)
    {
        SubmissionFlowProfile.StartSession();
        SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.SubmitEntered);
        using var output = new FailingWriter(disposedOutput);
        SubmissionFlowProfile.WriteTrace(output);
        using var nextOutput = new StringWriter();
        SubmissionFlowProfile.WriteTrace(nextOutput);
        Assert.Equal(string.Empty, nextOutput.ToString());
        Assert.Equal(RenderPhaseProfile.FrameTraceEnabled, output.WriteAttempted);
    }

    private sealed class FailingWriter(bool disposedOutput) : StringWriter
    {
        internal bool WriteAttempted { get; private set; }
        public override void WriteLine(string? value)
        {
            WriteAttempted = true;
            if (disposedOutput) throw new ObjectDisposedException(nameof(FailingWriter));
            throw new IOException("The trace output is unavailable.");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BufferRequiresPositiveCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubmissionFlowProfile.EventBuffer(capacity));
    }

    private static SubmissionFlowProfile.TraceEvent CreateEvent(int sequence) => new(sequence, sequence + 1,
        SubmissionFlowProfile.EventKind.Enqueued, sequence + 2, (ulong)sequence + 3,
        (ulong)sequence + 4, (uint)sequence + 5, sequence + 6);
}

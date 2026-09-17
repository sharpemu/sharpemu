// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.VideoOut;
using System.Reflection;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

[CollectionDefinition("GuestThreadFlowProfile", DisableParallelization = true)]
public sealed class GuestThreadFlowProfileCollection;

[Collection("GuestThreadFlowProfile")]
public sealed class GuestThreadFlowProfileTests
{
    [Fact]
    public void NewSessionReopensAllClosedTraces()
    {
        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var backendType = typeof(DirectExecutionBackend);
        const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        var profile = Assert.IsType<GuestThreadFlowProfile>(backendType
            .GetField("_guestThreadFlowProfile", instancePrivate)!.GetValue(backend));
        profile.Record(CreateEvent(1));
        profile.Close();
        SemaphoreSignalProfile.Close();
        MutexHandoffProfile.Close();
        SubmissionFlowProfile.WriteTrace(TextWriter.Null);
        backendType.GetMethod("StartGuestFlowTraces", instancePrivate)!.Invoke(backend, null);
        profile.Record(CreateEvent(2));
        Assert.Equal(CreateEvent(2), Assert.Single(profile.Close().Events));
        SemaphoreSignalProfile.Begin(0x83, 1);
        MutexHandoffProfile.Record(1, "Granted", 0x100, 0x200);
        Assert.Equal(RenderPhaseProfile.FrameTraceEnabled ? 1 : 0, SemaphoreSignalProfile.Close().Events.Length);
        Assert.Equal(RenderPhaseProfile.FrameTraceEnabled ? 1 : 0, MutexHandoffProfile.Close().Events.Length);
        SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.SubmitEntered);
        using var submissionOutput = new StringWriter();
        SubmissionFlowProfile.WriteTrace(submissionOutput);
        Assert.Equal(RenderPhaseProfile.FrameTraceEnabled, submissionOutput.ToString().Contains("[PERF][SUBMISSION_FLOW_TRACE]"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraceOutputFailureDoesNotInterruptBackendCleanup(bool disposedOutput)
    {
        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var backendType = typeof(DirectExecutionBackend);
        const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        var profile = Assert.IsType<GuestThreadFlowProfile>(backendType
            .GetField("_guestThreadFlowProfile", instancePrivate)!.GetValue(backend));
        profile.Record(CreateEvent(1));
        using var output = new FailingTraceWriter(disposedOutput);
        backendType.GetMethod("WriteGuestFlowTraces", instancePrivate)!.Invoke(backend, [output]);
        Assert.Empty(profile.Close().Events);
    }

    private sealed class FailingTraceWriter(bool disposedOutput) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            if (disposedOutput) throw new ObjectDisposedException(nameof(FailingTraceWriter));
            throw new IOException("The trace output is unavailable.");
        }
    }

    [Fact]
    public void DetailedTraceDoesNotLetReadyThreadBypassActiveExecutor()
    {
        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var backendType = typeof(DirectExecutionBackend);
        const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        var threadType = backendType.GetNestedType("GuestThreadState", BindingFlags.NonPublic)!;
        var runStateType = backendType.GetNestedType("GuestThreadRunState", BindingFlags.NonPublic)!;
        var thread = Activator.CreateInstance(threadType)!;
        threadType.GetProperty("ThreadHandle")!.SetValue(thread, 0x1234UL);
        threadType.GetProperty("State")!.SetValue(thread, Enum.Parse(runStateType, "Ready"));
        threadType.GetProperty("ExecutorActive")!.SetValue(thread, true);
        var queue = backendType.GetField("_readyGuestThreads", instancePrivate)!.GetValue(backend)!;
        queue.GetType().GetMethod("Enqueue")!.Invoke(queue, [thread]);
        backendType.GetField("_readyGuestThreadCount", instancePrivate)!.SetValue(backend, 1);
        var claim = backendType.GetMethod("TryClaimReadyGuestThreadLocked", instancePrivate)!;
        object?[] claimArguments = [null];
        Assert.False((bool)claim.Invoke(backend, claimArguments)!);
        Assert.Null(claimArguments[0]);
        Assert.Equal(1, backendType.GetField("_readyGuestThreadCount", instancePrivate)!.GetValue(backend));
        Assert.Equal("Ready", threadType.GetProperty("State")!.GetValue(thread)!.ToString());
        object?[] releaseArguments = [thread, null];
        lock (backendType.GetField("_guestThreadGate", instancePrivate)!.GetValue(backend)!)
            Assert.False((bool)backendType.GetMethod("TryReleaseGuestThreadExecutorLocked", instancePrivate)!
                .Invoke(backend, releaseArguments)!);
        Assert.True((bool)claim.Invoke(backend, claimArguments)!);
        Assert.Same(thread, claimArguments[0]);
        Assert.True((bool)threadType.GetProperty("ExecutorActive")!.GetValue(thread)!);
        var profile = (GuestThreadFlowProfile)backendType.GetField("_guestThreadFlowProfile", instancePrivate)!.GetValue(backend)!;
        var snapshot = profile.Close();
        if (GuestThreadFlowProfile.Enabled)
            Assert.Equal(new[] { GuestThreadFlowProfile.EventKind.ClaimDeferred,
                GuestThreadFlowProfile.EventKind.ExecutorReleased, GuestThreadFlowProfile.EventKind.Claimed },
                snapshot.Events.Select(item => item.Kind));
        else
            Assert.Empty(snapshot.Events);
    }

    [Fact]
    public void ShutdownRequestWritesTraceBeforeBackendDisposal()
    {
        var modules = new ModuleManager();
        modules.Freeze();
        using var backend = new DirectExecutionBackend(modules);
        var profile = Assert.IsType<GuestThreadFlowProfile>(typeof(DirectExecutionBackend)
            .GetField("_guestThreadFlowProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend));
        profile.Record(CreateEvent(1));
        var previousOutput = Console.Error;
        using var output = new StringWriter();
        try
        {
            SemaphoreSignalProfile.StartSession();
            SemaphoreSignalProfile.Begin(0x83, 1);
            MutexHandoffProfile.StartSession();
            MutexHandoffProfile.Record(1, "Granted", 0x100, 0x200);
            SubmissionFlowProfile.StartSession();
            SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.SubmitEntered);
            Console.SetError(output);
            backend.RequestHostShutdown("trace-test");
            Assert.Contains("[PERF][GUEST_FLOW_TRACE]", output.ToString());
            Assert.Contains("[PERF][GUEST_FLOW]", output.ToString());
            if (RenderPhaseProfile.FrameTraceEnabled)
                Assert.Contains("[PERF][SEMAPHORE_SIGNAL_TRACE]", output.ToString());
            backend.RequestHostShutdown("trace-test");
            Assert.Equal(1, output.ToString().Split("[PERF][GUEST_FLOW_TRACE]").Length - 1);
            Assert.Equal(RenderPhaseProfile.FrameTraceEnabled ? 1 : 0,
                output.ToString().Split("[PERF][SEMAPHORE_SIGNAL_TRACE]").Length - 1);
            Assert.Equal(RenderPhaseProfile.FrameTraceEnabled ? 1 : 0,
                output.ToString().Split("[PERF][MUTEX_HANDOFF_TRACE]").Length - 1);
            Assert.Equal(RenderPhaseProfile.FrameTraceEnabled ? 1 : 0,
                output.ToString().Split("[PERF][SUBMISSION_FLOW_TRACE]").Length - 1);
            Assert.Empty(MutexHandoffProfile.Close().Events);
            Assert.Empty(SemaphoreSignalProfile.Close().Events);
            Assert.Empty(profile.Close().Events);
        }
        finally
        {
            Console.SetError(previousOutput);
        }
    }

    [Fact]
    public void TraceFlushesTheWriterBeforeReturning()
    {
        var profile = new GuestThreadFlowProfile(1);
        profile.Record(CreateEvent(1));
        using var output = new FlushTrackingWriter();
        profile.WriteTrace(output);
        Assert.True(output.Flushed);
        Assert.Contains("[PERF][GUEST_FLOW]", output.ToString());
    }

    private sealed class FlushTrackingWriter : StringWriter
    {
        internal bool Flushed { get; private set; }
        public override void Flush() => Flushed = true;
    }

    [Fact]
    public void BufferRetainsNewestCompleteEventsAndCountsOverwrites()
    {
        var profile = new GuestThreadFlowProfile(3);
        for (var index = 0; index < 8; index++) profile.Record(CreateEvent(index));
        var snapshot = profile.Close();
        Assert.Equal(8, snapshot.TotalEvents);
        Assert.Equal(new[] { CreateEvent(5), CreateEvent(6), CreateEvent(7) }, snapshot.Events);
        profile.Record(CreateEvent(9));
        Assert.Empty(profile.Close().Events);
    }

    [Fact]
    public void ConcurrentWritersDoNotMixEventFields()
    {
        var profile = new GuestThreadFlowProfile(128);
        Parallel.For(0, 10000, index => profile.Record(CreateEvent(index)));
        var snapshot = profile.Close();
        Assert.Equal(10000, snapshot.TotalEvents);
        Assert.Equal(128, snapshot.Events.Length);
        Assert.Equal(128, snapshot.Events.Select(item => item.Timestamp).Distinct().Count());
        Assert.All(snapshot.Events, item => Assert.Equal(CreateEvent((int)item.Timestamp), item));
    }

    [Fact]
    public void TraceKeepsGuestIdentityWaitKeyAndArgumentsOnOneLine()
    {
        var profile = new GuestThreadFlowProfile(3);
        profile.Record(CreateEvent(1) with { Name = "worker'\r\nname", Kind = GuestThreadFlowProfile.EventKind.Blocked });
        profile.Record(CreateEvent(2) with { HostThreadId = 90, GuestThreadHandle = 4, Kind = GuestThreadFlowProfile.EventKind.Ready });
        using var output = new StringWriter();
        profile.WriteTrace(output);
        var text = output.ToString();
        Assert.Equal(3, text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("guest=0x4", text);
        Assert.Contains("name='worker\\'\\r\\nname'", text);
        Assert.Contains("wake='wait:1'", text);
        Assert.Contains("arg0=0x5 arg1=0x6 deadline=7", text);
        Assert.Contains("thread=90 guest=0x4", text);
        profile.WriteTrace(output);
        Assert.Equal(text, output.ToString());
    }

    [Fact]
    public void ConcurrentCloseCapturesOnlyCompleteRecords()
    {
        var profile = new GuestThreadFlowProfile(10000);
        GuestThreadFlowProfile.Snapshot snapshot = default;
        Parallel.Invoke(() => Parallel.For(0, 10000, index => profile.Record(CreateEvent(index))),
            () => snapshot = profile.Close());
        Assert.Equal(snapshot.TotalEvents, snapshot.Events.Length);
        Assert.All(snapshot.Events, item => Assert.Equal(CreateEvent((int)item.Timestamp), item));
        Assert.Empty(profile.Close().Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BufferRequiresPositiveCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GuestThreadFlowProfile(capacity));
    }

    private static GuestThreadFlowProfile.TraceEvent CreateEvent(int index) => new(index, index + 2,
        (ulong)index + 3, "worker", GuestThreadFlowProfile.EventKind.Blocked, "wait", $"wait:{index}",
        "import", (ulong)index + 4, (ulong)index + 5, index + 6);
}

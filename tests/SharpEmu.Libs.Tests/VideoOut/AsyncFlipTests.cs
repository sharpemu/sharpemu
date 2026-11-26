// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class AsyncFlipTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void PublicPendingCountWaitsForOrderedCapture()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        using var surface = new FlipSurface(stream);
        var cpu = new CpuContext(stream.Presenter.Harness.Memory, Generation.Gen5);
        cpu[CpuRegister.Rdi] = (ulong)stream.Handle;
        var beforeDrain = -1;
        stream.Run(() =>
        {
            Assert.True(stream.Host.Memory.TryWrite(surface.Address, new byte[] { 7, 0, 0, 0 }));
            var id = stream.Host.PrepareFlip(stream.Handle, -1, 0, 29);
            stream.Host.RecordEndOfPipe(new(EndOfPipeWriteKind.Flip, 1, FlipIndex: -1, FlipRequestId: id));
            stream.Host.Flush();
        }, _ => beforeDrain = VideoOutExports.VideoOutIsFlipPending(cpu));
        Assert.Equal(0, beforeDrain);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WaitForIdleIncludesDeferredBoundaryAcknowledgement(bool shutdown, bool failBeforeCapture)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var queue = new CommandStreamQueue(stream.Host, boundariesInFlight: 0);
        var guest = Task.Run(queue.Done);
        Assert.True(SpinWait.SpinUntil(() => queue.HasPending, 5000));
        var returnedBeforeCapture = false;
        var expected = IdleOutcome.Completed;
        using var enteringIdle = new ManualResetEventSlim();
        using var exitedIdle = new ManualResetEventSlim();
        Task<IdleOutcome>? idle = null;
        try
        {
            stream.Run(() =>
            {
                var id = stream.Host.PrepareFlip(stream.Handle, -1, 0, 7);
                Assert.Equal(SliceResult.Completed, queue.ProcessOne());
                stream.Host.RecordEndOfPipe(new(EndOfPipeWriteKind.Flip, 1, FlipIndex: -1, FlipRequestId: id));
            }, waiting =>
            {
                if (shutdown) queue.StopAccepting();
                idle = Task.Run(() =>
                {
                    enteringIdle.Set();
                    var result = shutdown ? queue.Done() : queue.WaitForIdle();
                    exitedIdle.Set();
                    return result;
                });
                Assert.True(enteringIdle.Wait(TimeSpan.FromSeconds(5)));
                returnedBeforeCapture = exitedIdle.Wait(TimeSpan.FromMilliseconds(150));
                if (failBeforeCapture && !waiting)
                {
                    expected = IdleOutcome.Failed;
                    queue.Fail();
                    Assert.True(exitedIdle.Wait(TimeSpan.FromSeconds(5)));
                }
            });
            Assert.Equal(expected, await guest.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(expected, await idle!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(returnedBeforeCapture);
        }
        finally { queue.Fail(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlipSnapshotsEventsAndSubmissionTimeAtCapture(bool orderedPublication)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        const ulong address = 0x100000000;
        const ulong userData = 0x1234;
        var memory = new FakeCpuMemory(address, 0x1000);
        var cpu = new CpuContext(memory, Generation.Gen5);
        cpu[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelEventQueueCompatExports.KernelCreateEqueue(cpu));
        Assert.True(cpu.TryReadUInt64(address, out var queue));
        try
        {
            var publications = stream.Presenter.GetField<PendingGuestPublications>("_guestPublications");
            var older = orderedPublication ? publications.AddOrdering(0) : null;
            const ulong status = address + 0x100;
            ulong ReadSubmitTime(uint pending)
            {
                cpu[CpuRegister.Rdi] = (ulong)stream.Handle;
                cpu[CpuRegister.Rsi] = status;
                Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(cpu));
                Assert.True(cpu.TryReadUInt64(status + 0x30, out var counts));
                Assert.Equal(((ulong)pending << 32) | pending, counts);
                Assert.True(cpu.TryReadUInt64(status + 0x40, out var time));
                return time;
            }

            var before = ReadSubmitTime(0);
            ulong registeredAt = 0;
            stream.Presenter.SetField("FlipCaptureStartedForTest", (Action)(() =>
                Assert.True(ReadSubmitTime(1) >= registeredAt)));
            stream.Run(() =>
            {
                stream.Interpreter.SetFlip(new(stream.Handle, -1, 0, 29));
                stream.Interpreter.SubmitFlip();
            }, waiting =>
            {
                Assert.Equal(orderedPublication, waiting);
                Assert.Equal(before, ReadSubmitTime(0));
                cpu[CpuRegister.Rdi] = queue;
                cpu[CpuRegister.Rsi] = (ulong)stream.Handle;
                cpu[CpuRegister.Rdx] = userData;
                Assert.Equal(0, VideoOutExports.VideoOutAddFlipEvent(cpu));
                registeredAt = KernelRuntimeCompatExports.ReadProcessTimeCounter();
                if (older is not null) publications.Publish(older, static () => { });
            });
            Assert.True(ReadSubmitTime(0) >= registeredAt);
            cpu[CpuRegister.Rdi] = queue;
            cpu[CpuRegister.Rsi] = address + 0x200;
            cpu[CpuRegister.Rdx] = 1;
            cpu[CpuRegister.Rcx] = address + 0x300;
            cpu[CpuRegister.R8] = address + 0x400; // Poll with a zero timeout.
            Assert.Equal(0, KernelEventQueueCompatExports.KernelWaitEqueue(cpu));
            Assert.True(cpu.TryReadUInt64(address + 0x200 + 24, out var delivered));
            Assert.Equal(userData, delivered);
        }
        finally
        {
            cpu[CpuRegister.Rdi] = queue;
            Assert.Equal(0, KernelEventQueueCompatExports.KernelDeleteEqueue(cpu));
        }
    }

    [Fact]
    public void FourReservationsPrecedeCaptureAndNextFrameDrawSurvivesTheDrain()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        using var surface = new FlipSurface(stream);
        var requests = new List<ulong>();

        var wait = StreamRunner.CustomPacket(PacketOpcode.Nop, PacketCustomCode.WaitFlipDone, (uint)stream.Handle, 0, 0, 0, 0, 0);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(wait.AsSpan()).ToArray();
        var cursor = new PacketCursorStack();
        SubmissionProgress Wait() => stream.Interpreter.Process(cursor, surface.Address, (uint)wait.Length, bytes);
        stream.Run(() =>
        {
            for (var frame = 0; frame < 4; frame++)
            {
                Assert.True(stream.Host.HasFlipSlot());
                stream.Host.DrawIndexed(1, default);
                Assert.True(stream.SawIndexedDraw);
                var id = stream.Host.PrepareFlip(0, stream.Handle, 0, 0, frame);
                Assert.False(stream.SawIndexedDraw);
                requests.Add(id);
                stream.Host.RecordEndOfPipe(new(EndOfPipeWriteKind.Flip, 1, FlipIndex: 0, FlipRequestId: id));
                stream.Host.Flush();
            }
            stream.Host.DrawIndexed(2, default);
            Assert.Equal(SubmissionProgress.Blocked, Wait());
        }, waiting =>
        {
            Assert.False(waiting);
            Assert.False(stream.Host.IsFlipDone(stream.Handle, 0));
            Assert.False(stream.Host.HasFlipSlot());
            Assert.Equal(4, requests.Count);
        });
        Assert.Equal(4, requests.Count);
        Assert.True(requests.Zip(requests.Skip(1)).All(pair => pair.First < pair.Second));
        Assert.True(stream.SawIndexedDraw);
        Assert.False(stream.Host.IsFlipDone(stream.Handle, 0));
        Assert.False(stream.Host.HasFlipSlot());
        foreach (var request in requests)
        {
            Assert.Equal(request, surface.Dequeue());
            VideoOutExports.MarkFlipPresented(request);
            Assert.True(stream.Host.HasFlipSlot());
        }
        Assert.Equal(SubmissionProgress.Complete, stream.Presenter.Run(Wait));
        Assert.True(stream.Host.HasFlipSlot());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FlipPacketsPublishLabelsBeforeInterruptAndFlush(int kind)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var memory = stream.Presenter.Harness;
        var label = memory.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        const uint value = 0x12345678;
        var interrupts = new List<uint>();
        stream.Interrupt = () => interrupts.Add(BitConverter.ToUInt32(memory.Read(label, 4)));
        stream.Run(() =>
        {
            stream.Interpreter.SetFlip(new(stream.Handle, -1, 0, 17));
            if (kind == 0) stream.Interpreter.SubmitFlip();
            else if (kind == 1) stream.Interpreter.SubmitFlipWithLabel(label, value);
            else stream.Interpreter.SubmitFlipWithInterrupt(4, 0x38, label, value);
        }, waiting =>
        {
            Assert.False(waiting);
            Assert.Equal(0u, BitConverter.ToUInt32(memory.Read(label, 4)));
        });
        Assert.Equal(kind == 0 ? 0u : value, BitConverter.ToUInt32(memory.Read(label, 4)));
        Assert.Equal(kind == 2 ? new[] { value } : [], interrupts);
        Assert.True(stream.Host.IsFlipDone(stream.Handle, -1));
    }

    [Fact]
    public void FailedCaptureCancelsAlsoTheLaterUncapturedRequest()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        // The slot passes admission but its surface is unavailable at capture.
        var slot = stream.Slot;
        slot.GetType().GetProperty("GroupIndex")!.SetValue(slot, 0);
        var baseline = VideoOutExports.PendingFlipRequestCount;
        ulong first = 0, second = 0;
        var failure = Record.Exception(() => stream.Run(() =>
        {
            first = stream.Host.PrepareFlip(stream.Handle, 0, 0, 1);
            second = stream.Host.PrepareFlip(stream.Handle, -1, 0, 2);
        }, waiting =>
        {
            Assert.False(waiting);
            Assert.False(stream.Host.IsFlipDone(stream.Handle, -1));
        }));

        Assert.Contains("display buffer that is not registered", Assert.IsType<SchedulerFatalException>(failure).Message);
        Assert.Equal(baseline, VideoOutExports.PendingFlipRequestCount);
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(first));
        Assert.Null(VideoOutExports.GetFlipOutcomeForTests(second));
        Assert.True(stream.Host.HasFlipSlot());
    }

    [Fact]
    public void RenderFailureBeforeCaptureCancelsTheOutstandingReservation()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);

        var baseline = VideoOutExports.PendingFlipRequestCount;
        var failure = Record.Exception(() => stream.Run(() =>
        {
            stream.Interpreter.SetFlip(new(stream.Handle, -1, 0, 19));
            stream.Interpreter.SubmitFlip();
        }, waiting =>
        {
            Assert.False(waiting);
            Assert.Equal(baseline + 1, VideoOutExports.PendingFlipRequestCount);
            stream.Presenter.InvokeMethod("AbortPendingFlipCaptures", new Exception("render failure before capture"));
            Assert.True(stream.Host.IsFlipDone(stream.Handle, -1));
        }));
        Assert.Equal("render failure before capture", Assert.IsType<InvalidOperationException>(failure).InnerException!.Message);
        Assert.Equal(baseline, VideoOutExports.PendingFlipRequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingPublicationAndRetainedReplayBothRendezvousBeforeParsingOn(bool pending)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var publications = stream.Presenter.GetField<PendingGuestPublications>("_guestPublications");
        var older = pending ? publications.AddOrdering(0) : null;
        if (older is not null) publications.Register(older, stream.Presenter.Harness.Scheduler.CurrentTick);
        stream.Run(() =>
        {
            if (!pending) Assert.True(stream.Translation.TryRetainTargetlessDraw(stream.Interpreter.TypedRegisters, default));
            stream.Interpreter.SetFlip(new(stream.Handle, -1, 0, 11));
            stream.Interpreter.SubmitFlip();
        }, waiting => Assert.True(waiting));
        if (older is not null) publications.Publish(older, static () => { });
        Assert.Equal(0, publications.Count);
        Assert.True(stream.Host.IsFlipDone(stream.Handle, -1));
    }

    [Fact]
    public async Task ParserPassesTheFrameBoundaryWhileGuestWaitsForCapture()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var queue = new CommandStreamQueue(stream.Host, boundariesInFlight: 0);
        var guest = Task.Run(queue.Done);
        Assert.True(SpinWait.SpinUntil(() => queue.HasPending, 5000));
        try
        {
            stream.Run(() =>
            {
                var request = stream.Host.PrepareFlip(stream.Handle, -1, 0, 7);
                Assert.Equal(SliceResult.Completed, queue.ProcessOne());
                stream.Host.RecordEndOfPipe(new(EndOfPipeWriteKind.Flip, 1, FlipIndex: -1, FlipRequestId: request));
            }, waiting =>
            {
                Assert.False(waiting);
                Assert.False(guest.IsCompleted);
            });
            Assert.Equal(IdleOutcome.Completed, await guest.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { queue.Fail(); }
    }

    [Fact]
    public void CpuFlipStillWaitsForItsRenderItem()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(stream.Handle, -1, 0, 5, false, out var request));
        stream.Run(() => stream.Host.PrepareCpuFlip(stream.Handle, -1, request), waiting =>
        {
            Assert.True(waiting);
            Assert.False(stream.Host.IsFlipDone(stream.Handle, -1));
        });
        Assert.True(stream.Host.IsFlipDone(stream.Handle, -1));
        VideoOutExports.CompleteFlip(request);
    }

    [Fact]
    public void OrderedPortChangeWaitsForAnAdmittedFlip()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var changed = false;
        stream.Run(() =>
        {
            var id = stream.Host.PrepareFlip(stream.Handle, -1, 0, 23);
            stream.Presenter.InvokeMethod("RunOrderedVideoOutChange", (Action)(() =>
            {
                Assert.True(stream.Host.IsFlipDone(stream.Handle, -1));
                changed = true;
            }));
            stream.Host.RecordEndOfPipe(new(EndOfPipeWriteKind.Flip, 1, FlipIndex: -1, FlipRequestId: id));
        }, waiting =>
        {
            Assert.True(waiting);
            Assert.False(changed);
            Assert.False(stream.Host.IsFlipDone(stream.Handle, -1));
        });
        Assert.True(changed);
    }

    // Register a small display surface without starting a window. Capture still uses
    // the real image cache, device allocation, barriers, Vulkan copy and presentation FIFO.
    private sealed class FlipSurface : IDisposable
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly FlipStream _stream;
        private readonly IDictionary _snapshots;
        private readonly object _queue = typeof(VulkanVideoPresenter).GetField("_pendingGuestImagePresentations", Static)!.GetValue(null)!;
        private readonly FieldInfo _latest = typeof(VulkanVideoPresenter).GetField("_latestPresentation", Static)!;
        private readonly object? _previousLatest;
        internal readonly ulong Address;

        internal FlipSurface(FlipStream stream)
        {
            _stream = stream;
            Assert.Empty((IEnumerable)_queue);
            _previousLatest = _latest.GetValue(null);
            Address = stream.Presenter.Harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
            var groups = (Array)stream.Port.GetType().GetProperty("Groups")!.GetValue(stream.Port)!;
            var group = Activator.CreateInstance(groups.GetType().GetElementType()!)!;
            var attribute = group.GetType().GetProperty("Attribute")!;
            attribute.SetValue(group, Activator.CreateInstance(attribute.PropertyType, [0x8000000022000000UL, 0u, 0u, 64u, 64u, 64u, 0UL]));
            groups.SetValue(group, 0);
            stream.Slot.GetType().GetProperty("GroupIndex")!.SetValue(stream.Slot, 0);
            stream.Slot.GetType().GetProperty("AddressLeft")!.SetValue(stream.Slot, Address);
            var versions = PresenterUnderTest.PresenterType.GetField("_guestImageVersions", PresenterUnderTest.InstanceMembers)!;
            _snapshots = (IDictionary)Activator.CreateInstance(versions.FieldType)!;
            stream.Presenter.SetField("_guestImageVersions", _snapshots);
            stream.Presenter.SetField("_flipSnapshotPoolGate", new object());
        }

        internal ulong Dequeue()
        {
            var frame = _queue.GetType().GetMethod("Dequeue")!.Invoke(_queue, null)!;
            Assert.Equal(Address, frame.GetType().GetProperty("GuestImageAddress")!.GetValue(frame));
            Assert.True(_snapshots.Contains(frame.GetType().GetProperty("GuestImageVersion")!.GetValue(frame)!));
            return (ulong)frame.GetType().GetProperty("FlipRequestId")!.GetValue(frame)!;
        }

        public void Dispose()
        {
            _queue.GetType().GetMethod("Clear")!.Invoke(_queue, null);
            _latest.SetValue(null, _previousLatest);
            _stream.Presenter.Run(() =>
            {
                _stream.Presenter.Harness.Scheduler.Finish();
                foreach (var snapshot in _snapshots.Values) _stream.Presenter.InvokeMethod("DestroyGuestImage", snapshot);
                _snapshots.Clear();
            });
        }
    }

    private sealed class FlipStream : IDisposable, IEndOfPipeSink
    {
        private static readonly Type ThreadType = typeof(VulkanVideoPresenter).GetNestedType("CommandThread", BindingFlags.NonPublic)!;
        internal readonly PresenterUnderTest Presenter;
        internal ICommandStreamHost Host => (ICommandStreamHost)Presenter.Instance;
        internal readonly GpuCommandInterpreter Interpreter;
        internal readonly AgcExports.CommandStreamTranslation Translation;
        private readonly CpuContext _context;
        private readonly FatalScope _fatal = new();
        internal readonly int Handle;
        internal object Port => ((IDictionary)typeof(VideoOutExports).GetField("_ports", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)[Handle]!;
        internal object Slot => ((Array)Port.GetType().GetProperty("BufferSlots")!.GetValue(Port)!).GetValue(0)!;
        internal Action? Interrupt;

        internal FlipStream(HeadlessVulkan vulkan)
        {
            Presenter = new(vulkan);
            _context = new CpuContext(Presenter.Harness.Memory, Generation.Gen5);
            Handle = VideoOutExports.VideoOutOpen(_context);
            Assert.True(Handle > 0);
            Interpreter = Presenter.GetField<CommandStreamQueue>("_commandStream").GetInterpreter(0);
            Translation = AgcExports.CreateCommandStreamTranslation(Host.Memory, Host);
            Presenter.SetField("_translation", Translation);
            Presenter.SetField("_endOfPipe", new EndOfPipe(this, (IFlipSubmitter)Presenter.Instance));
            Translation.BeginSubmission(0, 1, null, Interpreter);
        }

        internal bool SawIndexedDraw
        {
            get
            {
                var state = typeof(AgcExports.CommandStreamTranslation).GetField("_current", PresenterUnderTest.InstanceMembers)!.GetValue(Translation)!;
                return (bool)state.GetType().GetProperty("SawIndexedDraw")!.GetValue(state)!;
            }
        }

        public void TriggerInterrupt(int eventId, uint contextId) => Interrupt?.Invoke();

        // Release the render consumer only after the parser finishes or parks in a synchronous
        // call. Both sides use the real command-thread FIFO and the presenter submission scheduler.
        internal void Run(Action parse, Action<bool> beforeDrain)
        {
            using var parsed = new ManualResetEventSlim();
            ExceptionDispatchInfo? failure = null;
            Func<SliceResult> produce = () =>
            {
                if (parsed.IsSet) return SliceResult.NoWork;
                try { parse(); }
                catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
                finally { parsed.Set(); }
                return SliceResult.Completed;
            };
            var thread = Activator.CreateInstance(ThreadType, [Presenter.Instance, Presenter.GetField<CommandStreamQueue>("_commandStream"),
                Presenter.Harness.Memory, (Action<AgcExports.DeferredWork>)Translation.RunDeferred, produce])!;
            Presenter.SetField("_commandThread", thread);
            Translation.Sink = (AgcExports.IDeferredWorkSink)thread;
            var batch = ThreadType.GetMethod("RunBatch")!.CreateDelegate<Func<long, int, Action, int>>(thread);
            var stop = ThreadType.GetMethod("Stop")!.CreateDelegate<Action<Action>>(thread);
            void Drain() => Presenter.Run(() => batch(long.MaxValue, int.MaxValue, static () => { }));
            var parked = ThreadType.GetField("_waitingForSync", PresenterUnderTest.InstanceMembers)!;
            var waitingSequence = ThreadType.GetField("_waitingForSequence", PresenterUnderTest.InstanceMembers)!;
            ThreadType.GetMethod("Start")!.Invoke(thread, null);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => parsed.IsSet || (bool)parked.GetValue(thread)! ||
                    (long)waitingSequence.GetValue(thread)! != long.MaxValue, 5000));
                failure?.Throw();
                beforeDrain(!parsed.IsSet);
                Assert.True(SpinWait.SpinUntil(() => { Drain(); return parsed.IsSet; }, 5000));
                failure?.Throw();
                Drain();
                Presenter.Run(() =>
                {
                    Presenter.Harness.Scheduler.Finish();
                    Presenter.Harness.Scheduler.WaitForAllPriorityOperations();
                });
            }
            finally
            {
                stop(parsed.IsSet ? static () => { } : Drain);
                Translation.Sink = null;
                Presenter.SetField("_commandThread", null!);
            }
        }

        public void Dispose()
        {
            _context[CpuRegister.Rdi] = (ulong)Handle;
            VideoOutExports.VideoOutClose(_context);
            Presenter.Dispose();
            _fatal.Dispose();
        }
    }
}

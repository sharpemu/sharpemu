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
                Assert.False(waiting);
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
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    public void RetainedReplayConsumesItsOriginalTargetBeforeParsingOn(int index, bool known)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        using var surface = new FlipSurface(stream);
        var state = typeof(AgcExports.CommandStreamTranslation).GetField("_current", PresenterUnderTest.InstanceMembers)!.GetValue(stream.Translation)!;
        var targets = (IDictionary)state.GetType().GetProperty("KnownColorTargets")!.GetValue(state)!;
        if (known) targets.Add(surface.Address, RegisterWords.Color(surface.Address, 64, 64));
        var emptyReplay = index < 0 || !known;
        var asynchronous = emptyReplay;
        stream.Run(() =>
        {
            Assert.True(stream.Translation.TryRetainTargetlessDraw(stream.Interpreter.TypedRegisters, default));
            stream.Interpreter.SetFlip(new(stream.Handle, index, 0, 11));
            stream.Interpreter.SubmitFlip();
        }, waiting => Assert.Equal(!asynchronous, waiting));
        Assert.Null(state.GetType().GetProperty("RetainedTargetlessDraw")!.GetValue(state));
        if (index >= 0) VideoOutExports.MarkFlipPresented(surface.Dequeue());
        Assert.True(stream.Host.IsFlipDone(stream.Handle, index));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(2, true, true)]
    public void FlipChoosesItsPublicationAtCapture(int kind, bool retireBefore, bool requireGpu)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        using var surface = new FlipSurface(stream);
        var publications = stream.Presenter.GetField<PendingGuestPublications>("_guestPublications");
        var older = publications.AddOrdering(0); // May still be unregistered when the parser sees the flip.
        var memory = stream.Presenter.Harness;
        var label = memory.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        const uint value = 42;
        var captured = false;
        var interrupts = 0;
        stream.Interpreter.CompletionLabelsRequireGpu = requireGpu;
        var gate = default(PendingGuestPublications.Entry);
        stream.Presenter.SetField("FlipCaptureStartedForTest", (Action)(() =>
        {
            captured = true;
            gate = stream.Presenter.GetField<PendingGuestPublications.Entry?>("_capturePublication");
            Assert.Equal(retireBefore, gate is null);
            Assert.Equal(0u, BitConverter.ToUInt32(memory.Read(label, 4)));
            if (!retireBefore) publications.Publish(older, static () => { });
        }));
        stream.Interrupt = () =>
        {
            Assert.True(captured);
            Assert.Equal(value, BitConverter.ToUInt32(memory.Read(label, 4)));
            Assert.False(gate?.Completed ?? false);
            interrupts++;
        };
        stream.Run(() =>
        {
            stream.Interpreter.SetFlip(new(stream.Handle, 0, 0, 11));
            if (kind == 0) stream.Interpreter.SubmitFlip();
            else if (kind == 1) stream.Interpreter.SubmitFlipWithLabel(label, value);
            else stream.Interpreter.SubmitFlipWithInterrupt(4, 0x38, label, value);
            stream.Host.DrawIndexed(2, default);
        }, waiting =>
        {
            Assert.False(waiting);
            Assert.False(captured);
            Assert.True(stream.SawIndexedDraw);
            publications.Register(older, memory.Scheduler.CurrentTick);
            if (retireBefore) publications.Publish(older, static () => { });
        });
        Assert.True(captured);
        Assert.True(stream.SawIndexedDraw);
        Assert.Equal(0, publications.Count);
        Assert.Equal(kind == 2 ? 1 : 0, interrupts);
        Assert.Equal(kind == 0 ? 0u : value, BitConverter.ToUInt32(memory.Read(label, 4)));
        Assert.Equal(!retireBefore, gate?.Completed ?? false);
        Assert.Same(gate, surface.PeekPublication());
        Assert.False(stream.Host.IsFlipDone(stream.Handle, 0));
        VideoOutExports.MarkFlipPresented(surface.Dequeue());
        Assert.True(stream.Host.IsFlipDone(stream.Handle, 0));
        if (kind == 0) return;

        // The guest resets the live dword. A paired wait can still use an immediate
        // store, while a label published at GPU completion has no own-write hint.
        Assert.True(memory.Memory.TryWrite(label, new byte[4]));
        var execution = new PacketCursorStack();
        var active = typeof(GpuCommandInterpreter).GetField("_execution", PresenterUnderTest.InstanceMembers)!;
        active.SetValue(stream.Interpreter, execution);
        try { stream.Presenter.Run(() => stream.Interpreter.WaitOnMemory(3, label, value, uint.MaxValue, 0, false)); }
        finally { active.SetValue(stream.Interpreter, null); }
        Assert.Equal(!retireBefore || requireGpu, execution.Suspended);
    }

    [Fact]
    public void UnrelatedPublicationOnAnotherQueueDoesNotGateTheFlip()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var publications = stream.Presenter.GetField<PendingGuestPublications>("_guestPublications");
        var other = publications.AddOrdering(GpuCommandInterpreter.ComputeQueueBase);
        publications.Register(other, stream.Presenter.Harness.Scheduler.CurrentTick);
        stream.Run(() =>
        {
            stream.Interpreter.SetFlip(new(stream.Handle, -1, 0, 12));
            stream.Interpreter.SubmitFlip();
        }, waiting => Assert.False(waiting));
        Assert.True(stream.Host.IsFlipDone(stream.Handle, -1));
        Assert.False(other.Completed);
        publications.Publish(other, static () => { });
        Assert.Equal(0, publications.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignWaitAndInterruptObserveTheNoBufferFlipAtItsRelease(bool retireBefore)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var memory = stream.Presenter.Harness;
        var label = memory.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        var publications = stream.Presenter.GetField<PendingGuestPublications>("_guestPublications");
        var older = publications.AddOrdering(0);
        publications.Register(older, memory.Scheduler.CurrentTick);
        if (!retireBefore)
            stream.Presenter.SetField("FlipCaptureStartedForTest", (Action)(() => publications.Publish(older, static () => { })));
        var passedFlip = false;
        bool? doneAtInterrupt = null;
        stream.Interrupt = () =>
        {
            Assert.Equal(19u, BitConverter.ToUInt32(memory.Read(label, 4)));
            doneAtInterrupt = stream.Host.IsFlipDone(stream.Handle, -1);
        };
        stream.Run(() =>
        {
            stream.Interpreter.SetFlip(new(stream.Handle, -1, 0, 13));
            stream.Interpreter.SubmitFlipWithInterrupt(4, 0x38, label, 19);
            passedFlip = true;
            Assert.True(stream.Host.TryReadDeferredLabel(GpuCommandInterpreter.ComputeQueueBase, label, 4, out var forwarded, out var value));
            if (retireBefore) Assert.False(forwarded);
            if (!forwarded)
            {
                Span<byte> bytes = stackalloc byte[4];
                Assert.True(stream.Host.TryReadGuestWaitOperand(label, bytes));
                value = BitConverter.ToUInt32(bytes);
            }
            Assert.Equal(19UL, value);
        }, waiting =>
        {
            Assert.True(waiting);
            Assert.True(passedFlip);
            if (retireBefore) publications.Publish(older, static () => { });
        });
        Assert.Equal(retireBefore, doneAtInterrupt);
        Assert.True(stream.Host.IsFlipDone(stream.Handle, -1));
        Assert.Equal(0, publications.Count);
    }

    [Fact]
    public void CaptureFailureDropsTheReservedPublicationAndItsInterrupt()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var memory = stream.Presenter.Harness;
        var label = memory.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        var publications = stream.Presenter.GetField<PendingGuestPublications>("_guestPublications");
        var older = publications.AddOrdering(0);
        publications.Register(older, memory.Scheduler.CurrentTick);
        stream.Slot.GetType().GetProperty("GroupIndex")!.SetValue(stream.Slot, 0);
        var before = VideoOutExports.PendingFlipRequestCount;
        var interrupts = 0;
        stream.Interrupt = () => interrupts++;
        var failure = Record.Exception(() => stream.Run(() =>
        {
            stream.Interpreter.SetFlip(new(stream.Handle, 0, 0, 9));
            stream.Interpreter.SubmitFlipWithInterrupt(4, 0x38, label, 7);
        }, waiting => Assert.False(waiting)));
        Assert.Contains("display buffer that is not registered", Assert.IsType<SchedulerFatalException>(failure).Message);
        Assert.Equal(0, publications.Count);
        Assert.Equal(0, interrupts);
        Assert.Equal(0u, BitConverter.ToUInt32(memory.Read(label, 4)));
        Assert.Equal(before, VideoOutExports.PendingFlipRequestCount);
        Assert.True(stream.Host.HasFlipSlot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NextLabelAndInterruptObserveTheFlipDecision(bool retireBefore)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var stream = new FlipStream(fixture.Vulkan!);
        var memory = stream.Presenter.Harness;
        var label = memory.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        var publications = stream.Presenter.GetField<PendingGuestPublications>("_guestPublications");
        var older = publications.AddOrdering(0);
        publications.Register(older, memory.Scheduler.CurrentTick);
        var passedFlip = false;
        var signals = new List<uint>();
        stream.Interrupt = () => signals.Add(BitConverter.ToUInt32(memory.Read(label, 4)));
        if (!retireBefore)
            stream.Presenter.SetField("FlipCaptureStartedForTest", (Action)(() => publications.Publish(older, static () => { })));
        stream.Run(() =>
        {
            stream.Interpreter.SetFlip(new(stream.Handle, -1, 0, 8));
            stream.Interpreter.SubmitFlip();
            passedFlip = true;
            var next = new EndOfPipeWrite(EndOfPipeWriteKind.InterruptWriteBack32, 1, label, 17, 3);
            if (!stream.Host.TryDeferEndOfPipe(0, next))
            {
                stream.Interpreter.WriteDword(label, 17);
                stream.Host.RecordEndOfPipe(next);
            }
            stream.Host.Flush();
        }, waiting =>
        {
            Assert.True(waiting);
            Assert.True(passedFlip);
            Assert.Empty(signals);
            if (retireBefore) publications.Publish(older, static () => { });
        });
        Assert.Equal(new uint[] { 17 }, signals);
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

        internal PendingGuestPublications.Entry? PeekPublication()
        {
            var frame = _queue.GetType().GetMethod("Peek")!.Invoke(_queue, null)!;
            return (PendingGuestPublications.Entry?)frame.GetType().GetProperty("Publication")!.GetValue(frame);
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

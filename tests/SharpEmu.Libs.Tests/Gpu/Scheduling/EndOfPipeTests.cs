// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Agc;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class EndOfPipeTests
{
    private sealed class RecordingCompletion : IEndOfPipeSink, IFlipSubmitter
    {
        public List<string> Calls { get; } = new();

        public Queue<int> FlipResults { get; } = new();

        public int SlotWaits { get; private set; }

        public Action? OnInterrupt { get; set; }

        public void TriggerInterrupt(int eventId, uint contextId)
        {
            OnInterrupt?.Invoke();
            Add($"interrupt {eventId} {contextId}");
        }

        public int SubmitFlipFromGpu(RecordingBuffer buffer, int handle, int index, int flipMode, long flipArg, out ulong requestId)
        {
            _ = buffer.Handle;
            Add($"flip {handle} {index} {flipMode} {flipArg}");
            var result = FlipResults.Dequeue();
            requestId = result == 0 ? 42UL : 0UL;
            return result;
        }

        public void WaitForSubmitSlot() => SlotWaits++;

        public void CompleteFlip(ulong requestId) => Add($"complete_flip {requestId}");

        public string[] Snapshot()
        {
            lock (Calls)
            {
                return Calls.ToArray();
            }
        }

        private void Add(string entry)
        {
            lock (Calls)
            {
                Calls.Add(entry);
            }
        }
    }

    private readonly FakeTickDevice _device = new();
    private readonly RecordingCompletion _completion = new();
    private readonly FakeCpuMemory _memory = new(0x1000, 0x10_000);
    private readonly byte[] _gds = new byte[ManagedCommandStreamHost.GdsBytes];

    private EndOfPipe NewEndOfPipe() => new(_completion, _completion, _memory, ReadGds);

    private uint[] ReadGds(uint wordOffset, uint wordCount)
    {
        var words = new uint[checked((int)wordCount)];
        EndOfPipe.ReadGdsWords(_gds, words, wordOffset, wordCount);
        return words;
    }

    [Fact]
    public void AlreadyPublishedLabelIsNotRewrittenWhenInterruptRetires()
    {
        using var scheduler = NewActiveScheduler(_device);
        var eop = NewEndOfPipe();
        const ulong destination = 0x7000;
        const ulong value = 0x1122_3344_5566_7788;
        const ulong guestReplacement = 0x8877_6655_4433_2211;
        ulong valueObservedByInterrupt = 0;
        _completion.OnInterrupt = () => valueObservedByInterrupt = Read64(_memory, destination);

        Assert.True(_memory.TryWrite(destination, BitConverter.GetBytes(value)));
        eop.RecordWrite64WithInterrupt(1, scheduler.Current, destination, value, 9, 3);

        Assert.Equal(value, Read64(_memory, destination));
        Assert.Empty(_device.Submits);
        Assert.Equal(1UL, scheduler.Submit());
        Assert.Single(_device.Submits);
        Assert.True(_memory.TryWrite(destination, BitConverter.GetBytes(guestReplacement)));

        _device.Complete(1);
        Assert.True(WaitUntil(() => _completion.Snapshot().Length == 1));
        Assert.Equal(guestReplacement, Read64(_memory, destination));
        Assert.Equal(guestReplacement, valueObservedByInterrupt);
        Assert.Equal(new[] { "interrupt 9 3" }, _completion.Snapshot());
    }

    [Fact]
    public void WriteVariantsRecordDebugInfoAndDeferTheirCompletionUrgently()
    {
        using var scheduler = NewActiveScheduler(_device);
        var eop = NewEndOfPipe();
        var buffer = scheduler.Current;

        eop.RecordWrite64(7, buffer, 0x1000, 0x1_0000_0002);
        Assert.Equal(((uint)RecordedOperation.EopWrite, 7UL, 8u, 2u, 1u, 0u, 0x1000UL),
            (buffer.DebugOp, buffer.DebugSubmitId, buffer.DebugArg0, buffer.DebugArg1, buffer.DebugArg2, buffer.DebugArg3, buffer.DebugArg4));

        eop.RecordWrite32WithInterrupt(8, buffer, 0x2000, 5, 9, 3);
        Assert.Equal(((uint)RecordedOperation.EopInterrupt, 4u, 3u, 5u, 0u), (buffer.DebugOp, buffer.DebugArg0, buffer.DebugArg1, buffer.DebugArg2, buffer.DebugArg3));

        eop.RecordWrite32WithFlip(9, buffer, 0x3000, 1, 2, 3, 4, -5, 11);
        Assert.Equal(((uint)RecordedOperation.EopFlip, 2u, 3u, 4u, 1u, unchecked((ulong)-5L)),
            (buffer.DebugOp, buffer.DebugArg0, buffer.DebugArg1, buffer.DebugArg2, buffer.DebugArg3, buffer.DebugArg4));

        eop.RecordWrite32WithInterruptWriteBackAndFlip(10, buffer, 0x4000, 6, 2, 3, 4, 5, 12, 13);
        Assert.Equal((uint)RecordedOperation.EopWriteBackFlip, buffer.DebugOp);

        eop.RecordFlipCompletion(11, buffer, 2, 3, 4, 5, 14);
        eop.QueueInterruptOnCompletion(buffer, 15, 16);
        eop.RecordClockWriteWithWriteBack(12, buffer, 0x5000);
        Assert.Equal(((uint)RecordedOperation.EopWriteBack, 8u, 0u, 0u), (buffer.DebugOp, buffer.DebugArg0, buffer.DebugArg1, buffer.DebugArg2));
        eop.RecordGdsWrite32(13, buffer, 0x6000, 2, 4);
        Assert.Equal(((uint)RecordedOperation.EopWrite, 2u, 4u), (buffer.DebugOp, buffer.DebugArg0, buffer.DebugArg1));

        Assert.Empty(_completion.Snapshot());
        scheduler.Submit();
        Assert.Empty(_completion.Snapshot());

        _device.Complete(1);
        Assert.True(WaitUntil(() => _completion.Snapshot().Length == 6));
        Assert.Equal(
            new[] { "interrupt 9 3", "complete_flip 11", "complete_flip 12", "interrupt 13 0", "complete_flip 14", "interrupt 15 16" },
            _completion.Snapshot());
    }

    [Fact]
    public void GdsLabelIsPublishedAtRetirementBeforeItsInterrupt()
    {
        using var scheduler = NewActiveScheduler(_device);
        var eop = NewEndOfPipe();
        const ulong destination = 0x7100;
        const uint value = 0xAABB_CCDD;
        uint valueObservedByInterrupt = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(_gds.AsSpan(2 * sizeof(uint)), value);
        _completion.OnInterrupt = () => valueObservedByInterrupt = Read32(_memory, destination);

        eop.RecordGdsWrite32WithInterrupt(1, scheduler.Current, destination, 2, 1, 9, 3);

        Assert.Equal(0u, Read32(_memory, destination));
        Assert.Equal(1UL, scheduler.Submit());
        Assert.Equal(0u, Read32(_memory, destination));

        _device.Complete(1);
        Assert.True(WaitUntil(() => _completion.Snapshot().Length == 1));
        Assert.Equal(value, Read32(_memory, destination));
        Assert.Equal(value, valueObservedByInterrupt);
        Assert.Equal(new[] { "interrupt 9 3" }, _completion.Snapshot());
    }

    [Fact]
    public void DeferredSignalsWithoutAnActiveBufferOrToAddressZeroAreFatal()
    {
        using var fatal = new FatalScope();
        var scheduler = new SubmissionScheduler(_device, new RecordingRenderingState());
        var eop = NewEndOfPipe();
        var buffer = scheduler.BeginCommand();

        eop.RecordWrite32(1, buffer, 0x1000, 1);
        Assert.Throws<SchedulerFatalException>(() => eop.RecordWrite32WithInterrupt(1, buffer, 0x1000, 1, 2));
        Assert.Throws<SchedulerFatalException>(() => eop.QueueInterruptOnCompletion(buffer, 1, 2));
        Assert.Throws<SchedulerFatalException>(() => eop.RecordWrite32(1, buffer, 0, 1));
        Assert.Equal(3, fatal.Messages.Count);

        _device.CompleteOnSubmit = true;
        scheduler.Dispose();
    }

    [Theory]
    [InlineData(10UL, 10UL, true, 100_000_000UL)]
    [InlineData(3UL, 2UL, true, 150_000_000UL)]
    [InlineData(ulong.MaxValue, 1UL, false, 0UL)]
    [InlineData(199_999_999_999UL, 200_000_000_000UL, false, 0UL)]
    [InlineData(5UL, 0UL, false, 0UL)]
    public void ScaleReferenceClockRejectsOverflow(ulong ticks, ulong frequency, bool expected, ulong value)
    {
        Assert.Equal(expected, EndOfPipe.TryScaleReferenceClock(ticks, frequency, out var scaled));
        Assert.Equal(value, scaled);
        Assert.True(EndOfPipe.ReadReferenceClock() > 0);
    }

    [Fact]
    public void FlipPreparationRetriesWhileTheQueueIsFull()
    {
        using var fatal = new FatalScope();
        using var scheduler = NewActiveScheduler(_device);
        var eop = NewEndOfPipe();
        _completion.FlipResults.Enqueue(IFlipSubmitter.FlipQueueFull);
        _completion.FlipResults.Enqueue(IFlipSubmitter.FlipQueueFull);
        _completion.FlipResults.Enqueue(0);

        Assert.Equal(42UL, eop.PrepareVideoOutFlip(scheduler.Current, 1, 2, 3, 4));
        Assert.Equal(2, _completion.SlotWaits);
        Assert.Equal(3, _completion.Snapshot().Length);

        _completion.FlipResults.Enqueue(unchecked((int)0x8029000B));
        Assert.Throws<SchedulerFatalException>(() => eop.PrepareVideoOutFlip(scheduler.Current, 1, 2, 3, 4));
        _device.CompleteOnSubmit = true;
    }

    [Fact]
    public void ReadGdsCopiesDwordsAndRejectsOutOfRangeReads()
    {
        using var fatal = new FatalScope();
        var gds = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(gds.AsSpan(i * 4), (uint)(i + 1));
        }

        var destination = new uint[2];
        EndOfPipe.ReadGdsWords(gds, destination, 1, 2);
        Assert.Equal(new uint[] { 2, 3 }, destination);

        Assert.Throws<SchedulerFatalException>(() => EndOfPipe.ReadGdsWords(gds, destination, 3, 2));
        Assert.Throws<SchedulerFatalException>(() => EndOfPipe.ReadGdsWords(ReadOnlySpan<byte>.Empty, destination, 0, 0));
    }

    private static ulong Read64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static uint Read32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}

[Collection(GraphicsEventQueueStateCollection.Name)]
public sealed class EndOfPipeEventsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;

    [Fact]
    public void TriggerInterruptQueuesOneEventPerTriggerOnlyForTheRegisteredId()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress + 0x100;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        var handle = Read64(memory, BaseAddress + 0x100);
        Assert.True(KernelEventQueueCompatExports.RegisterEvent(handle, 0x20, KernelEventQueueCompatExports.KernelEventFilterGraphics, 0xBEEF));

        var events = new EndOfPipeEvents();
        events.TriggerInterrupt(0x20, 0x77);
        events.TriggerInterrupt(0x20, 0x78);
        events.TriggerInterrupt(0x21, 0x79);

        Span<byte> timeout = stackalloc byte[8];
        Assert.True(memory.TryWrite(BaseAddress + 0x400, timeout));
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = BaseAddress + 0x200;
        ctx[CpuRegister.Rdx] = 4;
        ctx[CpuRegister.Rcx] = BaseAddress + 0x300;
        ctx[CpuRegister.R8] = BaseAddress + 0x400;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelEventQueueCompatExports.KernelWaitEqueue(ctx));

        Assert.Equal(2UL, Read64(memory, BaseAddress + 0x300) & 0xFFFF_FFFF);
        Assert.Equal(0x20UL, Read64(memory, BaseAddress + 0x200));
        Assert.Equal(0x77UL, Read64(memory, BaseAddress + 0x210));
        Assert.Equal(0x20UL, Read64(memory, BaseAddress + 0x220));
        Assert.Equal(0x78UL, Read64(memory, BaseAddress + 0x230));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
    }

    private static ulong Read64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }
}

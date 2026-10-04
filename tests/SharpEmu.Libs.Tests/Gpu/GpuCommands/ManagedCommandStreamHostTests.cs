// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class ManagedCommandStreamHostTests
{
    [Fact]
    public void EndOfPipeWrite_LabelSizesCoverEveryKind()
    {
        var expected = new Dictionary<EndOfPipeWriteKind, ulong>
        {
            [EndOfPipeWriteKind.Write32] = 4,
            [EndOfPipeWriteKind.Write64] = 8,
            [EndOfPipeWriteKind.WriteBack32] = 4,
            [EndOfPipeWriteKind.WriteBack64] = 8,
            [EndOfPipeWriteKind.InterruptOnly] = 0,
            [EndOfPipeWriteKind.Interrupt32] = 4,
            [EndOfPipeWriteKind.Interrupt64] = 8,
            [EndOfPipeWriteKind.InterruptWriteBack32] = 4,
            [EndOfPipeWriteKind.InterruptWriteBack64] = 8,
            [EndOfPipeWriteKind.GdsWrite32] = 12,
            [EndOfPipeWriteKind.InterruptGdsWrite32] = 12,
            [EndOfPipeWriteKind.ClockWrite] = 8,
            [EndOfPipeWriteKind.ClockWriteBack] = 8,
            [EndOfPipeWriteKind.InterruptClockWrite] = 8,
            [EndOfPipeWriteKind.InterruptClockWriteBack] = 8,
            [EndOfPipeWriteKind.Flip] = 0,
            [EndOfPipeWriteKind.FlipWithWrite32] = 4,
            [EndOfPipeWriteKind.FlipWithInterruptWriteBack32] = 4,
        };

        Assert.Equal(Enum.GetValues<EndOfPipeWriteKind>().Length, expected.Count);
        foreach (var (kind, byteCount) in expected)
        {
            Assert.Equal(byteCount, new EndOfPipeWrite(kind, 1, GdsWordCount: 3).LabelByteCount);
        }
    }

    private sealed class RecordingInterrupt(FakeCpuMemory memory, ulong label) : IEndOfPipeSink
    {
        public uint ObservedLabel { get; private set; }

        public void TriggerInterrupt(int eventId, uint contextId)
        {
            Assert.Equal(7, eventId);
            Assert.Equal(9u, contextId);
            ObservedLabel = Read32(memory, label);
        }
    }

    private sealed class ImmediateFlips : ICommandStreamFlipTarget
    {
        public ulong Prepare(int handle, int index, int flipMode, long flipArgument) => 1;

        public void Complete(ulong requestId)
        {
        }

        public void Presented(ulong requestId)
        {
        }

        public bool IsDone(int handle, int index) => true;
    }

    private sealed class OverridingGdsHost(
        FakeCpuMemory memory,
        IEndOfPipeSink interrupts,
        ICommandStreamFlipTarget flips) : ManagedCommandStreamHost(memory, interrupts, flips)
    {
        public bool ReadGdsCalled { get; private set; }

        public override void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount)
        {
            ReadGdsCalled = true;
            Assert.Equal(2u, wordOffset);
            Assert.Equal(1u, wordCount);
            destination[0] = 0xAABB_CCDD;
        }
    }

    [Fact]
    public void GdsCompletionUsesTheVirtualReaderAndPublishesBeforeInterrupt()
    {
        const ulong memoryBase = 0x1000_0000;
        const ulong label = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var interrupt = new RecordingInterrupt(memory, label);
        var host = new OverridingGdsHost(memory, interrupt, new ImmediateFlips());

        host.RecordEndOfPipe(new EndOfPipeWrite(
            EndOfPipeWriteKind.InterruptGdsWrite32,
            1,
            label,
            EventId: 7,
            ContextId: 9,
            GdsWordOffset: 2,
            GdsWordCount: 1));

        Assert.True(host.ReadGdsCalled);
        Assert.Equal(0xAABB_CCDDu, Read32(memory, label));
        Assert.Equal(0xAABB_CCDDu, interrupt.ObservedLabel);
    }

    private static uint Read32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}

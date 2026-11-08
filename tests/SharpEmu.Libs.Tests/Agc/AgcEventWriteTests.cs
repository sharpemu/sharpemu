// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcEventWriteTests
{
    private const ulong CommandBufferAddress = RecordingCommandStreamHost.MemoryBase + 0x100;
    private const ulong PacketAddress = RecordingCommandStreamHost.MemoryBase + 0x400;
    private const ulong ResultAddress = RecordingCommandStreamHost.MemoryBase + 0x800;

    [Theory]
    [InlineData(0x38u, 0x138u)]
    [InlineData(0x39u, 0x139u)]
    public void AddressedEvent_EncodesAddressAndAdvancesFourDwords(uint eventType, uint control)
    {
        var host = CreateHost(out var context);
        context[CpuRegister.Rsi] = eventType;
        context[CpuRegister.Rdx] = 0x12_3456_7808;

        Assert.Equal(0, AgcExports.DcbEventWrite(context));
        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Assert.Equal(0xC0024600u, host.ReadDword(PacketAddress));
        Assert.Equal(control, host.ReadDword(PacketAddress + 4));
        Assert.Equal(0x34567808u, host.ReadDword(PacketAddress + 8));
        Assert.Equal(0x12u, host.ReadDword(PacketAddress + 12));
        Assert.Equal(PacketAddress + 16, host.ReadQword(CommandBufferAddress + 0x10));
    }

    [Theory]
    [InlineData(0x07u, 0x407u)]
    [InlineData(0x0Fu, 0x40Fu)]
    [InlineData(0x10u, 0x410u)]
    [InlineData(0x3Au, 0x3Au)]
    public void UnaddressedEvent_EncodesEventIndexAndAdvancesTwoDwords(uint eventType, uint control)
    {
        var host = CreateHost(out var context);
        context[CpuRegister.Rsi] = eventType;
        context[CpuRegister.Rdx] = ResultAddress;
        host.WriteDword(PacketAddress + 8, 0xDEADBEEF);

        Assert.Equal(0, AgcExports.DcbEventWrite(context));
        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Assert.Equal(0xC0004600u, host.ReadDword(PacketAddress));
        Assert.Equal(control, host.ReadDword(PacketAddress + 4));
        Assert.Equal(0xDEADBEEFu, host.ReadDword(PacketAddress + 8));
        Assert.Equal(PacketAddress + 8, host.ReadQword(CommandBufferAddress + 0x10));
    }

    [Fact]
    public void OcclusionEvents_PublishBothCountersThroughTheInterpreter()
    {
        var host = CreateHost(out var context);
        context[CpuRegister.Rsi] = 0x39;
        context[CpuRegister.Rdx] = ResultAddress;
        Assert.Equal(0, AgcExports.DcbEventWrite(context));
        context[CpuRegister.Rdx] = ResultAddress + 8;
        Assert.Equal(0, AgcExports.DcbEventWrite(context));

        var interpreter = new GpuCommandInterpreter(host, 0, 0);
        Assert.Equal(SubmissionProgress.Complete, interpreter.Process(new PacketCursorStack(), PacketAddress, 8));
        for (var block = 0; block < 16; block++)
        {
            Assert.Equal(1UL << 63, host.ReadQword(ResultAddress + (ulong)block * 16));
            Assert.Equal((1UL << 63) | 1, host.ReadQword(ResultAddress + (ulong)block * 16 + 8));
        }
    }

    [Fact]
    public void AddressedEvent_RequiresSpaceForTheAddressWords()
    {
        var host = CreateHost(out var context);
        host.WriteQword(CommandBufferAddress + 0x18, PacketAddress + 8);
        host.WriteDword(PacketAddress, 0xDEADBEEF);
        context[CpuRegister.Rsi] = 0x39;
        context[CpuRegister.Rdx] = ResultAddress;

        Assert.Equal(0, AgcExports.DcbEventWrite(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress, host.ReadQword(CommandBufferAddress + 0x10));
        Assert.Equal(0xDEADBEEFu, host.ReadDword(PacketAddress));
    }

    [Fact]
    public void InvalidEvent_DoesNotAllocateAPacket()
    {
        var host = CreateHost(out var context);
        context[CpuRegister.Rsi] = 0x40;
        Assert.Equal(0, AgcExports.DcbEventWrite(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress, host.ReadQword(CommandBufferAddress + 0x10));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void SelectedCounter_PreservesItsValueAcrossSelectionAndReset(uint counterId)
    {
        var runner = new StreamRunner();
        var otherCounter = (counterId + 1) % 4;
        Select(runner, counterId);
        Dump(runner);
        Dump(runner);
        Assert.Equal((1UL << 63) | 1, runner.Host.ReadQword(ResultAddress));

        Select(runner, otherCounter);
        Dump(runner);
        Assert.Equal(1UL << 63, runner.Host.ReadQword(ResultAddress));
        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x3A));
        Dump(runner);
        Assert.Equal(1UL << 63, runner.Host.ReadQword(ResultAddress));

        Select(runner, counterId);
        Dump(runner);
        Assert.Equal((1UL << 63) | 2, runner.Host.ReadQword(ResultAddress));
        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x3A));
        Dump(runner);
        Assert.Equal(1UL << 63, runner.Host.ReadQword(ResultAddress));
    }

    [Theory]
    [InlineData(1u, 8u)]
    [InlineData(2u, 16u)]
    [InlineData(3u, 32u)]
    public void SelectedLayout_WritesOnlyEnabledInstancesAtTheConfiguredStride(uint stride, uint strideBytes)
    {
        var runner = new StreamRunner();
        const uint mask = (1u << 0) | (1u << 2) | (1u << 15) | (1u << 23);
        for (var offset = 0u; offset < 24 * strideBytes + 8; offset += 8)
        {
            runner.Host.WriteQword(ResultAddress + offset, 0xDEADBEEF);
        }

        Select(runner, 2, stride, mask);
        Dump(runner);
        for (var instance = 0u; instance < 24; instance++)
        {
            for (var offset = 0u; offset < strideBytes; offset += 8)
            {
                var expected = offset == 0 && (mask & (1u << (int)instance)) != 0
                    ? 1UL << 63
                    : 0xDEADBEEFUL;
                Assert.Equal(expected, runner.Host.ReadQword(ResultAddress + instance * strideBytes + offset));
            }
        }
        Assert.Equal(0xDEADBEEFUL, runner.Host.ReadQword(ResultAddress + 24 * strideBytes));
    }

    [Fact]
    public void EmptyInstanceMask_DoesNotWriteGuestResults()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(ResultAddress, 0xDEADBEEF);
        Select(runner, 0, mask: 0);
        Dump(runner);
        Assert.Equal(0xDEADBEEFUL, runner.Host.ReadQword(ResultAddress));
        Assert.Empty(runner.Host.TracedWrites);
    }

    [Fact]
    public void CounterState_IsSeparateForEachInterpreter()
    {
        var first = new StreamRunner();
        var second = new StreamRunner(1);
        Select(first, 3);
        Dump(first);
        Dump(first);
        Select(second, 3);
        Dump(second);
        Assert.Equal(1UL << 63, second.Host.ReadQword(ResultAddress));
        Dump(first);
        Assert.Equal((1UL << 63) | 2, first.Host.ReadQword(ResultAddress));
    }

    [Fact]
    public void BuilderControl_UsesPackedDataWithoutReadingGuestMemory()
    {
        var host = CreateHost(out var context);
        context[CpuRegister.Rsi] = 0x38;
        const ulong packedControl = (2u << 3) | (1u << 9) | (5u << 11);
        context[CpuRegister.Rdx] = packedControl;
        Assert.Equal(0, AgcExports.DcbEventWrite(context));
        context[CpuRegister.Rsi] = 0x39;
        context[CpuRegister.Rdx] = ResultAddress;
        Assert.Equal(0, AgcExports.DcbEventWrite(context));
        host.WriteQword(ResultAddress + 8, 0xDEADBEEF);

        var interpreter = new GpuCommandInterpreter(host, 0, 0);
        Assert.Equal(SubmissionProgress.Complete, interpreter.Process(new PacketCursorStack(), PacketAddress, 8));
        Assert.Equal(1UL << 63, host.ReadQword(ResultAddress));
        Assert.Equal(0xDEADBEEFUL, host.ReadQword(ResultAddress + 8));
        Assert.Equal(1UL << 63, host.ReadQword(ResultAddress + 16));
        Assert.DoesNotContain(packedControl, host.GuestReads);
    }

    [Fact]
    public void SingleInstance_UsesTheMinimumStrideWithoutWritingPastItsResult()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(ResultAddress + 8, 0xDEADBEEF);
        Select(runner, 0, stride: 0, mask: 1);
        Dump(runner);
        Assert.Equal(1UL << 63, runner.Host.ReadQword(ResultAddress));
        Assert.Equal(0xDEADBEEFUL, runner.Host.ReadQword(ResultAddress + 8));
    }

    [Fact]
    public void MinimumStride_PublishesOverlappingResultsInInstanceOrder()
    {
        var runner = new StreamRunner();
        const uint mask = (1u << 0) | (1u << 1) | (1u << 3);
        runner.Host.WriteDword(ResultAddress + 20, 0xDEADBEEF);
        Select(runner, 0, stride: 0, mask: mask);
        Dump(runner);
        Dump(runner);
        Assert.Equal(0x0000000100000001UL, runner.Host.ReadQword(ResultAddress));
        Assert.Equal((1UL << 63) | 1, runner.Host.ReadQword(ResultAddress + 4));
        Assert.Equal((1UL << 63) | 1, runner.Host.ReadQword(ResultAddress + 12));
        Assert.Equal(0xDEADBEEFu, runner.Host.ReadDword(ResultAddress + 20));
    }

    [Theory]
    [InlineData(0x38u, 2)]
    [InlineData(0x38u, 3)]
    [InlineData(0x38u, 5)]
    [InlineData(0x39u, 2)]
    [InlineData(0x39u, 3)]
    [InlineData(0x39u, 5)]
    public void AddressedEvent_RejectsAnInvalidPacketLength(uint eventType, int length)
    {
        var runner = new StreamRunner();
        var payload = new uint[length - 1];
        payload[0] = eventType | 0x100;
        Assert.Contains("packet length", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.EventWrite, payload)).Message);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(7u)]
    [InlineData(63u)]
    public void UnsupportedCounter_DoesNotChangeTheSelectedCounter(uint counterId)
    {
        var runner = new StreamRunner();
        Select(runner, 0);
        Dump(runner);
        Assert.Contains("selection is not supported", runner.RunExpectingFatal(
            StreamRunner.Packet(PacketOpcode.EventWrite, 0x138, counterId << 3, 0)).Message);
        Dump(runner);
        Assert.Equal((1UL << 63) | 1, runner.Host.ReadQword(ResultAddress));
    }

    [Fact]
    public void InvalidControlAndResetIndex_DoNotChangeCounterState()
    {
        var runner = new StreamRunner();
        Dump(runner);
        Assert.Contains("selection is not supported", runner.RunExpectingFatal(
            StreamRunner.Packet(PacketOpcode.EventWrite, 0x38, 0, 0)).Message);
        Assert.Contains("reset index is invalid", runner.RunExpectingFatal(
            StreamRunner.Packet(PacketOpcode.EventWrite, 0x13A)).Message);
        Assert.Contains("reset packet length", runner.RunExpectingFatal(
            StreamRunner.Packet(PacketOpcode.EventWrite, 0x3A, 0)).Message);
        Dump(runner);
        Assert.Equal((1UL << 63) | 1, runner.Host.ReadQword(ResultAddress));
    }

    private static void Select(StreamRunner runner, uint counterId, uint stride = 2, uint mask = 0xFFFF)
    {
        var control = ((ulong)counterId << 3) | ((ulong)stride << 9) | ((ulong)mask << 11);
        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x138, StreamRunner.Low(control), StreamRunner.High(control)));
    }

    private static void Dump(StreamRunner runner) =>
        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x139, StreamRunner.Low(ResultAddress), StreamRunner.High(ResultAddress)));

    private static RecordingCommandStreamHost CreateHost(out CpuContext context)
    {
        var host = new RecordingCommandStreamHost();
        host.WriteQword(CommandBufferAddress + 0x10, PacketAddress);
        host.WriteQword(CommandBufferAddress + 0x18, PacketAddress + 0x100);
        context = new CpuContext(host.Memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        return host;
    }
}

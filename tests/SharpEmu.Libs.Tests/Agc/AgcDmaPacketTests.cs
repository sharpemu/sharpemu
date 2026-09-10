// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDmaPacketTests
{
    [Theory]
    [InlineData(0u, 2u, 0UL)]
    [InlineData(3u, 2u, 0UL)]
    [InlineData(3u, 2u, 0x12345678UL)]
    [InlineData(3u, 3u, StreamRunner.DataAddress)]
    public void AsyncDmaPreservesArgumentSelectorsAndControls(uint destinationSelector, uint sourceSelector, ulong source)
    {
        const ulong memoryAddress = 0x2_1000_0000;
        const ulong commandBufferAddress = memoryAddress + 0x80;
        const ulong packetAddress = memoryAddress + 0x200;
        const ulong stackAddress = memoryAddress + 0x500;
        var memory = new FakeCpuMemory(memoryAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteQword(memory, commandBufferAddress + 0x10, packetAddress);
        WriteQword(memory, commandBufferAddress + 0x18, packetAddress + 0x100);
        WriteQword(memory, stackAddress + 8, source);
        WriteQword(memory, stackAddress + 16, 4);
        WriteQword(memory, stackAddress + 24, 1);
        WriteQword(memory, stackAddress + 32, 1);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = destinationSelector;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = StreamRunner.LabelAddress;
        context[CpuRegister.R8] = sourceSelector;
        context[CpuRegister.R9] = 2;
        context[CpuRegister.Rsp] = stackAddress;

        AgcExports.AcbDmaData(context);
        Assert.Equal(packetAddress, context[CpuRegister.Rax]);
        Span<byte> packetBytes = stackalloc byte[32];
        Assert.True(memory.TryRead(packetAddress, packetBytes));
        var packet = new uint[8];
        for (var index = 0; index < packet.Length; index++)
        {
            packet[index] = BinaryPrimitives.ReadUInt32LittleEndian(packetBytes[(index * 4)..]);
        }
        Assert.Equal(destinationSelector | (1u << 8) | (sourceSelector << 16) | (2u << 24), packet[1]);
        Assert.Equal(0x10100u, packet[2]);
        Assert.Equal(4u, packet[3]);
        AgcExports.AcbDmaDataGetSize(context);
        Assert.Equal((ulong)packetBytes.Length, context[CpuRegister.Rax]);
        Span<byte> cursorBytes = stackalloc byte[8];
        Assert.True(memory.TryRead(commandBufferAddress + 0x10, cursorBytes));
        Assert.Equal(packetAddress + 32, BinaryPrimitives.ReadUInt64LittleEndian(cursorBytes));

        var runner = new StreamRunner();
        runner.Host.WriteDword(StreamRunner.DataAddress, 0xABCDEF01);
        runner.Host.WriteDword(StreamRunner.LabelAddress, uint.MaxValue);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(packet));
        Assert.Equal(sourceSelector == 2 ? (uint)source : 0xABCDEF01u,
            runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.StartsWith(sourceSelector == 2 ? "fill " : "copy ", Assert.Single(runner.Host.Calls));
    }

    private static void WriteQword(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}

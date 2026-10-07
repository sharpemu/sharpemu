// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcContextStateTests
{
    private const ulong MemoryBase = 0x210000000;
    private const ulong CommandBuffer = MemoryBase + 0x40;
    private const ulong Commands = MemoryBase + 0x200;

    private static uint[] Emit(uint operation, uint capacity, out ulong result, out ulong cursor)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(CommandBuffer + 0x10, Commands));
        Assert.True(context.TryWriteUInt64(CommandBuffer + 0x18, Commands + capacity * 4));
        context[CpuRegister.Rdi] = CommandBuffer;
        context[CpuRegister.Rsi] = operation;
        AgcExports.DcbContextStateOperation(context);
        result = context[CpuRegister.Rax];
        Span<byte> cursorBytes = stackalloc byte[8];
        Assert.True(memory.TryRead(CommandBuffer + 0x10, cursorBytes));
        cursor = BinaryPrimitives.ReadUInt64LittleEndian(cursorBytes);
        var bytes = new byte[(int)(cursor - Commands)];
        Assert.True(memory.TryRead(Commands, bytes));
        return Enumerable.Range(0, bytes.Length / 4)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4))).ToArray();
    }

    [Theory]
    [InlineData(0u, 5u)]
    [InlineData(1u, 27u)]
    [InlineData(2u, 27u)]
    [InlineData(3u, 32u)]
    public void ExportPreservesPacketSizesAndOperation(uint operation, uint expectedDwords)
    {
        var words = Emit(operation, expectedDwords, out var result, out var cursor);
        Assert.Equal(Commands, result);
        Assert.Equal(Commands + expectedDwords * 4, cursor);
        Assert.Equal(operation, words[1]);
        for (var index = 0; index < words.Length;)
        {
            Assert.Equal(PacketOpcode.Nop, PacketHeader.Opcode(words[index]));
            Assert.Equal(index == 0 ? PacketCustomCode.ContextState : 0u, PacketHeader.CustomCode(words[index]));
            index += (int)PacketHeader.Length(words[index]);
            Assert.InRange(index, 1, words.Length);
        }
    }

    [Fact]
    public void ExportedPushClearAndPopRestoreViewportAndTargetMask()
    {
        var push = Emit(3, 32, out _, out _);
        var pop = Emit(2, 27, out _, out _);
        var runner = new StreamRunner();
        runner.Run(
            StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x113, 0x3F800000u, 0u),
            StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x8E, 0xFu),
            push,
            StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x113, 0x3F000000u, 0x3F000000u),
            StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x8E, 0u));
        Assert.Equal(0.5f, runner.Interpreter.TypedRegisters.Context.ScreenViewport.Viewports[0].ZOffset);
        runner.Run(pop);
        var restored = runner.Interpreter.TypedRegisters.Context;
        Assert.Equal(1f, restored.ScreenViewport.Viewports[0].ZScale);
        Assert.Equal(0f, restored.ScreenViewport.Viewports[0].ZOffset);
        Assert.Equal(0xFu, restored.RenderTargetMask);
        Assert.False(runner.Interpreter.TypedRegisters.ContextPushed);
    }

    [Theory]
    [InlineData(4u, 32u)]
    [InlineData(1u, 21u)]
    [InlineData(0u, 4u)]
    public void InvalidOperationOrInsufficientSpaceReturnsNull(uint operation, uint capacity)
    {
        Assert.Empty(Emit(operation, capacity, out var result, out var cursor));
        Assert.Equal(0UL, result);
        Assert.Equal(Commands, cursor);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Direct draws carry a modifier naming the shader stage and the user registers of the pipeline.
// Every modifier must still produce the draw: Unreal's volume passes use a geometry-stage modifier.
public sealed class AgcDirectDrawModifierTests
{
    private const ulong BaseAddress = 0x2_1000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x80;
    private const ulong CommandAddress = BaseAddress + 0x200;

    public static TheoryData<ulong> Modifiers => new() { 0x4000_0000UL, 0x6000_0000UL, 0xA000_0000UL, 0x4008_0205UL, 0x1_6000_0000UL };

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void DrawIndexAutoEmitsTheDrawForEveryModifier(ulong modifier)
    {
        var memory = CreateCommandBuffer();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 6;
        context[CpuRegister.Rdx] = modifier;

        AgcExports.DcbDrawIndexAuto(context);

        Assert.Equal(CommandAddress, context[CpuRegister.Rax]);
        var runner = new StreamRunner();
        Assert.Equal(SubmissionProgress.Complete, runner.Run(ReadDwords(memory, CommandAddress, 7)));
        var draw = Assert.Single(runner.Host.AutoDraws);
        Assert.Equal(6u, draw.VertexCount);
    }

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void DrawIndexEmitsTheDrawForEveryModifier(ulong modifier)
    {
        var memory = CreateCommandBuffer();
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 36;
        context[CpuRegister.Rdx] = StreamRunner.DataAddress;
        context[CpuRegister.Rcx] = modifier;

        AgcExports.DcbDrawIndex(context);

        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        var runner = new StreamRunner();
        Assert.Equal(SubmissionProgress.Complete, runner.Run(ReadDwords(memory, CommandAddress, 11)));
        var draw = Assert.Single(runner.Host.IndexedDraws);
        Assert.Equal(36u, draw.IndexCount);
        Assert.Equal(StreamRunner.DataAddress, draw.IndexAddress);
    }

    private static FakeCpuMemory CreateCommandBuffer()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, CommandAddress + 0x100);
        return memory;
    }

    private static uint[] ReadDwords(FakeCpuMemory memory, ulong address, int count)
    {
        var bytes = new byte[count * sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        var dwords = new uint[count];
        for (var index = 0; index < count; index++)
        {
            dwords[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)));
        }

        return dwords;
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

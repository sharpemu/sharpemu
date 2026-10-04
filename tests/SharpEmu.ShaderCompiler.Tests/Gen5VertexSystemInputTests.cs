// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5VertexSystemInputTests
{
    [Theory]
    [InlineData(32u)]
    [InlineData(64u)]
    public void VertexEntrySeedsNggWaveState(uint waveSize)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.EndProgram(0));
        var request = ResourceTestProgram.Request(
            program,
            ShaderStage.Vertex,
            userDataCount: 0,
            waveSize: waveSize);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var instructions = Instructions(shader.Spirv);
        var constants = instructions
            .Where(static instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length == 3)
            .ToDictionary(
                static instruction => instruction.Operands[1],
                static instruction => instruction.Operands[2]);
        var waveCount = checked(waveSize << 12);
        var waveInfo = (1u << 28) | waveSize;
        var waveCountId = Assert.Single(
            constants,
            pair => pair.Value == waveCount).Key;
        var waveInfoId = Assert.Single(
            constants,
            pair => pair.Value == waveInfo).Key;

        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Store &&
                instruction.Operands.Length >= 2 &&
                instruction.Operands[1] == waveCountId);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Store &&
                instruction.Operands.Length >= 2 &&
                instruction.Operands[1] == waveInfoId);
    }

    private static List<Instruction> Instructions(byte[] code)
    {
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var result = new List<Instruction>();
        for (var index = 5; index < words.Length;)
        {
            var count = (int)(words[index] >> 16);
            result.Add(
                new Instruction(
                    (SpirvOp)(words[index] & 0xffff),
                    words[(index + 1)..(index + count)]));
            index += count;
        }

        return result;
    }

    private sealed record Instruction(
        SpirvOp Opcode,
        uint[] Operands);
}

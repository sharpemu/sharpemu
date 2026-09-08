// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5AlternateImageEntryTests
{
    [Fact]
    public void ConditionalWithoutSkippedImageBlockDoesNotCreateAnAlternateEntry()
    {
        var program = new Gen5ShaderProgram(0x1000,
        [
            new(0, Gen5ShaderEncoding.Sopp, "SCbranchVccz", [1u], [], [], null),
            new(4, Gen5ShaderEncoding.Sopp, "SNop", [0u], [], [], null),
            new(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null),
        ]);
        var property = typeof(Gen5ShaderProgram).GetProperty("AlternateImageEntries",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = Assert.IsAssignableFrom<IReadOnlyDictionary<uint, uint>>(property.GetValue(program));
        Assert.Empty(entries);
        Assert.Same(entries, property.GetValue(program));
    }

    [Theory]
    [InlineData("SCbranchVccz")]
    [InlineData("SCbranchVccnz")]
    [InlineData("SCbranchExecz")]
    public void AlternateImageUsesRegistersFromItsConditionalEntry(string branchOpcode)
    {
        uint[] imageWords = [0x123400, 0x00100000, 0, 0x90000FAC, 0, 0, 0, 0];
        uint[] bufferWords = [0xCD606800, 0x00100045, 0x169, 0x4DFAC];
        var registers = new uint[40];
        imageWords.CopyTo(registers, 0);
        uint[] samplerWords = [1, 2, 3, 4];
        samplerWords.CopyTo(registers, 32);
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Sopp, branchOpcode, [6u], [], [], null),
        };
        for (uint index = 0; index < 4; index++)
        {
            instructions.Add(new(4 + index * 4, Gen5ShaderEncoding.Sop1, "SMovB32", [0u],
                [new Gen5Operand(Gen5OperandKind.LiteralConstant, bufferWords[index])], [Gen5Operand.Scalar(index)], null));
        }

        instructions.Add(new(20, Gen5ShaderEncoding.Sopp, "SBranch", [3u], [], [], null));
        instructions.Add(new(24, Gen5ShaderEncoding.Sopp, "SNop", [0u], [], [], null));
        instructions.Add(new(28, Gen5ShaderEncoding.Mimg, "ImageSampleLz", [0u, 0u], [], [],
            new Gen5ImageControl(1, 0, [], 0, 0, 32, 1, false, false, false, false, false)));
        instructions.Add(new(36, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null));
        var program = new Gen5ShaderProgram(0x1000, instructions);
        var state = new Gen5ShaderState(program, registers, null);

        Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
            new CpuContext(new UnreadableMemory(), Generation.Gen5), state, out var evaluation, out var error), error);
        var binding = Assert.Single(evaluation.ImageBindings);
        Assert.Equal(28u, binding.Pc);
        Assert.Equal(imageWords, binding.ResourceDescriptor);
        Assert.Equal(samplerWords, binding.SamplerDescriptor);
    }

    private sealed class UnreadableMemory : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

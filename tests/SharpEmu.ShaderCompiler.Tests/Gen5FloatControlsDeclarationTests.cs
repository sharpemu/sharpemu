// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5FloatControlsDeclarationTests
{
    private const string FloatControlsExtension = "SPV_KHR_float_controls";

    [Fact]
    public void SupportedSignedZeroInfNanPreserveDeclaresFloat32Mode()
    {
        var instructions = Compile(supported: true);
        var entryPoint = Assert.Single(
            instructions,
            instruction => instruction.Opcode == SpirvOp.EntryPoint);
        var main = entryPoint.Operands[1];

        Assert.Equal(4466u, (uint)SpirvCapability.SignedZeroInfNanPreserve);
        Assert.Equal(4461u, (uint)SpirvExecutionMode.SignedZeroInfNanPreserve);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Extension &&
                DecodeString(instruction.Operands) == FloatControlsExtension);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands.Length == 1 &&
                instruction.Operands[0] ==
                    (uint)SpirvCapability.SignedZeroInfNanPreserve);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[0] == main &&
                instruction.Operands[1] ==
                    (uint)SpirvExecutionMode.SignedZeroInfNanPreserve &&
                instruction.Operands[2] == 32);
    }

    [Fact]
    public void UnsupportedSignedZeroInfNanPreserveOmitsFloatControlsDeclarations()
    {
        var instructions = Compile(supported: false);

        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Extension &&
                DecodeString(instruction.Operands) == FloatControlsExtension);
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands.Length == 1 &&
                instruction.Operands[0] ==
                    (uint)SpirvCapability.SignedZeroInfNanPreserve);
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands.Length >= 2 &&
                instruction.Operands[1] ==
                    (uint)SpirvExecutionMode.SignedZeroInfNanPreserve);
    }

    private static IReadOnlyList<Instruction> Compile(bool supported)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.EndProgram(0));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            ShaderSignedZeroInfNanPreserveFloat32Supported = supported,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);
        return ReadInstructions(shader.Spirv);
    }

    private static IReadOnlyList<Instruction> ReadInstructions(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        Assert.Equal(0x07230203u, words[0]);

        var instructions = new List<Instruction>();
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = checked((int)(words[offset] >> 16));
            Assert.InRange(wordCount, 1, words.Length - offset);
            var operands = words
                .AsSpan(offset + 1, wordCount - 1)
                .ToArray();
            instructions.Add(
                new Instruction(
                    (SpirvOp)(ushort)words[offset],
                    operands));
            offset += wordCount;
        }

        return instructions;
    }

    private static string DecodeString(IReadOnlyList<uint> words)
    {
        var bytes = new List<byte>();
        foreach (var word in words)
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var value = (byte)(word >> shift);
                if (value == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record Instruction(SpirvOp Opcode, uint[] Operands);
}

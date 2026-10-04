// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PrivateFlagStorageTests
{
    [Fact]
    public void ControlFlagsUseUintStorageInGraphicsAndPairedWave64Modules()
    {
        var graphics = Compile(
            Request(
                Program(EndProgram(0)),
                ShaderStage.Pixel,
                userDataCount: 0));

        var computeProgram = Program(Nop(0), EndProgram(4));
        var (plan, resources, layout) = Prepare(
            computeProgram,
            ShaderStage.Compute,
            userDataCount: 0,
            waveSize: 64);
        var pairedWave64 = Compile(
            new ShaderCompileRequest(plan, resources, layout)
            {
                WaveSize = 64,
                HostSubgroupSize = 32,
                LocalSizeX = 64,
            });

        AssertUintBackedPrivateFlags(graphics);
        AssertUintBackedPrivateFlags(pairedWave64, "vccHigh", "execHigh");
    }

    private static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var program,
                out var error),
            error);
        return program.Spirv;
    }

    private static void AssertUintBackedPrivateFlags(
        byte[] spirv,
        params string[] namedFlags)
    {
        var instructions = ReadInstructions(spirv);
        var boolType = Assert.Single(
            instructions,
            static instruction => instruction.Opcode == SpirvOp.TypeBool)
            .Operands[0];
        var uintType = Assert.Single(
            instructions,
            static instruction =>
                instruction.Opcode == SpirvOp.TypeInt &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] == 32 &&
                instruction.Operands[2] == 0)
            .Operands[0];
        var privateBoolPointers = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.TypePointer &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] ==
                    (uint)SpirvStorageClass.Private &&
                instruction.Operands[2] == boolType)
            .Select(static instruction => instruction.Operands[0])
            .ToHashSet();
        var privateUintPointers = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.TypePointer &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] ==
                    (uint)SpirvStorageClass.Private &&
                instruction.Operands[2] == uintType)
            .Select(static instruction => instruction.Operands[0])
            .ToHashSet();

        Assert.Empty(privateBoolPointers);
        Assert.NotEmpty(privateUintPointers);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.INotEqual &&
                instruction.Operands[0] == boolType);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Select &&
                instruction.Operands[0] == uintType);

        foreach (var name in namedFlags)
        {
            var variableId = Assert.Single(
                instructions,
                instruction =>
                    instruction.Opcode == SpirvOp.Name &&
                    DecodeString(instruction.Operands[1..]) == name)
                .Operands[0];
            var variable = Assert.Single(
                instructions,
                instruction =>
                    instruction.Opcode == SpirvOp.Variable &&
                    instruction.Operands[1] == variableId);
            Assert.Contains(variable.Operands[0], privateUintPointers);
            Assert.Equal(
                (uint)SpirvStorageClass.Private,
                variable.Operands[2]);
        }
    }

    private static IReadOnlyList<SpirvInstruction> ReadInstructions(byte[] spirv)
    {
        var instructions = new List<SpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + ((index + 1) * sizeof(uint))));
            }

            instructions.Add(
                new SpirvInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static string DecodeString(ReadOnlySpan<uint> words)
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

    private readonly record struct SpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}

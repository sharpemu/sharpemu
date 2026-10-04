// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Xor3Tests
{
    // Exact first occurrence from the Little Nightmares compute shader.
    private const uint CapturedOpcode = 0xD5780017u;
    private const uint CapturedOperands = 0x045E331Bu;

    [Fact]
    public void CapturedOpcode178DecodesAsXor3()
    {
        var instruction = Decode(CapturedOpcode, CapturedOperands);

        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal("VXor3B32", instruction.Opcode);
        Assert.Equal(
            [Gen5Operand.Vector(27), Gen5Operand.Vector(25), Gen5Operand.Vector(23)],
            instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(23), Assert.Single(instruction.Destinations));
    }

    [Fact]
    public void BothBackendsLowerXorOfAllThreeSources()
    {
        var xor = Decode(CapturedOpcode, CapturedOperands) with { Pc = 0 };
        var program = Program(xor, EndProgram(2 * sizeof(uint)));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var spirv, out var spirvError),
            spirvError);
        var spirvXors = CountSpirvOpcode(spirv.Spirv, SpirvOp.BitwiseXor);
        Assert.True(spirvXors >= 2, $"expected two SPIR-V XORs, found {spirvXors}");

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        var metalXors = CountOccurrences(metal.Source, " ^ ");
        Assert.True(metalXors >= 2, $"expected two Metal XORs, found {metalXors}");
    }

    [Fact]
    public void ResourceGraphTracksXorOfAllThreeSources()
    {
        var xor = Decode(CapturedOpcode, CapturedOperands) with { Pc = 12 };
        var program = Program(
            MoveVectorFromScalar(0, 27, 8),
            MoveVectorFromScalar(4, 25, 9),
            MoveVectorFromScalar(8, 23, 10),
            xor,
            Vop1(20, "VReadfirstlaneB32", 4, Gen5Operand.Vector(23)) with
            {
                Destinations = [Gen5Operand.Scalar(4)],
            },
            MoveScalar(24, 5, 0),
            MoveScalar(28, 6, 16),
            MoveScalar(32, 7, 0),
            BufferLoad(36, 4),
            EndProgram(44));
        var plan = Extract(program);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(
            plan,
            Assert.Single(plan.Info.Buffers).Source,
            Inputs([0, 0, 0, 0, 0, 0, 0, 0, 0xF0F0_00FF, 0x0FF0_F00F, 0xAAAA_5555]),
            out var descriptor));
        Assert.Equal(0x55AA_A5A5u, descriptor.Dwords[0]);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0;
             (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
        {
            count++;
        }

        return count;
    }

    private static int CountSpirvOpcode(byte[] spirv, SpirvOp expected)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var count = 0;
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = checked((int)(words[offset] >> 16));
            if ((SpirvOp)(words[offset] & 0xFFFFu) == expected)
            {
                count++;
            }

            offset += Math.Max(wordCount, 1);
        }

        return count;
    }

    private static Gen5ShaderInstruction Decode(params uint[] instructionWords)
    {
        var bytes = new byte[(instructionWords.Length + 1) * sizeof(uint)];
        for (var index = 0; index < instructionWords.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                instructionWords[index]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(instructionWords.Length * sizeof(uint)),
            0xBF810000u);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error),
            error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

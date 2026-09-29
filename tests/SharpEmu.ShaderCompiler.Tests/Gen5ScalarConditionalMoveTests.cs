// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScalarConditionalMoveTests
{
    [Fact]
    public void DecodesReportedMeshShaderInstruction()
    {
        var words = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(words, 0xBEFE06C1);
        BinaryPrimitives.WriteUInt32LittleEndian(words.AsSpan(4), 0xBF810000);
        var context = new CpuContext(new InstructionMemory(words), Generation.Gen5);

        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        var instruction = program.Instructions[0];
        Assert.Equal("SCselectB64", instruction.Opcode);
        Assert.Equal(Gen5Operand.Scalar(126), Assert.Single(instruction.Destinations));
        Assert.Equal([Gen5Operand.Source(193), Gen5Operand.Scalar(126)], instruction.Sources);

        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Theory]
    [InlineData(0u, 0x12345678u, 0x9ABCDEF0u)]
    [InlineData(1u, 0xFEDCBA98u, 0x76543210u)]
    public void ResourceEvaluationPreservesOrReplacesBothDestinationWords(
        uint condition,
        uint expectedLow,
        uint expectedHigh)
    {
        var program = CreateProgram();
        var plan = Extract(program);
        var userData = new uint[13];
        userData[4] = 0x12345678;
        userData[5] = 0x9ABCDEF0;
        userData[10] = 0xFEDCBA98;
        userData[11] = 0x76543210;
        userData[12] = condition;

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(
            plan, plan.Info.Buffers[0].Source, Inputs(userData), out var descriptor));
        Assert.Equal(expectedLow, descriptor.Dwords[0]);
        Assert.Equal(expectedHigh, descriptor.Dwords[1]);
    }

    [Fact]
    public void BackendsCompileConditionalMoveAndRetainOldDestinationInput()
    {
        var program = CreateProgram();
        var requiredRegisters = BindingLayout.CollectUserDataRegisters(program, 0, 13);
        Assert.Contains(4u, requiredRegisters);
        Assert.Contains(5u, requiredRegisters);

        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    private static Gen5ShaderProgram CreateProgram() => Program(
        Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(12), Operand(1)),
        Sop1(4, "SCmovB64", 4, Gen5Operand.Scalar(10)),
        BufferLoad(8, 4),
        EndProgram(16));

    private sealed class InstructionMemory(byte[] instructionBytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > instructionBytes.Length || address - 0x1000 >
                (ulong)(instructionBytes.Length - destination.Length))
            {
                return false;
            }

            instructionBytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

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

public sealed class Gen5AlignBitTests
{
    private const uint SourcesV1V2V3 = 0x040E0501;

    [Theory]
    [InlineData(0xD54E0005u, 0u)]
    [InlineData(0xD54E1805u, 3u)]
    public void Opcode14EDecodesAsAlignBitWithByteSelector(uint word, uint byteSelector)
    {
        var instruction = Decode(word, SourcesV1V2V3);

        Assert.Equal("VAlignbitB32", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal(
            new[]
            {
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
            },
            instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(5), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5Vop3Control>(instruction.Control);
        Assert.Equal(byteSelector, control.OperandSelect & 0x3u);
    }

    [Theory]
    [InlineData(0xD54E0005u, false)]
    [InlineData(0xD54E1805u, true)]
    public void BothBackendsLowerFunnelShiftAndProtectZeroShift(
        uint word,
        bool selectsHighByte)
    {
        var align = Decode(word, SourcesV1V2V3);
        var program = Program(align with { Pc = 0 }, EndProgram(2 * sizeof(uint)));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var spirv, out var spirvError),
            spirvError);
        var opcodes = new SpirvModuleInspector(spirv.Spirv).Opcodes;
        Assert.Contains((ushort)SpirvOp.ShiftLeftLogical, opcodes);
        Assert.Contains((ushort)SpirvOp.ShiftRightLogical, opcodes);
        Assert.Contains((ushort)SpirvOp.Select, opcodes);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        Assert.Contains("== 0u ?", metal.Source, StringComparison.Ordinal);
        Assert.Contains(" << ", metal.Source, StringComparison.Ordinal);
        Assert.Contains(" >> ", metal.Source, StringComparison.Ordinal);
        Assert.Equal(
            selectsHighByte,
            metal.Source.Contains(">> 24u", StringComparison.Ordinal));
    }

    private static Gen5ShaderInstruction Decode(params uint[] words)
    {
        var bytes = new byte[(words.Length + 1) * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(words.Length * sizeof(uint)),
            0xBF810000);
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

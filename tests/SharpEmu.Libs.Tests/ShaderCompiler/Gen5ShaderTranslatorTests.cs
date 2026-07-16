// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.ShaderCompiler;

public sealed class Gen5ShaderTranslatorTests
{
    private const ulong ProgramAddress = 0x1_0000_0000;

    [Theory]
    [InlineData(0xD7600005u, 5u)]
    [InlineData(0xD7600065u, 101u)]
    public void VReadlaneB32DecodesScalarDestinationFromVdstByte(
        uint instructionWord,
        uint expectedDestination)
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        Span<byte> code = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(code, instructionWord);
        BinaryPrimitives.WriteUInt32LittleEndian(code[sizeof(uint)..], 0x02000501u);
        BinaryPrimitives.WriteUInt32LittleEndian(code[(2 * sizeof(uint))..], 0xBF810000u);
        Assert.True(memory.TryWrite(ProgramAddress, code));

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = Assert.Single(
            program.Instructions,
            static item => item.Opcode == "VReadlaneB32");
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);

        var destination = Assert.Single(instruction.Destinations);
        Assert.Equal(Gen5OperandKind.ScalarRegister, destination.Kind);
        Assert.Equal(expectedDestination, destination.Value);

        Assert.Equal(Gen5OperandKind.VectorRegister, instruction.Sources[0].Kind);
        Assert.Equal(1u, instruction.Sources[0].Value);
        Assert.Equal(Gen5OperandKind.ScalarRegister, instruction.Sources[1].Kind);
        Assert.Equal(2u, instruction.Sources[1].Value);
    }
}

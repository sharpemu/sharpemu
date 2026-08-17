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

    [Theory]
    [InlineData(0xBF970001u, "SCbranchCdbgsys")]
    [InlineData(0xBF980001u, "SCbranchCdbguser")]
    [InlineData(0xBF990001u, "SCbranchCdbgsysOrUser")]
    [InlineData(0xBF9A0001u, "SCbranchCdbgsysAndUser")]
    public void DebugConditionBranchesDecode(uint instructionWord, string expectedOpcode)
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        Span<byte> code = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(code, instructionWord);
        BinaryPrimitives.WriteUInt32LittleEndian(code[sizeof(uint)..], 0xBF800000u);
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

        Assert.Equal(expectedOpcode, program.Instructions[0].Opcode);
    }

    [Theory]
    [InlineData(0xBBFD0000u)]
    [InlineData(0xBC7D0000u)]
    [InlineData(0xBCFD0000u)]
    [InlineData(0xBD7D0000u)]
    public void SopkWaitCounterFormsDecodeWithoutScalarDestination(uint instructionWord)
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        Span<byte> code = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(code, instructionWord);
        BinaryPrimitives.WriteUInt32LittleEndian(code[sizeof(uint)..], 0xBF810000u);
        Assert.True(memory.TryWrite(ProgramAddress, code));

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sopk, instruction.Encoding);
        Assert.Equal("SWaitcnt", instruction.Opcode);
        Assert.Empty(instruction.Destinations);
        Assert.Single(instruction.Sources);
        Assert.Equal(Gen5OperandKind.EncodedConstant, instruction.Sources[0].Kind);
    }

    [Fact]
    public void FusedProgramContinuesAfterSetProgramCounter()
    {
        const ulong continuationAddress = ProgramAddress + 0x100;
        const ulong entryHeaderAddress = ProgramAddress + 0x400;
        const ulong continuationHeaderAddress = ProgramAddress + 0x500;
        var memory = new FakeCpuMemory(ProgramAddress, 0x1000);

        WriteWords(memory, ProgramAddress, 0xBF800000u, 0xBE802000u);
        WriteWords(memory, continuationAddress, 0xBF800000u, 0xBF810000u);
        WriteUInt32(memory, entryHeaderAddress + 0x44, 2 * sizeof(uint));
        WriteUInt32(memory, continuationHeaderAddress + 0x44, 2 * sizeof(uint));

        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderTranslator.RegisterFusedProgram(
            context,
            ProgramAddress,
            entryHeaderAddress,
            continuationAddress,
            continuationHeaderAddress);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        Assert.Equal(
            ["SNop", "SNop", "SNop", "SEndpgm"],
            program.Instructions.Select(static instruction => instruction.Opcode));
        Assert.Equal(
            [0u, 4u, 0x100u, 0x104u],
            program.Instructions.Select(static instruction => instruction.Pc));
    }

    private static void WriteWords(FakeCpuMemory memory, ulong address, params uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint), sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}

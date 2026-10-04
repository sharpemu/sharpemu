// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.Libs.Tests.ShaderCompiler;

public sealed class Gen5ShaderTranslatorTests
{
    private const ulong ProgramAddress = 0x1_0000_0000;

    [Fact]
    public void Vop2OpcodeZeroDecodesAsNoop()
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        WriteWords(memory, ProgramAddress, 0x00000000u, 0xBF810000u);

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Vop2, instruction.Encoding);
        Assert.Equal("VNop", instruction.Opcode);
        Assert.Equal(0u, instruction.Pc);
    }

    [Fact]
    public void Vop2FmamkF16ConsumesLiteralDword()
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        WriteWords(memory, ProgramAddress, 0x6F104E10u, 0x3C003C00u, 0xBF810000u);

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Vop2, instruction.Encoding);
        Assert.Equal("VFmaMkF16", instruction.Opcode);
        Assert.Equal(2, instruction.Words.Count);
        Assert.Equal(3, instruction.Sources.Count);
        Assert.Equal(Gen5OperandKind.LiteralConstant, instruction.Sources[1].Kind);
        Assert.Equal(0x3C003C00u, instruction.Sources[1].Value);
    }

    [Fact]
    public void Sop2ScalarFloatSubtractDecodes()
    {
        const uint instructionWord =
            (2u << 30) | (0x41u << 23) | (3u << 16) | (2u << 8) | 1u;
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        WriteWords(memory, ProgramAddress, instructionWord, 0xBF810000u);

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Sop2, instruction.Encoding);
        Assert.Equal("SSubF32", instruction.Opcode);
        Assert.Equal(3u, instruction.Destinations[0].Value);
    }

    [Fact]
    public void ScalarScratchStoreDwordx2DecodesAsStore()
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        WriteWords(memory, ProgramAddress, 0xC59B2040u, 0xBF810000u);

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Smrd, instruction.Encoding);
        Assert.Equal("ScratchStoreDwordx2", instruction.Opcode);
        Assert.Empty(instruction.Destinations);
        Assert.Equal(4, instruction.Sources.Count);
        Assert.Equal(32u, instruction.Sources[0].Value);
        Assert.Equal(54u, instruction.Sources[1].Value);
        Assert.Equal(55u, instruction.Sources[2].Value);
        Assert.Equal(64u, instruction.Sources[3].Value);
        var control = Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control);
        Assert.Equal(2u, control.DwordCount);
        Assert.Equal(32u, control.ScalarAddress);
        Assert.Equal(54u, control.SourceVectorRegister);
        Assert.Equal(64u, control.DynamicOffsetRegister);
        Assert.True(control.SourceIsScalar);
    }

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
    public void SopkSetregB32DecodesScalarSourceWithoutDestination()
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        Span<byte> code = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(code, 0xB9851234u);
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
        Assert.Equal("SSetregB32", instruction.Opcode);
        Assert.Empty(instruction.Destinations);
        Assert.Equal(2, instruction.Sources.Count);
        Assert.Equal(Gen5OperandKind.ScalarRegister, instruction.Sources[0].Kind);
        Assert.Equal(5u, instruction.Sources[0].Value);
        Assert.Equal(Gen5OperandKind.EncodedConstant, instruction.Sources[1].Kind);
        Assert.Equal(0x1234u, instruction.Sources[1].Value);
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
            [0u, 4u, 8u, 12u],
            program.Instructions.Select(static instruction => instruction.Pc));
        Assert.Null(program.Instructions[0].AddressOffset);
        Assert.Null(program.Instructions[1].AddressOffset);
        Assert.Equal(0x100UL, program.Instructions[2].AddressOffset);
        Assert.Equal(0x104UL, program.Instructions[3].AddressOffset);
    }

    private sealed class TwoRegionMemory(FakeCpuMemory first, FakeCpuMemory second) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            first.TryRead(virtualAddress, destination) || second.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            first.TryWrite(virtualAddress, source) || second.TryWrite(virtualAddress, source);
    }

    [Theory]
    [InlineData(0x1_8000_0000L)]
    [InlineData(-0x1000L)]
    public void FusedProgramKeepsAFarContinuationsAddressForGetPc(long distance)
    {
        var continuationAddress = unchecked(ProgramAddress + (ulong)distance);
        const ulong entryHeaderAddress = ProgramAddress + 0x400;
        var continuationHeaderAddress = continuationAddress + 0x400;
        var entry = new FakeCpuMemory(ProgramAddress, 0x800);
        var continuation = new FakeCpuMemory(continuationAddress, 0x800);
        WriteWords(entry, ProgramAddress, 0xBF800000u, 0xBE802000u);
        WriteWords(continuation, continuationAddress, 0xBE801F00u, 0xBF810000u);
        WriteUInt32(entry, entryHeaderAddress + 0x44, 2 * sizeof(uint));
        WriteUInt32(continuation, continuationHeaderAddress + 0x44, 2 * sizeof(uint));

        var context = new CpuContext(new TwoRegionMemory(entry, continuation), Generation.Gen5);
        Gen5ShaderTranslator.RegisterFusedProgram(context, ProgramAddress, entryHeaderAddress, continuationAddress, continuationHeaderAddress);

        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ProgramAddress, out var program, out var error), error);
        Assert.Equal(["SNop", "SNop", "SGetpcB64", "SEndpgm"], program.Instructions.Select(static instruction => instruction.Opcode));
        Assert.Equal([0u, 4u, 8u, 12u], program.Instructions.Select(static instruction => instruction.Pc));
        Assert.Equal((ulong)distance, program.Instructions[2].ProgramOffset);
        Assert.Equal(unchecked((ulong)distance + 4), program.Instructions[3].ProgramOffset);
        Assert.Equal(continuationAddress, unchecked(program.Address + program.Instructions[2].ProgramOffset));
    }

    [Fact]
    public void FusedProgramMoreThanFourGiBApartUsesLogicalPcsAndPhysicalGetpcOffset()
    {
        // Addresses captured from Poppy Playtime: Chapter 3. The 0x17FBD5D00
        // byte gap is 6.44 decimal GB and cannot fit in the uint logical Pc.
        const ulong entryAddress = 0x0000_0015_C139_B700;
        const ulong continuationAddress = 0x0000_0017_40F7_1400;
        const ulong entryHeaderAddress = entryAddress + 0x100;
        const ulong continuationHeaderAddress = continuationAddress + 0x100;
        const ulong continuationOffset = continuationAddress - entryAddress;
        var memory = new SparseCpuMemory(
            (entryAddress, 0x1000),
            (continuationAddress, 0x1000));

        WriteWords(memory, entryAddress, 0xBF800000u, 0xBE802000u);
        WriteWords(
            memory,
            continuationAddress,
            0xBE801F00u,
            0xBF820001u,
            0xBF800000u,
            0xBF810000u);
        WriteUInt32(memory, entryHeaderAddress + 0x44, 2 * sizeof(uint));
        WriteUInt32(memory, continuationHeaderAddress + 0x44, 4 * sizeof(uint));

        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderTranslator.RegisterFusedProgram(
            context,
            entryAddress,
            entryHeaderAddress,
            continuationAddress,
            continuationHeaderAddress);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                entryAddress,
                out var program,
                out var error),
            error);

        Assert.Equal(
            ["SNop", "SNop", "SGetpcB64", "SBranch", "SNop", "SEndpgm"],
            program.Instructions.Select(static instruction => instruction.Opcode));
        Assert.Equal(
            [0u, 4u, 8u, 12u, 16u, 20u],
            program.Instructions.Select(static instruction => instruction.Pc));
        var getpc = program.Instructions[2];
        Assert.Equal(continuationOffset, getpc.GuestProgramCounterOffset);
        Assert.Equal(continuationOffset + sizeof(uint), getpc.NextGuestProgramCounterOffset);
        Assert.True(
            Gen5IrBranchResolver.Instance.TryGetBranchTarget(
                program.Instructions[3],
                out var target));
        Assert.Equal(20u, target);
    }

    [Fact]
    public void FusedContinuationBeforeEntryPreservesItsGuestGetpcAddress()
    {
        const ulong entryAddress = 0x0000_0002_0000_1000;
        const ulong continuationAddress = 0x0000_0001_0000_0000;
        const ulong entryHeaderAddress = entryAddress + 0x100;
        const ulong continuationHeaderAddress = continuationAddress + 0x100;
        var memory = new SparseCpuMemory(
            (entryAddress, 0x1000),
            (continuationAddress, 0x1000));

        WriteWords(memory, entryAddress, 0xBF800000u, 0xBE802000u);
        WriteWords(memory, continuationAddress, 0xBE801F00u, 0xBF810000u);
        WriteUInt32(memory, entryHeaderAddress + 0x44, 2 * sizeof(uint));
        WriteUInt32(memory, continuationHeaderAddress + 0x44, 2 * sizeof(uint));
        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderTranslator.RegisterFusedProgram(
            context,
            entryAddress,
            entryHeaderAddress,
            continuationAddress,
            continuationHeaderAddress);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                entryAddress,
                out var program,
                out var error),
            error);

        Assert.Equal([0u, 4u, 8u, 12u], program.Instructions.Select(static instruction => instruction.Pc));
        var getpc = program.Instructions[2];
        Assert.Equal(
            continuationAddress + sizeof(uint),
            unchecked(entryAddress + getpc.NextGuestProgramCounterOffset));
    }

    [Fact]
    public void FusedProgramRegistrationCrossesMemoryWrapper()
    {
        const ulong continuationAddress = ProgramAddress + 0x100;
        const ulong entryHeaderAddress = ProgramAddress + 0x400;
        const ulong continuationHeaderAddress = ProgramAddress + 0x500;
        var memory = new FakeCpuMemory(ProgramAddress, 0x1000);

        WriteWords(memory, ProgramAddress, 0xBF800000u, 0xBE802000u);
        WriteWords(memory, continuationAddress, 0xBF800000u, 0xBF810000u);
        WriteUInt32(memory, entryHeaderAddress + 0x44, 2 * sizeof(uint));
        WriteUInt32(memory, continuationHeaderAddress + 0x44, 2 * sizeof(uint));

        var registrationContext = new CpuContext(new TrackedCpuMemory(memory), Generation.Gen5);
        Gen5ShaderTranslator.RegisterFusedProgram(
            registrationContext,
            ProgramAddress,
            entryHeaderAddress,
            continuationAddress,
            continuationHeaderAddress);

        var decodeContext = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryGetFusedProgramParts(
                decodeContext,
                ProgramAddress,
                out var registeredContinuationAddress,
                out var registeredContinuationHeaderAddress));
        Assert.Equal(continuationAddress, registeredContinuationAddress);
        Assert.Equal(continuationHeaderAddress, registeredContinuationHeaderAddress);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                decodeContext,
                ProgramAddress,
                out var program,
                out var error),
            error);

        Assert.Equal(
            ["SNop", "SNop", "SNop", "SEndpgm"],
            program.Instructions.Select(static instruction => instruction.Opcode));
    }

    private static void WriteWords(ICpuMemory memory, ulong address, params uint[] words)
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

    private static void WriteUInt32(ICpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}

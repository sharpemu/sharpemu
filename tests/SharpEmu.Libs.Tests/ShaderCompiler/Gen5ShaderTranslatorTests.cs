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

    /// <summary>
    /// VOP3 re-encodes the whole VOPC opcode space so a compare can carry source modifiers, a
    /// clamp, or a destination other than VCC. Before these were decoded they produced an unnamed
    /// stub, which failed translation for the entire shader and silently dropped every draw
    /// bound to it.
    /// </summary>
    [Fact]
    public void Vop3EncodedCompareDecodesThroughTheVopcTable()
    {
        // v_cmp_lt_f32_e64 s[4:5], v0, v1
        var instruction = DecodeSingle(0xD4010104u, 0x00020300u);

        Assert.Equal("VCmpLtF32", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);

        // The _e64 form exists precisely so the result can go somewhere other than VCC.
        var destination = Assert.Single(instruction.Destinations);
        Assert.Equal(Gen5OperandKind.ScalarRegister, destination.Kind);
        Assert.Equal(4u, destination.Value);
    }

    /// <summary>
    /// The VOP1 space is aliased at VOP3 opcode 0x180 + n, and unlike a compare it keeps a vector
    /// destination.
    /// </summary>
    [Fact]
    public void Vop3EncodedVop1DecodesThroughTheVop1Table()
    {
        // v_rcp_f32_e64 v2, v0
        var instruction = DecodeSingle(0xD5AA0002u, 0x00000100u);

        Assert.Equal("VRcpF32", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);

        var destination = Assert.Single(instruction.Destinations);
        Assert.Equal(Gen5OperandKind.VectorRegister, destination.Kind);
        Assert.Equal(2u, destination.Value);
    }

    /// <summary>
    /// VCMPX is EXEC-only on GFX10, so it must keep the vector-destination path and be routed to
    /// the wave mask by the emitters rather than picking up an SGPR destination.
    /// </summary>
    [Fact]
    public void Vop3EncodedCmpxKeepsItsVectorDestination()
    {
        // v_cmpx_lt_f32_e64 with vdst byte 0x04
        var instruction = DecodeSingle(0xD4110104u, 0x00020300u);

        Assert.StartsWith("VCmpx", instruction.Opcode, StringComparison.Ordinal);
        Assert.Equal(Gen5OperandKind.VectorRegister, Assert.Single(instruction.Destinations).Kind);
    }

    /// <summary>
    /// Both alias ranges must resolve through their tables rather than falling through to the
    /// unnamed stub. Opcodes the tables do not cover are expected to fail decode, and the error
    /// has to name the VOP3 opcode — reporting the aliased VOPC/VOP1 number instead would hide
    /// which opcode actually needs implementing.
    /// </summary>
    [Theory]
    [InlineData(0x001u)] // VOPC alias  -> v_cmp_lt_f32
    [InlineData(0x0C1u)] // VOPC alias  -> v_cmp_lt_u32
    [InlineData(0x1AAu)] // VOP1 alias  -> v_rcp_f32
    [InlineData(0x1A1u)] // VOP1 alias  -> v_cvt_f32_i32
    public void AliasRangesNeverDecodeToAnUnnamedStub(uint vop3Opcode)
    {
        var word = 0xD4000000u | (vop3Opcode << 16);
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        Span<byte> code = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(code, word);
        BinaryPrimitives.WriteUInt32LittleEndian(code[sizeof(uint)..], 0x00020300u);
        BinaryPrimitives.WriteUInt32LittleEndian(code[(2 * sizeof(uint))..], 0xBF810000u);
        Assert.True(memory.TryWrite(ProgramAddress, code));

        var context = new CpuContext(memory, Generation.Gen5);
        var decoded = Gen5ShaderTranslator.TryDecodeProgram(
            context,
            ProgramAddress,
            out var program,
            out var error);

        if (decoded)
        {
            Assert.DoesNotContain(
                program.Instructions,
                static item => item.Opcode.StartsWith("Vop3Raw", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("unknown-vop3", error, StringComparison.Ordinal);
        }
    }

    private static Gen5ShaderInstruction DecodeSingle(uint word, uint extra)
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        Span<byte> code = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(code, word);
        BinaryPrimitives.WriteUInt32LittleEndian(code[sizeof(uint)..], extra);
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

        return program.Instructions[0];
    }
}

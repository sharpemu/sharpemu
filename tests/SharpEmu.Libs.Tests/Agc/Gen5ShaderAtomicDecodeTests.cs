// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Decodes synthetic GFX10 atomic instructions and checks the opcode names and operand wiring
// that the SPIR-V translator relies on. Word layouts follow the RDNA2 ISA manual:
// MUBUF op=word0[24:18], MIMG op=word0[24:18], DS op=word0[25:18].
public sealed class Gen5ShaderAtomicDecodeTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint EndPgm = 0xBF810000;

    [Fact]
    public void BufferAtomicUmax_DecodesControlAndDestination()
    {
        // BUFFER_ATOMIC_UMAX v1, off, s[0:3], 128 offset:8 glc
        var instruction = DecodeSingle(0xE0E04008, 0x80000100);

        Assert.Equal("BufferAtomicUmax", instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(1u, control.DwordCount);
        Assert.Equal(1u, control.VectorData);
        Assert.Equal(0u, control.ScalarResource);
        Assert.Equal(8, control.OffsetBytes);
        Assert.True(control.Glc);
        Assert.Equal(new[] { Gen5Operand.Vector(1) }, instruction.Destinations);
    }

    [Fact]
    public void BufferAtomicCmpswap_UsesTwoDataRegisters()
    {
        // BUFFER_ATOMIC_CMPSWAP v[1:2], off, s[0:3], 128 glc
        var instruction = DecodeSingle(0xE0C44000, 0x80000100);

        Assert.Equal("BufferAtomicCmpswap", instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(2u, control.DwordCount);
        Assert.Equal(1u, control.VectorData);
    }

    [Theory]
    [InlineData(0xE0FC4000u, "BufferAtomicFmin")]
    [InlineData(0xE1004000u, "BufferAtomicFmax")]
    public void BufferAtomicFloatMinMax_Decode(uint word, string opcode)
    {
        var instruction = DecodeSingle(word, 0x80000100);

        Assert.Equal(opcode, instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(1u, control.DwordCount);
        Assert.Equal(1u, control.VectorData);
        Assert.Equal(0u, control.ScalarResource);
    }

    [Fact]
    public void BufferAtomicOrX2_UsesTwoDataRegisters()
    {
        // BUFFER_ATOMIC_OR_X2 v[1:2], off, s[0:3], 128 glc
        var instruction = DecodeSingle(0xE1684000, 0x80000100);

        Assert.Equal("BufferAtomicOrX2", instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(2u, control.DwordCount);
        Assert.Equal(1u, control.VectorData);
        Assert.Equal(
            new[] { Gen5Operand.Vector(1), Gen5Operand.Vector(2) },
            instruction.Destinations);
    }

    [Fact]
    public void BufferAtomicSwapX2_UsesTwoDataRegisters()
    {
        // BUFFER_ATOMIC_SWAP_X2 v[1:2], off, s[0:3], 128 glc
        var instruction = DecodeSingle(0xE1404000, 0x80000100);

        Assert.Equal("BufferAtomicSwapX2", instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(2u, control.DwordCount);
        Assert.Equal(1u, control.VectorData);
        Assert.Equal(new[] { Gen5Operand.Vector(1), Gen5Operand.Vector(2) }, instruction.Destinations);
    }

    [Fact]
    public void ImageAtomicAdd_KeepsDataRegisterAsDestination()
    {
        // IMAGE_ATOMIC_ADD v2, v[0:1], s[4:11] dmask:0x1 dim:2D glc
        var instruction = DecodeSingle(0xF0442100, 0x00010200);

        Assert.Equal("ImageAtomicAdd", instruction.Opcode);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(2u, control.VectorData);
        Assert.Equal(4u, control.ScalarResource);
        Assert.True(control.Glc);
        Assert.Equal(new[] { Gen5Operand.Vector(2) }, instruction.Destinations);
    }

    [Fact]
    public void ImageBvhIntersectRay_UsesOpcodeBitSevenFromOpm()
    {
        // IMAGE_BVH_INTERSECT_RAY v[12:15], v[0:10], s[4:7] (op 0xE6: word0[24:18]=0x66, OPM=1)
        var instruction = DecodeSingle(0xF1980F01, 0x00010C00);

        Assert.Equal("ImageBvhIntersectRay", instruction.Opcode);
        var control = Assert.IsType<Gen5RayIntersectControl>(instruction.Control);
        Assert.Equal(12u, control.VectorData);
        Assert.Equal(4u, control.ScalarResource);
        Assert.Equal(
            Enumerable.Range(12, 4).Select(index => Gen5Operand.Vector((uint)index)),
            instruction.Destinations);
        Assert.Equal(
            Enumerable.Range(0, 11).Select(index => Gen5Operand.Vector((uint)index)).Append(Gen5Operand.Scalar(4)),
            instruction.Sources);
    }

    [Fact]
    public void DsAppend_ReadsTheGdsBitAtBit17()
    {
        // DS_APPEND v0 offset:16 gds (Astro Bot's indirect-dispatch producer)
        var instruction = DecodeSingle(0xD8FA0010, 0x00000000);

        Assert.Equal("DsAppend", instruction.Opcode);
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.True(control.Gds);
        Assert.Equal(16u, control.SingleOffsetBytes);
    }

    [Fact]
    public void Vop3CompareGtU64_WritesTheScalarPairInVdst()
    {
        // V_CMP_GT_U64 s[4:5], v[0:1], v[2:3] (VOP3 op 0x0E4)
        var instruction = DecodeSingle(0xD4E40004, 0x00020500);

        Assert.Equal("VCmpGtU64", instruction.Opcode);
        var control = Assert.IsType<Gen5Vop3Control>(instruction.Control);
        Assert.Equal(4u, control.ScalarDestination);
        Assert.Equal(new[] { Gen5Operand.Scalar(4) }, instruction.Destinations);
        Assert.Equal(new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(2) }, instruction.Sources);
    }

    [Fact]
    public void SSetprio_Decodes()
    {
        // S_SETPRIO 3 (SOPP op 0x0F)
        var instruction = DecodeSingle(0xBF8F0003);

        Assert.Equal("SSetprio", instruction.Opcode);
    }

    [Fact]
    public void DsAddU32_HasAddressAndDataSourcesButNoDestination()
    {
        // DS_ADD_U32 v0, v1
        var instruction = DecodeSingle(0xD8000000, 0x00000100);

        Assert.Equal("DsAddU32", instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1) },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Fact]
    public void DsAddRtnU32_WritesReturnRegister()
    {
        // DS_ADD_RTN_U32 v3, v0, v1
        var instruction = DecodeSingle(0xD8800000, 0x03000100);

        Assert.Equal("DsAddRtnU32", instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1) },
            instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(3) }, instruction.Destinations);
    }

    [Theory]
    [InlineData(0xD8480004u, "DsMinF32")]
    [InlineData(0xD84C0008u, "DsMaxF32")]
    public void DsFloatMinMax_KeepReplacementAndCompareOperands(uint word, string opcode)
    {
        // DATA0 is the replacement value and DATA1 is the float compare operand.
        var instruction = DecodeSingle(word, 0x00010907);

        Assert.Equal(opcode, instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(7), Gen5Operand.Vector(9), Gen5Operand.Vector(1) },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Fact]
    public void DsCmpstRtnB32_OrdersComparatorBeforeNewValue()
    {
        // DS_CMPST_RTN_B32 v3, v0, v1, v2 - DATA0 (v1) is the comparator, DATA1 (v2) the
        // new value, reversed relative to buffer/image cmpswap.
        var instruction = DecodeSingle(0xD8C00000, 0x03020100);

        Assert.Equal("DsCmpstRtnB32", instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2) },
            instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(3) }, instruction.Destinations);
    }

    [Fact]
    public void DsWriteB32_CombinesBothOffsetBytes()
    {
        // DS_WRITE_B32 v0, v1 offset:0x0808.
        var instruction = DecodeSingle(0xD8340808, 0x00000100);

        Assert.Equal("DsWriteB32", instruction.Opcode);
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0x08u, control.Offset0);
        Assert.Equal(0x08u, control.Offset1);
        Assert.Equal(0x0808u, control.SingleOffsetBytes);
    }

    [Fact]
    public void DsReadI8_DecodesAddressAndDestination()
    {
        // DS_READ_I8 v5, v7 offset:3
        var instruction = DecodeSingle(0xD8E40003, 0x05000007);

        Assert.Equal("DsReadI8", instruction.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(7) }, instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(5) }, instruction.Destinations);
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(3u, control.SingleOffsetBytes);
    }

    [Theory]
    [InlineData(0xD8F83412u, "DsAppend")]
    [InlineData(0xD8F43412u, "DsConsume")]
    public void DsWaveCounter_UsesM0AndReturnsOldValue(uint word, string opcode)
    {
        var instruction = DecodeSingle(word, 0x07000000);

        Assert.Equal(opcode, instruction.Opcode);
        Assert.Equal(new[] { Gen5Operand.Scalar(124) }, instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(7) }, instruction.Destinations);
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0x12u, control.Offset0);
        Assert.Equal(0x34u, control.Offset1);
        Assert.Equal(0x3412u, control.SingleOffsetBytes);
        Assert.False(control.Gds);
    }

    [Fact]
    public void VCmpxNeU64_DecodesVectorRegisterPairs()
    {
        // V_CMPX_NE_U64 v[0:1], v[3:4]. The translator consumes each encoded
        // source as the low register of a 64-bit pair and updates EXEC.
        var instruction = DecodeSingle(0x7DEA0700);

        Assert.Equal("VCmpxNeU64", instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(3) },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Fact]
    public void VCmpNeU64_DecodesVectorRegisterPairs()
    {
        // V_CMP_NE_U64 v[0:1], v[3:4].
        var instruction = DecodeSingle(0x7DCA0700);

        Assert.Equal("VCmpNeU64", instruction.Opcode);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(3) },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Fact]
    public void VFfbhU32_DecodesVop1Opcode39()
    {
        // V_FFBH_U32 v1, v2.
        var instruction = DecodeSingle(0x7E027302);

        Assert.Equal("VFfbhU32", instruction.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(2) }, instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(1) }, instruction.Destinations);
    }

    [Fact]
    public void VCmpxClassF32_DecodesVopcOpcode98()
    {
        var instruction = DecodeSingle(0x7D300702);

        Assert.Equal("VCmpxClassF32", instruction.Opcode);
    }

    [Fact]
    public void VAlignbitB32_DecodesVop3Opcode14E()
    {
        var instruction = DecodeSingle(0xD14E0001, 0x040E0502);

        Assert.Equal("VAlignbitB32", instruction.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(3) }, instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(1) }, instruction.Destinations);
    }

    private static Gen5ShaderInstruction DecodeSingle(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteProgram(memory, ShaderAddress, words);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program.Instructions[0];
    }

    internal static void WriteProgram(FakeCpuMemory memory, ulong address, uint[] words)
    {
        Span<byte> buffer = stackalloc byte[4];
        foreach (var word in words.Append(EndPgm))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, word);
            Assert.True(memory.TryWrite(address, buffer));
            address += sizeof(uint);
        }
    }
}

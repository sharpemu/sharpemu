// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.ShaderCompiler;

public sealed class Gen5DataShareDecodeTests
{
    private const ulong ProgramAddress = 0x1_0000_0000;

    [Fact]
    public void DsWriteAddtidB32DecodesM0AndDataWithoutAddressVgpr()
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        Span<byte> code = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(code, 0xDAC01234u);
        BinaryPrimitives.WriteUInt32LittleEndian(code[sizeof(uint)..], 0x00002A11u);
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
            static item => item.Opcode == "DsWriteAddtidB32");
        Assert.Equal(Gen5ShaderEncoding.Ds, instruction.Encoding);
        Assert.Empty(instruction.Destinations);

        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0x34u, control.Offset0);
        Assert.Equal(0x12u, control.Offset1);
        Assert.False(control.Gds);

        Assert.Collection(
            instruction.Sources,
            source =>
            {
                Assert.Equal(Gen5OperandKind.ScalarRegister, source.Kind);
                Assert.Equal(124u, source.Value);
            },
            source =>
            {
                Assert.Equal(Gen5OperandKind.VectorRegister, source.Kind);
                Assert.Equal(42u, source.Value);
            });
        Assert.DoesNotContain(
            instruction.Sources,
            static source =>
                source.Kind == Gen5OperandKind.VectorRegister && source.Value == 17);
    }
}

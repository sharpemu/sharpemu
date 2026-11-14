// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// V_MIN, V_MAX, V_MIN3, V_MAX3 and V_MED3 give the same bits in a module that declares SignedZeroInfNanPreserve as in one
// that does not, over every ordered triple of a value set (NaN payloads of both signs, quiet and signaling, zeros,
// subnormals, infinities, normals); GLSL NMin and NMax did not (two NaNs, subnormals). A NaN operand gives the RDNA2 result
// (IEEE mode off): a NaN S0 gives S1, a NaN S1 gives S0, so two NaNs give S1.
public sealed class FloatMinMaxLoweringDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const int RowBytes = 32;
    private const uint Lanes = 1024;

    private static readonly uint[] Float32Values =
    [
        0x7FC0_0000, 0xFFC0_0000, 0x7FC0_0001, 0x7FFF_FFFF, 0xFFFF_FFFF, 0x7FD2_3456,
        0x7F80_0001, 0xFF80_0001, 0x7FA0_0000, 0xFFBF_FFFF,
        0x0000_0000, 0x8000_0000, 0x0000_0001, 0x807F_FFFF,
        0x7F80_0000, 0xFF80_0000, 0x3F80_0000, 0xBF80_0000, 0x0080_0000, 0x7F7F_FFFF,
    ];

    private static readonly uint[] Float16Values =
    [
        0x7E00, 0xFE00, 0x7E01, 0x7FFF, 0xFFFF, 0x7C01, 0xFC01, 0x7D00,
        0x0000, 0x8000, 0x0001, 0x83FF, 0x7C00, 0xFC00, 0x3C00, 0xBC00, 0x0400, 0x7BFF,
    ];

    [Fact]
    public void Float32LoweringsAgreeAndFollowTheGuestNanRule()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Program(
            Vop2(0, "VAddU32", 1, Gen5Operand.Scalar(8), Gen5Operand.Vector(0)),
            Vop2(4, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(1)),
            BufferAccess(8, "BufferLoadDwordx3", 4, dwords: 3, vectorData: 8, offsetEnabled: true, vectorAddress: 7),
            Vop2(16, "VMinF32", 11, Gen5Operand.Vector(8), Gen5Operand.Vector(9)),
            Vop2(20, "VMaxF32", 12, Gen5Operand.Vector(8), Gen5Operand.Vector(9)),
            Vop3(24, "VMin3F32", 13, Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            Vop3(32, "VMax3F32", 14, Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            Vop3(40, "VMed3F32", 15, Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            BufferAccess(48, "BufferStoreDwordx4", 4, offset: 12, dwords: 4, vectorData: 11, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(56, "BufferStoreDword", 4, offset: 28, vectorData: 15, offsetEnabled: true, vectorAddress: 7),
            EndProgram(64));
        var rows = Rows(Float32Values);
        var bitTests = Run(vulkan, program, rows, preserveNan: false);
        var preserving = Run(vulkan, program, rows, preserveNan: true);
        var count = Float32Values.Length * Float32Values.Length * Float32Values.Length;
        for (var row = 0; row < count; row++)
        {
            var (a, b, c) = (Word(rows, row, 0), Word(rows, row, 1), Word(rows, row, 2));
            for (var output = 0; output < 5; output++)
            {
                Assert.True(Word(bitTests, row, 3 + output) == Word(preserving, row, 3 + output),
                    $"output {output} of ({a:X8}, {b:X8}, {c:X8}): bit tests {Word(bitTests, row, 3 + output):X8}, " +
                    $"NaN-preserving {Word(preserving, row, 3 + output):X8}");
            }

            if (IsNan(a) || IsNan(b))
            {
                Assert.Equal(Minimum(a, b), Word(preserving, row, 3));
                Assert.Equal(Maximum(a, b), Word(preserving, row, 4));
            }
        }
    }

    [Fact]
    public void Float16LoweringsAgree()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Program(
            Vop2(0, "VAddU32", 1, Gen5Operand.Scalar(8), Gen5Operand.Vector(0)),
            Vop2(4, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(1)),
            BufferAccess(8, "BufferLoadDwordx3", 4, dwords: 3, vectorData: 8, offsetEnabled: true, vectorAddress: 7),
            Vop2(16, "VMaxF16", 11, Gen5Operand.Vector(8), Gen5Operand.Vector(9)),
            Vop3(20, "VMax3F16", 12, Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            Vop3(28, "VMed3F16", 13, Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            BufferAccess(36, "BufferStoreDwordx3", 4, offset: 12, dwords: 3, vectorData: 11, offsetEnabled: true, vectorAddress: 7),
            EndProgram(44));
        var rows = Rows(Float16Values);
        var bitTests = Run(vulkan, program, rows, preserveNan: false);
        var preserving = Run(vulkan, program, rows, preserveNan: true);
        Assert.Equal(bitTests, preserving);
    }

    private static bool IsNan(uint bits) => (bits & 0x7FFF_FFFF) > 0x7F80_0000;

    private static uint Minimum(uint a, uint b) => IsNan(a) ? b : IsNan(b) ? a
        : BitConverter.UInt32BitsToSingle(a) < BitConverter.UInt32BitsToSingle(b) ? a : b;

    private static uint Maximum(uint a, uint b) => IsNan(a) ? b : IsNan(b) ? a
        : BitConverter.UInt32BitsToSingle(a) > BitConverter.UInt32BitsToSingle(b) ? a : b;

    private static uint Word(byte[] bytes, int row, int word) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(row * RowBytes + word * sizeof(uint)));

    // Every ordered triple of the value set, one 32-byte row each (a, b, c, then the results), padded to whole dispatches.
    private static byte[] Rows(uint[] values)
    {
        var count = values.Length * values.Length * values.Length;
        var rows = new byte[(count + (int)Lanes - 1) / (int)Lanes * (int)Lanes * RowBytes];
        var row = 0;
        foreach (var a in values)
        foreach (var b in values)
        foreach (var c in values)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * RowBytes), a);
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * RowBytes + 4), b);
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * RowBytes + 8), c);
            row++;
        }

        return rows;
    }

    private static byte[] Run(HeadlessVulkan vulkan, Gen5ShaderProgram program, byte[] rows, bool preserveNan)
    {
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = Lanes, ThreadCountX = Lanes, WaveSize = 32, ShaderSignedZeroInfNanPreserveFloat32Supported = preserveNan,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var buffer = runner.CreateBuffer(rows);
        var registers = new uint[256];
        registers[6] = (uint)rows.Length;
        for (uint first = 0; first < rows.Length / RowBytes; first += Lanes)
        {
            registers[8] = first;
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
        }

        var result = runner.ReadBack(buffer, 0, (ulong)rows.Length);
        harness.AssertNoValidationMessages();
        return result;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A device that rounds f16 exactly lets the shader convert with OpFConvert. The native path must give
// the bits of the integer emulation (the path for devices without the guarantee) and of IEEE
// round-to-nearest-even, over subnormals, ties, overflow, NaN and signed zero.
public sealed class Float16NativeConversionDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const int Lanes = 256;

    [Fact]
    public void HalfToFloatIsExactForEveryHalf()
    {
        if (!Ready(out var vulkan)) return;
        var input = Enumerable.Range(0, 0x10000).Select(value => (uint)value | 0xABCD_0000u).ToArray();
        var native = Run(vulkan, "VCvtF32F16", native: true, input);
        var emulated = Run(vulkan, "VCvtF32F16", native: false, input);
        for (var index = 0; index < input.Length; index++)
        {
            var half = BitConverter.UInt16BitsToHalf((ushort)input[index]);
            var expected = BitConverter.SingleToUInt32Bits((float)half);
            if (!Half.IsNaN(half))
            {
                Assert.True(expected == native[index], $"half {input[index] & 0xFFFF:X4}: native {native[index]:X8} expected {expected:X8}");
            }

            Assert.True(emulated[index] == native[index], $"half {input[index] & 0xFFFF:X4}: native {native[index]:X8} emulated {emulated[index]:X8}");
        }
    }

    [Fact]
    public void FloatToHalfRoundsToNearestEvenLikeTheEmulation()
    {
        if (!Ready(out var vulkan)) return;
        var input = FloatEdgeCases();
        var native = Run(vulkan, "VCvtF16F32", native: true, input);
        var emulated = Run(vulkan, "VCvtF16F32", native: false, input);
        for (var index = 0; index < input.Length; index++)
        {
            var value = BitConverter.UInt32BitsToSingle(input[index]);
            var expected = float.IsNaN(value)
                ? ((input[index] >> 16) & 0x8000u) | 0x7E00u
                : BitConverter.HalfToUInt16Bits((Half)value);
            Assert.True(expected == native[index], $"f32 {input[index]:X8}: native {native[index]:X4} expected {expected:X4}");
            // The packing of a NaN is implementation-defined without the guarantee; it must stay a NaN.
            if (float.IsNaN(value)) { Assert.True((emulated[index] & 0x7FFF) > 0x7C00); continue; }
            Assert.True(emulated[index] == native[index], $"f32 {input[index]:X8}: native {native[index]:X4} emulated {emulated[index]:X4}");
        }
    }

    [Theory]
    [InlineData("VPkMulF16")]
    [InlineData("VPkFmaF16")]
    public void PackedArithmeticMatchesTheEmulationAndIeee(string opcode)
    {
        if (!Ready(out var vulkan)) return;
        var random = new System.Random(0x516);
        const int count = 0x10000;
        uint[] Operands() => Enumerable.Range(0, count).Select(_ => RandomHalf(random) | (RandomHalf(random) << 16)).ToArray();
        var (a, b, c) = (Operands(), Operands(), Operands());
        var native = Run(vulkan, opcode, native: true, a, b, c);
        var emulated = Run(vulkan, opcode, native: false, a, b, c);
        for (var index = 0; index < count; index++)
        {
            Assert.True(emulated[index] == native[index],
                $"{opcode} {a[index]:X8} {b[index]:X8} {c[index]:X8}: native {native[index]:X8} emulated {emulated[index]:X8}");
            for (var lane = 0; lane < 2; lane++)
            {
                var shift = lane * 16;
                double x = (double)BitConverter.UInt16BitsToHalf((ushort)(a[index] >> shift));
                double y = (double)BitConverter.UInt16BitsToHalf((ushort)(b[index] >> shift));
                double z = (double)BitConverter.UInt16BitsToHalf((ushort)(c[index] >> shift));
                var reference = opcode == "VPkMulF16" ? x * y : Math.FusedMultiplyAdd(x, y, z);
                if (double.IsNaN(reference)) continue;
                var expected = BitConverter.HalfToUInt16Bits((Half)reference);
                var actual = (ushort)(native[index] >> shift);
                Assert.True(expected == actual,
                    $"{opcode} lane {lane} {a[index]:X8} {b[index]:X8} {c[index]:X8}: native {actual:X4} expected {expected:X4}");
            }
        }
    }

    private bool Ready(out HeadlessVulkan vulkan)
    {
        vulkan = fixture.Vulkan!;
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return false;
        // The device cannot run the native path, so there is nothing to compare.
        return vulkan.ExactFloat16Conversion;
    }

    private static uint RandomHalf(System.Random random)
    {
        var kind = random.Next(8);
        var sign = (uint)random.Next(2) << 15;
        return kind switch
        {
            0 => (uint)random.Next(0x10000),
            1 => sign | (uint)random.Next(0x400),                        // zero and subnormals
            2 => sign | 0x7C00u,                                         // infinity
            3 => sign | 0x7C00u | (uint)random.Next(1, 0x400),           // NaN
            _ => sign | ((uint)random.Next(6, 26) << 10) | (uint)random.Next(0x400), // moderate exponents
        };
    }

    private static uint[] FloatEdgeCases()
    {
        var values = new List<uint>();
        for (var half = 0u; half < 0x7C00; half++)
        {
            var low = (float)BitConverter.UInt16BitsToHalf((ushort)half);
            var next = half == 0x7BFF ? 65536f : (float)BitConverter.UInt16BitsToHalf((ushort)(half + 1));
            var tie = BitConverter.SingleToUInt32Bits((low + next) * 0.5f);
            foreach (var bits in new[] { BitConverter.SingleToUInt32Bits(low), tie - 1, tie, tie + 1 })
            {
                values.Add(bits);
                values.Add(bits | 0x8000_0000u);
            }
        }

        values.AddRange([
            0x0000_0000u, 0x8000_0000u, 0x7F80_0000u, 0xFF80_0000u, 0x7FC0_0000u, 0xFFC0_0000u, 0x7F80_0001u, 0xFF80_0001u,
            0x7FFF_FFFFu, 0xFFFF_FFFFu, 0x0000_0001u, 0x8000_0001u, 0x007F_FFFFu, 0x0080_0000u, 0x7F7F_FFFFu, 0x4780_0000u,
            0x477F_E000u, 0x477F_EFFFu, 0x477F_F000u, 0x477F_F001u, 0x3300_0000u, 0x3300_0001u, 0x3380_0000u, 0x3380_0001u,
        ]);
        var random = new System.Random(0x2F16);
        for (var index = 0; index < 100_000; index++) values.Add((uint)random.NextInt64(0, 1L << 32));
        while (values.Count % Lanes != 0) values.Add(0);
        return [.. values];
    }

    private static uint[] Run(HeadlessVulkan vulkan, string opcode, bool native, uint[] a, uint[]? b = null, uint[]? c = null)
    {
        var count = a.Length;
        var arrayBytes = (uint)(count * sizeof(uint));
        var packed = opcode is "VPkMulF16" or "VPkFmaF16";
        Gen5ShaderInstruction operation = opcode switch
        {
            "VPkMulF16" => Packed(0, 0x10, 8, 4, 5, 0),
            "VPkFmaF16" => Packed(0, 0x0E, 8, 4, 5, 6),
            _ => Vop1(0, opcode, 8, Gen5Operand.Vector(4)),
        };
        Gen5Operand Literal(uint value) => new(Gen5OperandKind.LiteralConstant, value);
        // Element index = workgroup id (s12) * Lanes + local id (v0); the three input arrays follow each other.
        var program = Program(
            Vop2(0, "VLshlrevB32", 3, Operand(8), Gen5Operand.Scalar(12)),
            Vop2(4, "VAddI32", 3, Gen5Operand.Vector(3), Gen5Operand.Vector(0)),
            Vop2(8, "VLshlrevB32", 2, Operand(2), Gen5Operand.Vector(3)),
            Vop2(12, "VAddI32", 7, Literal(arrayBytes), Gen5Operand.Vector(2)),
            Vop2(16, "VAddI32", 9, Literal(2 * arrayBytes), Gen5Operand.Vector(2)),
            BufferAccess(20, "BufferLoadDword", 4, 0, 1, 4, offsetEnabled: true, vectorAddress: 2),
            BufferAccess(24, "BufferLoadDword", 4, 0, 1, 5, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(28, "BufferLoadDword", 4, 0, 1, 6, offsetEnabled: true, vectorAddress: 9),
            operation with { Pc = 32 },
            BufferAccess(40, "BufferStoreDword", 8, 0, 1, 8, offsetEnabled: true, vectorAddress: 2),
            EndProgram(48));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            ExactFloat16ConversionSupported = native, LocalSizeX = Lanes, ThreadCountX = (uint)count, WaveSize = 32,
            ComputeSystemRegisters = new Gen5ComputeSystemRegisters(12, null, null, null),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var registers = new uint[256];
        registers[6] = 3 * arrayBytes;
        registers[10] = arrayBytes;
        var data = new byte[3 * arrayBytes];
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index * 4), a[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)arrayBytes + index * 4), packed ? b![index] : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)(2 * arrayBytes) + index * 4), packed ? c![index] : 0);
        }

        var buffer = runner.CreateBuffer(data);
        var output = runner.CreateBuffer(arrayBytes);
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer, output] },
            (uint)(count / Lanes), memoryOffsets: [0, 0]));
        var bytes = runner.ReadBack(output, 0, arrayBytes);
        harness.AssertNoValidationMessages();
        return Enumerable.Range(0, count).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4))).ToArray();
    }

    // VOP3P with op_sel_hi all set, so the high lane reads the high half of every source.
    private static Gen5ShaderInstruction Packed(uint pc, uint op, uint destination, uint source0, uint source1, uint source2) =>
        Float16ArithmeticTestsDecode(
        [
            (0x33u << 26) | (op << 16) | (1u << 14) | destination,
            (256 + source0) | ((256 + source1) << 9) | ((256 + source2) << 18) | (3u << 27),
            0xBF81_0000u,
        ]) with { Pc = pc };

    private static Gen5ShaderInstruction Float16ArithmeticTestsDecode(uint[] words) =>
        Gen5Float16ArithmeticTests.Decode(words).Instructions[0];
}

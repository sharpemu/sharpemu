// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// Packed f16 arithmetic with native f16<->f32 conversions (SupportsExactFloat16Conversions)
// against correctly rounded references, for every f16 value: two multiplies and a fused
// multiply-add, which widen each operand and round the result back to f16.
public sealed class Float16ConversionDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint LocalSize = 1024;

    // (multiplier, addend) pairs, one f16 per lane: 1, 1/3 (rounds), the smallest denormal,
    // the largest finite value, -0.75, and a NaN lane.
    public static IEnumerable<object[]> Operands() =>
    [
        [0x3C00_3C00u, 0x0000_8000u],
        [0x3555_3555u, 0x0001_8001u],
        [0x0001_8001u, 0x3C00_BC00u],
        [0x7BFF_FBFFu, 0x7BFF_0400u],
        [0xBA00_3A00u, 0x7E00_7C00u],
    ];

    [Theory]
    [MemberData(nameof(Operands))]
    public void PackedArithmeticIsCorrectlyRoundedForEveryHalf(uint multiplier, uint addend)
    {
        CheckPackedArithmetic(multiplier, addend, fmac: false);
        CheckPackedArithmetic(multiplier, addend, fmac: true);
    }

    private void CheckPackedArithmetic(uint multiplier, uint addend, bool fmac)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        if (!vulkan.ExactFloat16Conversions)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "The required gate needs exact native float16 support.");
            return;
        }
        var output = Run(vulkan, multiplier, addend, native: true, fmac);
        var failures = new List<string>();
        for (var value = 0; value < 0x10000; value++)
        {
            for (var result = 0; result < 3; result++)
            {
                uint expected = 0;
                for (var lane = 0; lane < 2; lane++)
                {
                    var x = (ushort)value;
                    var y = (ushort)(multiplier >> (16 * lane));
                    var z = (ushort)(addend >> (16 * lane));
                    var half = result switch
                    {
                        0 => Multiply(x, y),
                        1 => FusedMultiplyAdd(x, y, z),
                        _ => Multiply(x, x),
                    };
                    expected |= (uint)half << (16 * lane);
                }

                var actual = BitConverter.ToUInt32(output, value * 16 + result * 4);
                if (!SameHalves(expected, actual) && failures.Count < 8)
                {
                    failures.Add($"half=0x{value:X4} result={result} expected=0x{expected:X8} actual=0x{actual:X8}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Equal halves, where any two NaNs are equal: the sign of a NaN result is not defined.
    private static bool SameHalves(uint left, uint right)
    {
        for (var lane = 0; lane < 2; lane++)
        {
            var a = (ushort)(left >> (16 * lane));
            var b = (ushort)(right >> (16 * lane));
            var bothNan = (a & 0x7FFF) > 0x7C00 && (b & 0x7FFF) > 0x7C00;
            if (a != b && !bothNan) return false;
        }

        return true;
    }

    private static double Widen(ushort bits) => (double)BitConverter.UInt16BitsToHalf(bits);

    private static ushort Narrow(double value) => BitConverter.HalfToUInt16Bits((Half)value);

    // A product of two halves is exact in double, so one rounding gives the f16 result.
    private static ushort Multiply(ushort left, ushort right) => Narrow(Widen(left) * Widen(right));

    // x * y + z rounded once to f16: the double sum is corrected to round-to-odd (2Sum gives
    // the exact residual), which the round-to-nearest-even narrowing then rounds correctly.
    private static ushort FusedMultiplyAdd(ushort x, ushort y, ushort z)
    {
        var product = Widen(x) * Widen(y);
        var addend = Widen(z);
        var sum = product + addend;
        if (!double.IsFinite(sum)) return Narrow(sum);
        var productPart = sum - addend;
        var residual = (product - productPart) + (addend - (sum - productPart));
        var bits = BitConverter.DoubleToInt64Bits(sum);
        if (residual != 0 && (bits & 1) == 0)
        {
            bits += Math.Sign(residual) == Math.Sign(sum) ? 1 : -1;
            sum = BitConverter.Int64BitsToDouble(bits);
        }

        return Narrow(sum);
    }

    private static byte[] Run(HeadlessVulkan vulkan, uint multiplier, uint addend, bool native, bool fmac)
    {
        // v2 = s8 + v0 (the f16 value), v3 = that f16 in both lanes;
        // v4 = v3 * s9, v5 = fma(v3, s9, s10), v6 = v3 * v3; stored at v2 * 16.
        var fusedInstructions = fmac
            ? new[] { MoveVectorFromScalar(24, 5, 10), Vop2(28, "VPkFmacF16", 5, Gen5Operand.Vector(3), Gen5Operand.Scalar(9)) }
            : new[] { PackedF16(24, op: 0x0E, destination: 5, source0: 256 + 3, source1: 9, source2: 10) };
        var program = Program([
            MoveVectorFromScalar(0, 2, 8),
            Vop2(4, "VAddU32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(2)),
            Vop2(8, "VLshlrevB32", 3, Operand(16), Gen5Operand.Vector(2)),
            Vop2(12, "VOrB32", 3, Gen5Operand.Vector(3), Gen5Operand.Vector(2)),
            PackedF16(16, op: 0x10, destination: 4, source0: 256 + 3, source1: 9, source2: 0),
            .. fusedInstructions,
            PackedF16(32, op: 0x10, destination: 6, source0: 256 + 3, source1: 256 + 3, source2: 0),
            Vop2(40, "VLshlrevB32", 1, Operand(4), Gen5Operand.Vector(2)),
            BufferAccess(44, "BufferStoreDwordx3", 4, dwords: 3, vectorData: 4, offsetEnabled: true, vectorAddress: 1),
            EndProgram(52)]);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = LocalSize,
            SupportsExactFloat16Conversions = native,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        const uint bytes = 0x10000 * 16;
        var output = runner.CreateBuffer(bytes);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = Enumerable.Repeat(output, Math.Max(1, plan.Info.Buffers.Count)).ToArray(),
        };
        var registers = new uint[256];
        registers[6] = bytes;
        registers[9] = multiplier;
        registers[10] = addend;
        for (uint batch = 0; batch < 0x10000; batch += LocalSize)
        {
            registers[8] = batch;
            harness.Run(() => runner.Dispatch(registers, bindings, 1));
        }

        harness.AssertNoValidationMessages();
        return runner.ReadBack(output, 0, bytes);
    }

    // A VOP3P instruction with op_sel_hi set for every source (the high lane reads high halves).
    private static Gen5ShaderInstruction PackedF16(uint pc, uint op, uint destination, uint source0, uint source1, uint source2)
    {
        var decoded = Gen5Float16ArithmeticTests.Decode(
        [
            0xCC00_0000u | (op << 16) | (1u << 14) | destination,
            source0 | (source1 << 9) | (source2 << 18) | (3u << 27),
            0xBF81_0000u,
        ]);
        return decoded.Instructions[0] with { Pc = pc };
    }
}

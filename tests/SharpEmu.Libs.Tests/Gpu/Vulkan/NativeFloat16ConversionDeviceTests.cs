// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// With NativeFloat16Conversions the f16 conversions are OpFConvert under the 16-bit float controls, and the f16 add, subtract,
// multiply, maximum and median are f16 ops. Each kernel runs translated both ways and must give the same bits: every f32
// narrowed (all 2^32 patterns, as x * 1 + -0 through V_FMA_MIXLO_F16), every f16 widened (all 2^16, through V_FMA_MIX_F32 from
// either half, V_MAX_F16 and V_PK_MAX_F16), the f16 arithmetic over every ordered pair of halves (all 2^32) and, with its
// modifiers and constants, over every ordered triple of a value set. The narrowing and widening also match System.Half, and a
// NaN narrows to 0x7E00 with its sign. V_CVT_F16_F32 and V_CVT_F32_F16 (GLSL packing) keep their bits in a module that declares
// the float controls.
public sealed class NativeFloat16ConversionDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Lanes = 1024;
    private const uint WorkGroupRegister = 9;

    private static readonly ushort[] Halves =
    [
        0x0000, 0x8000, 0x0001, 0x8001, 0x0002, 0x03FF, 0x83FF, 0x0400, 0x8400, 0x0401,
        0x1400, 0x2E66, 0x3555, 0xB555, 0x3800, 0x3BFF, 0x3C00, 0xBC00, 0x3C01, 0x4000,
        0x4200, 0x4900, 0x5640, 0x5BFF, 0x6BFF, 0x77FF, 0x7800, 0x7BFE, 0x7BFF, 0xFBFF,
        0x7C00, 0xFC00, 0x7E00, 0xFE00, 0x7C01, 0xFC01, 0x7FFF, 0x7D55, 0x0200, 0x8200,
    ];

    public static IEnumerable<object[]> Parts() => Enumerable.Range(0, 16).Select(part => new object[] { (uint)part });

    // 2^28 patterns per part, in 64 dispatches of 4,096 groups. A result word is the narrowed half (low) and V_CVT_F16_F32 (high).
    [Theory]
    [MemberData(nameof(Parts))]
    public void EveryFloatNarrowsAsTheIntegerSequence(uint part)
    {
        if (!Ready()) return;
        const uint groups = 4096, count = groups * Lanes;
        using var pair = new KernelPair(fixture.Vulkan!, Kernel(1,
            Mix("VFmaMixloF16", 10, [Gen5Operand.Vector(1), Gen5Operand.Source(242), new(Gen5OperandKind.LiteralConstant, 0x8000_0000)],
                new Gen5Vop3pControl(0, 0, 0, 0, false)),
            MoveVector(0, 11, 0),
            Vop1(0, "VCvtF16F32", 11, Gen5Operand.Vector(1)),
            Vop2(0, "VLshlrevB32", 11, Operand(16), Gen5Operand.Vector(11)),
            Vop2(0, "VOrB32", 10, Gen5Operand.Vector(10), Gen5Operand.Vector(11))), new byte[count * 4], preserveNan: true);
        long positiveNans = 0, negativeNans = 0, packDiffers = 0;
        var wrong = new List<string>();
        long integerWrong = 0, nativeWrong = 0, unequal = 0;
        for (var dispatch = 0u; dispatch < 64; dispatch++)
        {
            var first = (part << 28) + dispatch * count;
            pair.Dispatch(first, groups);
            var (integer, native) = pair.Read();
            for (var index = 0; index < count; index++)
            {
                var bits = first + (uint)index;
                var word = BinaryPrimitives.ReadUInt32LittleEndian(integer.AsSpan(index * 4));
                var nativeWord = BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(index * 4));
                var value = BitConverter.UInt32BitsToSingle(bits);
                // A NaN reaches the narrowing as the NaN the device's FMA returns, so either sign is right.
                var nan = float.IsNaN(value);
                var expected = nan ? word & 0xFFFF : BitConverter.HalfToUInt16Bits((Half)value);
                var integerOk = nan ? expected is 0x7E00 or 0xFE00 : (word & 0xFFFF) == expected;
                var nativeOk = (nativeWord & 0xFFFF) == expected;
                if (integerOk && nativeOk && word == nativeWord)
                {
                    if (nan && expected == 0x7E00) positiveNans++;
                    else if (nan) negativeNans++;
                    else if (word >> 16 != expected) packDiffers++;
                    continue;
                }

                integerWrong += integerOk ? 0 : 1;
                nativeWrong += nativeOk ? 0 : 1;
                unequal += word == nativeWord ? 0 : 1;
                if (wrong.Count < 12)
                    wrong.Add($"f32 0x{bits:X8}: expected 0x{expected:X4}, integer 0x{word:X8}, native 0x{nativeWord:X8}");
            }
        }

        output.WriteLine($"part {part}: NaN results +{positiveNans} -{negativeNans}; " +
            $"V_CVT_F16_F32 differs from the narrowing for {packDiffers} other inputs");
        Assert.True(wrong.Count == 0, $"integer wrong {integerWrong}, native wrong {nativeWrong}, unequal {unequal}:" +
            Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    // All 2^16 patterns in one dispatch: V_FMA_MIX_F32 (h * 1 + -0) from the low half and from the high half, V_MAX_F16 (h, h),
    // V_PK_MAX_F16 with h in both halves, and V_CVT_F32_F16.
    [Fact]
    public void EveryHalfWidensAsTheIntegerSequence()
    {
        if (!Ready()) return;
        const uint count = 1u << 16, words = 8;
        using var pair = new KernelPair(fixture.Vulkan!, Kernel(words,
            Vop2(0, "VLshlrevB32", 4, Operand(16), Gen5Operand.Vector(1)),
            Vop2(0, "VOrB32", 5, Gen5Operand.Vector(1), Gen5Operand.Vector(4)),
            Mix("VFmaMixF32", 10, [Gen5Operand.Vector(1), Gen5Operand.Source(242), new(Gen5OperandKind.LiteralConstant, 0x8000_0000)],
                new Gen5Vop3pControl(0, 1, 0, 0, false)),
            Mix("VFmaMixF32", 11, [Gen5Operand.Vector(4), Gen5Operand.Source(242), new(Gen5OperandKind.LiteralConstant, 0x8000_0000)],
                new Gen5Vop3pControl(1, 1, 0, 0, false)),
            Vop2(0, "VMaxF16", 12, Gen5Operand.Vector(1), Gen5Operand.Vector(1)),
            Mix("VPkMaxF16", 13, [Gen5Operand.Vector(5), Gen5Operand.Vector(5)], new Gen5Vop3pControl(0, 3, 0, 0, false)),
            Vop1(0, "VCvtF32F16", 14, Gen5Operand.Vector(1))), new byte[count * words * 4], preserveNan: true);
        pair.Dispatch(0, count / Lanes);
        var (integer, native) = pair.Read();
        AssertSame(integer, native, word => $"f16 0x{word / words:X4} result {word % words}");
        var payloadsKept = 0;
        for (var bits = 0u; bits < count; bits++)
        {
            uint Word(int index) => BinaryPrimitives.ReadUInt32LittleEndian(integer.AsSpan((int)(bits * words + index) * 4));
            var half = BitConverter.UInt16BitsToHalf((ushort)bits);
            var widened = BitConverter.SingleToUInt32Bits((float)half);
            var narrowed = Half.IsNaN(half) ? (bits & 0x8000) | 0x7E00 : bits;
            if (Half.IsNaN(half))
            {
                Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(Word(0))), $"f16 0x{bits:X4} widened to 0x{Word(0):X8}");
                if (Word(0) == (((bits & 0x8000) << 16) | 0x7F80_0000 | ((bits & 0x3FF) << 13))) payloadsKept++;
            }
            else
            {
                Assert.True(Word(0) == widened && Word(1) == widened,
                    $"f16 0x{bits:X4} widened to 0x{Word(0):X8} and 0x{Word(1):X8}, not 0x{widened:X8}");
            }

            Assert.True(Word(2) == narrowed && Word(3) == (narrowed | (narrowed << 16)),
                $"f16 0x{bits:X4}: maximum 0x{Word(2):X8}, packed maximum 0x{Word(3):X8}");
        }

        output.WriteLine($"{payloadsKept} of 2048 NaN results of V_FMA_MIX_F32 keep the f16 payload");
    }

    // Every ordered triple (a, b, c) of the value set, one 64-byte row each: the inputs, then 13 results.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HalfArithmeticKeepsItsBits(bool preserveNan)
    {
        if (!Ready()) return;
        var program = Program(Numbered(
        [
            Vop2(0, "VAddU32", 1, Gen5Operand.Scalar(8), Gen5Operand.Vector(0)),
            Vop2(0, "VLshlrevB32", 7, Operand(6), Gen5Operand.Vector(1)),
            BufferAccess(0, "BufferLoadDwordx3", 4, dwords: 3, vectorData: 8, offsetEnabled: true, vectorAddress: 7),
            Vop2(0, "VLshlrevB32", 30, Operand(16), Gen5Operand.Vector(9)),
            Vop2(0, "VOrB32", 30, Gen5Operand.Vector(8), Gen5Operand.Vector(30)),
            Vop2(0, "VLshlrevB32", 31, Operand(16), Gen5Operand.Vector(8)),
            Vop2(0, "VOrB32", 31, Gen5Operand.Vector(10), Gen5Operand.Vector(31)),
            .. Enumerable.Range(11, 13).Select(register => MoveVector(0, (uint)register, 0)),
            Vop2(0, "VAddF16", 11, Gen5Operand.Vector(8), Gen5Operand.Vector(9)),
            Vop2(0, "VMulF16", 12, Gen5Operand.Vector(8), Gen5Operand.Vector(9)),
            Vop3(0, "VFmaF16", 13, Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            Vop1(0, "VRcpF16", 14, Gen5Operand.Vector(8)),
            Vop1(0, "VSqrtF16", 15, Gen5Operand.Vector(8)),
            Vop3(0, "VMax3F16", 16, Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            Vop3(0, "VMed3F16", 17, Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            Mix("VFmaMixloF16", 18, [Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)], new Gen5Vop3pControl(0, 7, 0, 0, false)),
            Mix("VFmaMixF32", 19, [Gen5Operand.Vector(8), Gen5Operand.Vector(9), Gen5Operand.Vector(10)], new Gen5Vop3pControl(0, 7, 0, 0, false)),
            Mix("VPkAddF16", 20, [Gen5Operand.Vector(30), Gen5Operand.Vector(31)], new Gen5Vop3pControl(0, 3, 0, 0, false)),
            Mix("VPkMulF16", 21, [Gen5Operand.Vector(30), Gen5Operand.Vector(31)], new Gen5Vop3pControl(0, 3, 0, 0, false)),
            Mix("VPkFmaF16", 22, [Gen5Operand.Vector(30), Gen5Operand.Vector(31), Gen5Operand.Vector(30)], new Gen5Vop3pControl(0, 7, 0, 0, false)),
            Vop2(0, "VMaxF16", 23, Gen5Operand.Vector(8), Gen5Operand.Vector(9)),
            BufferAccess(0, "BufferStoreDwordx4", 4, offset: 12, dwords: 4, vectorData: 11, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(0, "BufferStoreDwordx4", 4, offset: 28, dwords: 4, vectorData: 15, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(0, "BufferStoreDwordx4", 4, offset: 44, dwords: 4, vectorData: 19, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(0, "BufferStoreDword", 4, offset: 60, vectorData: 23, offsetEnabled: true, vectorAddress: 7),
        ]));
        const int rowBytes = 64;
        var count = Halves.Length * Halves.Length * Halves.Length;
        var rows = new byte[(count + (int)Lanes - 1) / (int)Lanes * (int)Lanes * rowBytes];
        var row = 0;
        foreach (var a in Halves)
        foreach (var b in Halves)
        foreach (var c in Halves)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * rowBytes), a);
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * rowBytes + 4), b);
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * rowBytes + 8), c);
            row++;
        }

        using var pair = new KernelPair(fixture.Vulkan!, program, rows, preserveNan);
        for (var first = 0u; first < rows.Length / rowBytes; first += Lanes)
        {
            pair.Dispatch(first, 1);
        }

        var (integer, native) = pair.Read();
        AssertSame(integer, native, word =>
        {
            var index = word / (rowBytes / 4);
            return index >= count ? $"padding row {index}"
                : $"({Halves[index / (Halves.Length * Halves.Length)]:X4}, {Halves[index / Halves.Length % Halves.Length]:X4}, " +
                    $"{Halves[index % Halves.Length]:X4}) result {word % (rowBytes / 4) - 3}";
        });
    }

    // Every ordered pair of halves, 2^28 per part in 64 dispatches of 4,096 groups: a in the low half of v1 and b in the high
    // half. The words are V_ADD_F16, V_SUB_F16, V_MUL_F16 and V_MAX_F16 of (a, b) (VOP3, b through op_sel), then V_PK_ADD_F16,
    // V_PK_MUL_F16, V_PK_MAX_F16 and V_PK_MIN_F16 of the pairs (a, b) and (b, a). The 16 parts take several minutes on a
    // desktop GPU, so they run only with SHARPEMU_TEST_EXHAUSTIVE_F16=1; HalfArithmeticModifiersKeepTheirBits always runs.
    [Theory]
    [MemberData(nameof(Parts))]
    public void EveryHalfPairComputesAsTheFloatSequence(uint part)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXHAUSTIVE_F16") != "1" || !Ready()) return;
        const uint groups = 4096, count = groups * Lanes, words = 8;
        string[] names = ["V_ADD_F16", "V_SUB_F16", "V_MUL_F16", "V_MAX_F16", "V_PK_ADD_F16", "V_PK_MUL_F16", "V_PK_MAX_F16", "V_PK_MIN_F16"];
        using var pair = new KernelPair(fixture.Vulkan!, Kernel(words,
        [
            Vop2(0, "VLshlrevB32", 5, Operand(16), Gen5Operand.Vector(1)),
            Vop2(0, "VLshrrevB32", 6, Operand(16), Gen5Operand.Vector(1)),
            Vop2(0, "VOrB32", 4, Gen5Operand.Vector(5), Gen5Operand.Vector(6)),
            .. new[] { "VAddF16", "VSubF16", "VMulF16", "VMaxF16" }.Select((opcode, index) =>
                Vop3With(opcode, 10 + (uint)index, Modifiers(select: 2), Gen5Operand.Vector(1), Gen5Operand.Vector(1))),
            .. new[] { "VPkAddF16", "VPkMulF16", "VPkMaxF16", "VPkMinF16" }.Select((opcode, index) =>
                Mix(opcode, 14 + (uint)index, [Gen5Operand.Vector(1), Gen5Operand.Vector(4)], new Gen5Vop3pControl(0, 3, 0, 0, false))),
        ]), new byte[count * words * 4], preserveNan: true);
        var unequal = new long[words];
        var differences = new List<string>();
        for (var dispatch = 0u; dispatch < 64; dispatch++)
        {
            var first = (part << 28) + dispatch * count;
            pair.Dispatch(first, groups);
            var (integer, native) = pair.Read();
            if (integer.AsSpan().SequenceEqual(native)) continue;
            for (var word = 0; word < integer.Length / 4; word++)
            {
                var expected = BinaryPrimitives.ReadUInt32LittleEndian(integer.AsSpan(word * 4));
                var actual = BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(word * 4));
                if (expected == actual) continue;
                var bits = first + (uint)(word / words);
                if (unequal[word % words]++ == 0 || differences.Count < 12)
                    differences.Add($"{names[word % words]} (0x{bits & 0xFFFF:X4}, 0x{bits >> 16:X4}): f32 forms 0x{expected:X8}, f16 0x{actual:X8}");
            }
        }

        Assert.True(differences.Count == 0, $"unequal results {string.Join(", ", unequal.Select((n, i) => $"{names[i]} {n}"))}:" +
            Environment.NewLine + string.Join(Environment.NewLine, differences));
    }

    // Every ordered triple (a, b, c) of the value set, one 256-byte row each: the inputs, then the f16 ops with abs, neg, clamp,
    // op_sel and output modifiers and inline and literal constants, and the packed ops with op_sel, op_sel_hi, neg_lo, neg_hi,
    // clamp and constants. Among them are the constants and modifiers that keep the f32 forms (1/(2 pi), NaN, omod). Without
    // SignedZeroInfNanPreserve for 32 bits the f32 forms may fold x + -x, x - x, x * -0, x + 0 and an operand's negated copy
    // (fast math), so those results depend on the code around them and are compared only with it. A maximum, minimum or
    // median with a negated operand keeps the f32 forms, whose NaN result after a float negation has the sign the driver
    // leaves it (Vulkan keeps no NaN bits: a driver may keep it for -b, -c and drop it for x, -x); a NaN lane of those
    // is compared without its sign.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HalfArithmeticModifiersKeepTheirBits(bool preserveNan)
    {
        if (!Ready()) return;
        var a = Gen5Operand.Vector(8);
        var b = Gen5Operand.Vector(9);
        var c = Gen5Operand.Vector(10);
        var ab = Gen5Operand.Vector(30);
        var ca = Gen5Operand.Vector(31);
        var bc = Gen5Operand.Vector(32);
        Gen5Operand Literal(uint value) => new(Gen5OperandKind.LiteralConstant, value);
        Gen5Vop3pControl Packed(uint select = 0, uint selectHigh = 3, uint negateLow = 0, uint negateHigh = 0, bool clamp = false) =>
            new(select, selectHigh, negateLow, negateHigh, clamp);
        (string Name, Func<uint, Gen5ShaderInstruction> Emit)[] cases =
        [
            ("add a b", d => Vop2(0, "VAddF16", d, a, b)),
            ("subrev a b", d => Vop2(0, "VSubrevF16", d, a, b)),
            ("add -a |b|", d => Vop3With("VAddF16", d, Modifiers(absolute: 2, negate: 1), a, b)),
            ("sub |a| -b clamp", d => Vop3With("VSubF16", d, Modifiers(absolute: 1, negate: 2, clamp: true), a, b)),
            ("mul -a -b clamp", d => Vop3With("VMulF16", d, Modifiers(negate: 3, clamp: true), a, b)),
            ("mul b a to the high half", d => Vop3With("VMulF16", d, Modifiers(select: 11), ab, ca)),
            ("max -a |b|", d => Vop3With("VMaxF16", d, Modifiers(absolute: 2, negate: 1), a, b)),
            ("max -a -b clamp", d => Vop3With("VMaxF16", d, Modifiers(negate: 3, clamp: true), a, b)),
            ("max3 -a b |c|", d => Vop3With("VMax3F16", d, Modifiers(absolute: 4, negate: 1), a, b, c)),
            ("med3 |a| -b c clamp", d => Vop3With("VMed3F16", d, Modifiers(absolute: 1, negate: 2, clamp: true), a, b, c)),
            ("med3 b a -c", d => Vop3With("VMed3F16", d, Modifiers(negate: 4, select: 3), ab, ca, c)),
            ("add a 1.0", d => Vop3With("VAddF16", d, Modifiers(), a, Gen5Operand.Source(242))),
            ("mul a 1/(2 pi)", d => Vop3With("VMulF16", d, Modifiers(), a, Gen5Operand.Source(248))),
            ("mul -a -0.5", d => Vop3With("VMulF16", d, Modifiers(negate: 1), a, Gen5Operand.Source(241))),
            ("add 5 b", d => Vop3With("VAddF16", d, Modifiers(), Gen5Operand.Source(133), b)),
            ("sub a 0x3555", d => Vop3With("VSubF16", d, Modifiers(), a, Literal(0x3555))),
            ("max a NaN", d => Vop3With("VMaxF16", d, Modifiers(), a, Literal(0xFD55))),
            ("mul a -0", d => Vop3With("VMulF16", d, Modifiers(), a, Literal(0x8000))),
            ("add a -0", d => Vop3With("VAddF16", d, Modifiers(), a, Literal(0x8000))),
            ("mul a 1.0", d => Vop3With("VMulF16", d, Modifiers(), a, Gen5Operand.Source(242))),
            ("sub a b omod 2", d => Vop3With("VSubF16", d, Modifiers(outputModifier: 1), a, b)),
            ("max a b", d => Vop2(0, "VMaxF16", d, a, b)),
            ("mul c b", d => Vop2(0, "VMulF16", d, c, b)),
            ("max3 a b c clamp to the high half", d => Vop3With("VMax3F16", d, Modifiers(clamp: true, select: 8), a, b, c)),
            ("pk_add ab ca", d => Mix("VPkAddF16", d, [ab, ca], Packed())),
            ("pk_add bb cc", d => Mix("VPkAddF16", d, [ab, ca], Packed(select: 1, selectHigh: 1))),
            ("pk_mul -ab c-a", d => Mix("VPkMulF16", d, [ab, ca], Packed(negateLow: 1, negateHigh: 2))),
            ("pk_mul aa bb clamp", d => Mix("VPkMulF16", d, [ab, bc], Packed(selectHigh: 0, clamp: true))),
            ("pk_max -a-b -ca", d => Mix("VPkMaxF16", d, [ab, ca], Packed(negateLow: 3, negateHigh: 1))),
            ("pk_min ab cb", d => Mix("VPkMinF16", d, [ab, bc], Packed(select: 2, selectHigh: 1))),
            ("pk_max ca ab clamp", d => Mix("VPkMaxF16", d, [ca, ab], Packed(clamp: true))),
            ("pk_add ab 1.0", d => Mix("VPkAddF16", d, [ab, Gen5Operand.Source(242)], Packed())),
            ("pk_mul ab (0.5, -1)", d => Mix("VPkMulF16", d, [ab, Literal(0xBC00_3800)], Packed())),
            ("pk_min ab -1 (NaN halves)", d => Mix("VPkMinF16", d, [ab, Gen5Operand.Source(193)], Packed())),
            ("pk_add ab -ab", d => Mix("VPkAddF16", d, [ab, ab], Packed(negateLow: 2, negateHigh: 2))),
            ("pk_mul ac cb", d => Mix("VPkMulF16", d, [ca, bc], Packed(select: 3, selectHigh: 0))),
            ("pk_max ab -b-c", d => Mix("VPkMaxF16", d, [ab, bc], Packed(negateHigh: 3))),
            ("pk_min -ab ca clamp", d => Mix("VPkMinF16", d, [ab, ca], Packed(negateLow: 1, clamp: true))),
            ("add a -a", d => Vop3With("VAddF16", d, Modifiers(negate: 2), a, a)),
            ("sub a a", d => Vop2(0, "VSubF16", d, a, a)),
            ("subrev -a -a", d => Vop3With("VSubrevF16", d, Modifiers(negate: 3), a, a)),
            ("max a -a", d => Vop3With("VMaxF16", d, Modifiers(negate: 2), a, a)),
            ("pk_add -ab ab", d => Mix("VPkAddF16", d, [ab, ab], Packed(negateLow: 1, negateHigh: 1))),
            ("pk_mul ab -ab", d => Mix("VPkMulF16", d, [ab, ab], Packed(negateLow: 2, negateHigh: 2))),
            ("pk_add ab ab", d => Mix("VPkAddF16", d, [ab, ab], Packed())),
            ("pk_max ab -ab", d => Mix("VPkMaxF16", d, [ab, ab], Packed(negateLow: 2, negateHigh: 2))),
        ];
        string[] fastMath = ["mul a -0", "add a -0", "pk_add ab 1.0", "add a -a", "sub a a", "subrev -a -a", "max a -a",
            "pk_add -ab ab", "pk_mul ab -ab", "pk_add ab ab", "pk_add ab -ab", "pk_max ab -ab"];
        string[] negatedSelection = ["max -a |b|", "max3 -a b |c|", "med3 b a -c", "pk_max -a-b -ca", "pk_max ab -b-c",
            "max a -a", "pk_max ab -ab"];
        Assert.All(fastMath.Concat(negatedSelection), name => Assert.Contains(cases, test => test.Name == name));
        const uint first = 40;
        var results = (uint)cases.Length;
        var program = Program(Numbered(
        [
            Vop2(0, "VAddU32", 1, Gen5Operand.Scalar(8), Gen5Operand.Vector(0)),
            Vop2(0, "VLshlrevB32", 7, Operand(8), Gen5Operand.Vector(1)),
            BufferAccess(0, "BufferLoadDwordx3", 4, dwords: 3, vectorData: 8, offsetEnabled: true, vectorAddress: 7),
            Vop2(0, "VLshlrevB32", 30, Operand(16), b),
            Vop2(0, "VOrB32", 30, a, ab),
            Vop2(0, "VLshlrevB32", 31, Operand(16), a),
            Vop2(0, "VOrB32", 31, c, ca),
            Vop2(0, "VLshlrevB32", 32, Operand(16), c),
            Vop2(0, "VOrB32", 32, b, bc),
            .. Enumerable.Range((int)first, (int)(results + 3) / 4 * 4).Select(register => MoveVector(0, (uint)register, 0)),
            .. cases.Select((test, index) => test.Emit(first + (uint)index)),
            .. Enumerable.Range(0, (int)(results + 3) / 4).Select(store => BufferAccess(0, "BufferStoreDwordx4", 4,
                offset: 16 + store * 16, dwords: 4, vectorData: first + (uint)store * 4, offsetEnabled: true, vectorAddress: 7)),
        ]));
        const int rowBytes = 256;
        var count = Halves.Length * Halves.Length * Halves.Length;
        var rows = new byte[(count + (int)Lanes - 1) / (int)Lanes * (int)Lanes * rowBytes];
        var row = 0;
        foreach (var first16 in Halves)
        foreach (var second in Halves)
        foreach (var third in Halves)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * rowBytes), first16);
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * rowBytes + 4), second);
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(row * rowBytes + 8), third);
            row++;
        }

        using var pair = new KernelPair(fixture.Vulkan!, program, rows, preserveNan);
        for (var start = 0u; start < rows.Length / rowBytes; start += Lanes)
        {
            pair.Dispatch(start, 1);
        }

        var (integer, native) = pair.Read();
        var unequal = new int[cases.Length];
        var differences = new List<string>();
        for (var index = 0; index < count; index++)
        {
            for (var result = 0; result < cases.Length; result++)
            {
                if (!preserveNan && fastMath.Contains(cases[result].Name)) continue;
                var offset = index * rowBytes + 16 + result * 4;
                var expected = BinaryPrimitives.ReadUInt32LittleEndian(integer.AsSpan(offset));
                var actual = BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(offset));
                if (negatedSelection.Contains(cases[result].Name) && NanSignless(expected) == NanSignless(actual)) continue;
                if (expected == actual || unequal[result]++ >= 3) continue;
                differences.Add($"{cases[result].Name} ({Halves[index / (Halves.Length * Halves.Length)]:X4}, " +
                    $"{Halves[index / Halves.Length % Halves.Length]:X4}, {Halves[index % Halves.Length]:X4}): " +
                    $"f32 forms 0x{expected:X8}, f16 0x{actual:X8}");
            }
        }

        Assert.True(differences.Count == 0, string.Join(", ", unequal.Select((n, i) => (n, i)).Where(c => c.n > 0)
            .Select(c => $"{cases[c.i].Name}: {c.n}")) + Environment.NewLine + string.Join(Environment.NewLine, differences));
    }

    // A word with the sign bit of each NaN half cleared.
    private static uint NanSignless(uint word)
    {
        foreach (var shift in new[] { 0, 16 })
        {
            if (((word >> shift) & 0x7FFF) > 0x7C00) word &= ~(0x8000u << shift);
        }

        return word;
    }

    private static Gen5Vop3Control Modifiers(
        uint absolute = 0, uint negate = 0, uint outputModifier = 0, bool clamp = false, uint select = 0) =>
        new(absolute, negate, outputModifier, clamp, select, null);

    private static Gen5ShaderInstruction Vop3With(string opcode, uint destination, Gen5Vop3Control control, params Gen5Operand[] sources) =>
        Vop3(0, opcode, destination, sources) with { Control = control };

    private bool Ready()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return false;
        if (vulkan.NativeFloat16Conversions) return true;
        Assert.False(GatePrerequisites.DeviceRequired, "The device lacks shaderFloat16 or the 16-bit float controls.");
        return false;
    }

    private static Gen5ShaderInstruction Mix(string opcode, uint destination, Gen5Operand[] sources, Gen5Vop3pControl control) =>
        new(0, Gen5ShaderEncoding.Vop3p, opcode, [0u, 0u], sources, [Gen5Operand.Vector(destination)], control);

    // The instructions with consecutive addresses, then s_endpgm.
    private static Gen5ShaderInstruction[] Numbered(params Gen5ShaderInstruction[] body) =>
        [.. body.Append(EndProgram(0)).Select((instruction, index) => instruction with { Pc = (uint)index * 8 })];

    // v1 = s8 + the invocation's index in the dispatch (its work group in s9); the results v10.., cleared first, are stored as
    // `words` (1, 2, 4 or 8) consecutive dwords per invocation.
    private static Gen5ShaderProgram Kernel(uint words, params Gen5ShaderInstruction[] body)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            Sop2(0, "SLshlB32", 10, Gen5Operand.Scalar(WorkGroupRegister), Operand(10)),
            Vop2(0, "VAddU32", 2, Gen5Operand.Scalar(10), Gen5Operand.Vector(0)),
            Vop2(0, "VAddU32", 1, Gen5Operand.Scalar(8), Gen5Operand.Vector(2)),
            Vop2(0, "VLshlrevB32", 3, Operand(2 + (uint)BitOperations.Log2(words)), Gen5Operand.Vector(2)),
        };
        instructions.AddRange(Enumerable.Range(10, (int)words).Select(register => MoveVector(0, (uint)register, 0)));
        instructions.AddRange(body);
        for (var word = 0u; word < words; word += 4)
        {
            var dwords = Math.Min(4, words - word);
            instructions.Add(BufferAccess(0, dwords == 1 ? "BufferStoreDword" : $"BufferStoreDwordx{dwords}", 4, offset: (int)word * 4,
                dwords: dwords, vectorData: 10 + word, offsetEnabled: true, vectorAddress: 3));
        }

        return Program(Numbered([.. instructions]));
    }

    private static void AssertSame(byte[] integer, byte[] native, Func<int, string> describe)
    {
        if (integer.AsSpan().SequenceEqual(native)) return;
        var differences = new List<string>();
        var count = 0;
        for (var word = 0; word < integer.Length / 4; word++)
        {
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(integer.AsSpan(word * 4));
            var actual = BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(word * 4));
            if (expected == actual) continue;
            if (count++ < 8) differences.Add($"{describe(word)}: integer conversions 0x{expected:X8}, native 0x{actual:X8}");
        }

        Assert.Fail($"{count} words differ, first:{Environment.NewLine}{string.Join(Environment.NewLine, differences)}");
    }

    // One kernel translated with the integer conversions and with the native ones, each with a copy of the buffer.
    private sealed class KernelPair : IDisposable
    {
        private readonly ImageTestHarness _harness;
        private readonly (LayoutComputeRunner Runner, GpuBuffer Buffer)[] _kernels;
        private readonly uint _size;

        public KernelPair(HeadlessVulkan vulkan, Gen5ShaderProgram program, byte[] buffer, bool preserveNan)
        {
            _harness = new ImageTestHarness(vulkan);
            _size = (uint)buffer.Length;
            _kernels = [.. new[] { false, true }.Select(native =>
            {
                var (plan, resources, layout) = Prepare(program, userDataCount: WorkGroupRegister);
                var request = new ShaderCompileRequest(plan, resources, layout)
                {
                    LocalSizeX = Lanes, WaveSize = 32, ShaderSignedZeroInfNanPreserveFloat32Supported = preserveNan,
                    ComputeSystemRegisters = new Gen5ComputeSystemRegisters(WorkGroupRegister, null, null, null),
                    NativeFloat16Conversions = native,
                };
                Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
                Assert.Equal(native, DeclaresFloat16(shader.Spirv));
                var runner = new LayoutComputeRunner(_harness, request, shader.Spirv);
                return (runner, runner.CreateBuffer(buffer));
            })];
        }

        public void Dispatch(uint first, uint groups)
        {
            var registers = new uint[256];
            registers[6] = _size;
            registers[8] = first;
            foreach (var (runner, buffer) in _kernels)
            {
                _harness.Run(() => runner.Dispatch(registers,
                    new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, groups));
            }
        }

        public (byte[] Integer, byte[] Native) Read()
        {
            var results = _kernels.Select(kernel => kernel.Runner.ReadBack(kernel.Buffer, 0, _size)).ToArray();
            _harness.AssertNoValidationMessages();
            return (results[0], results[1]);
        }

        public void Dispose()
        {
            foreach (var (runner, _) in _kernels) runner.Dispose();
            _harness.Dispose();
        }

        private static bool DeclaresFloat16(byte[] spirv)
        {
            for (var offset = 20; offset < spirv.Length;)
            {
                var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
                if ((header & 0xFFFF) == (uint)SpirvOp.Capability &&
                    BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + 4)) == (uint)SpirvCapability.Float16) return true;
                offset += (int)(header >> 16) * 4;
            }

            return false;
        }
    }
}

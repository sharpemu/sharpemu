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

// With NativeFloat16Conversions the f16 conversions are OpFConvert under the 16-bit float controls. Each kernel runs translated
// both ways and must give the same bits: every f32 narrowed (all 2^32 patterns, as x * 1 + -0 through V_FMA_MIXLO_F16), every
// f16 widened (all 2^16, through V_FMA_MIX_F32 from either half, V_MAX_F16 and V_PK_MAX_F16), and the f16 arithmetic over every
// ordered triple of a value set. The narrowing and widening also match System.Half, and a NaN narrows to 0x7E00 with its sign.
// V_CVT_F16_F32 and V_CVT_F32_F16 (GLSL packing) keep their bits in a module that declares the float controls.
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
                Assert.Equal(native, CountOpcode(shader.Spirv, SpirvOp.FConvert) > 0);
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

        private static int CountOpcode(byte[] spirv, SpirvOp opcode)
        {
            var count = 0;
            for (var offset = 20; offset < spirv.Length;)
            {
                var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
                offset += (int)(header >> 16) * 4;
            }

            return count;
        }
    }
}

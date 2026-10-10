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

public sealed class FloatSemanticsDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint PackBase = 0xAABB_CCDD;
    private const uint ThreadCount = 32;
    private const uint RowBytes = 32;

    [Fact]
    public void ConversionsAndNanOperandsMatchTheHardwareRules()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Vop1(0, "VCvtF32U32", 1, Gen5Operand.Vector(0)),
            MoveScalar(4, 8, Bits(0.25f)),
            MoveScalar(12, 9, Bits(-2f)),
            MoveScalar(20, 10, Bits(20f)),
            MoveScalar(28, 11, Bits(-60f)),
            MoveScalar(36, 12, Bits(float.NaN)),
            MoveScalar(44, 13, PackBase),
            MoveScalar(52, 14, Bits(5f)),
            MoveScalar(60, 15, Bits(10f)),
            Vop3(68, "VMadF32", 2, Gen5Operand.Vector(1), Gen5Operand.Scalar(8), Gen5Operand.Scalar(9)),
            Vop3(76, "VMadF32", 3, Gen5Operand.Vector(1), Gen5Operand.Scalar(10), Gen5Operand.Scalar(11)),
            Vop1(84, "VCvtRpiI32F32", 12, Gen5Operand.Vector(2)),
            Vop3(88, "VCvtPkU8F32", 13, Gen5Operand.Vector(3), Operand(1), Gen5Operand.Scalar(13)),
            Vop2(96, "VMaxF32", 14, Gen5Operand.Scalar(12), Gen5Operand.Vector(1)),
            Vop2(100, "VMinF32", 15, Gen5Operand.Vector(1), Gen5Operand.Scalar(12)),
            Vop3(104, "VMed3F32", 16, Gen5Operand.Scalar(12), Gen5Operand.Vector(1), Gen5Operand.Scalar(14)),
            Vop3(112, "VMed3F32", 17, Gen5Operand.Vector(1), Gen5Operand.Scalar(14), Gen5Operand.Scalar(15)),
            Vop2(120, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(124, "BufferStoreDwordx4", 4, vectorData: 12, dwords: 4, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(132, "BufferStoreDword", 4, offset: 16, vectorData: 16, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(140, "BufferStoreDword", 4, offset: 20, vectorData: 17, offsetEnabled: true, vectorAddress: 7),
            EndProgram(148));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = ThreadCount, ThreadCountX = ThreadCount, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ThreadCount * RowBytes);
        var registers = new uint[256];
        registers[6] = ThreadCount * RowBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ThreadCount * RowBytes);
        for (var lane = 0u; lane < ThreadCount; lane++)
        {
            var row = actual.AsSpan((int)(lane * RowBytes));
            var rounded = (int)MathF.Floor(lane * 0.25f - 2f + 0.5f);
            var packed = (uint)Math.Clamp(lane * 20f - 60f, 0f, 255f);
            Assert.Equal(rounded, BinaryPrimitives.ReadInt32LittleEndian(row));
            Assert.Equal((PackBase & ~0xFF00u) | (packed << 8), BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
            Assert.Equal(Bits(lane), BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            Assert.Equal(Bits(lane), BinaryPrimitives.ReadUInt32LittleEndian(row[12..]));
            Assert.Equal(Bits(MathF.Min(lane, 5f)), BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
            Assert.Equal(Bits(Math.Clamp(lane, 5f, 10f)), BinaryPrimitives.ReadUInt32LittleEndian(row[20..]));
        }
    }

    [Fact]
    public void ConditionalMaskAppliesSourceSignModifiers()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var negateFirst = new Gen5Vop3Control(0, 1, 0, false, 0, null);
        var absoluteBothNegateSecond = new Gen5Vop3Control(3, 2, 0, false, 0, null);
        var program = Program(
            Vop1(0, "VCvtF32U32", 1, Gen5Operand.Vector(0)),
            MoveScalar(4, 8, Bits(1f)),
            MoveScalar(12, 9, Bits(-8f)),
            MoveScalar(20, 20, uint.MaxValue),
            MoveScalar(28, 21, uint.MaxValue),
            MoveScalar(36, 22, 0),
            MoveScalar(44, 23, 0),
            Vop3(52, "VMadF32", 2, Gen5Operand.Vector(1), Gen5Operand.Scalar(8), Gen5Operand.Scalar(9)),
            Vop3(60, "VCndmaskB32", 12, Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Scalar(22)) with { Control = negateFirst },
            Vop3(68, "VCndmaskB32", 13, Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Scalar(20)) with { Control = negateFirst },
            Vop3(76, "VCndmaskB32", 14, Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Scalar(20)) with { Control = absoluteBothNegateSecond },
            Vop3(84, "VCndmaskB32", 15, Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Scalar(22)) with { Control = absoluteBothNegateSecond },
            Vop2(92, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(96, "BufferStoreDwordx4", 4, vectorData: 12, dwords: 4, offsetEnabled: true, vectorAddress: 7),
            EndProgram(104));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = ThreadCount, ThreadCountX = ThreadCount, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ThreadCount * RowBytes);
        var registers = new uint[256];
        registers[6] = ThreadCount * RowBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ThreadCount * RowBytes);
        for (var lane = 0u; lane < ThreadCount; lane++)
        {
            var row = actual.AsSpan((int)(lane * RowBytes));
            var value = Bits(lane - 8f);
            Assert.Equal(value ^ 0x8000_0000, BinaryPrimitives.ReadUInt32LittleEndian(row));
            Assert.Equal(value, BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
            Assert.Equal(value | 0x8000_0000, BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            Assert.Equal(value & 0x7FFF_FFFF, BinaryPrimitives.ReadUInt32LittleEndian(row[12..]));
        }
    }

    // op_sel_hi marks a V_FMA_MIX operand as f16, literals included: 0x34CD is 0.3, not an
    // f32 denormal. Silent Hill weights a luminance with such literals and divides by it.
    [Fact]
    public void FusedMixReadsHalfSelectedLiteralsAsHalfFloats()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var halfLiteral = new Gen5Vop3pControl(0, 0b001, 0, 0, false);
        var floatLiteral = new Gen5Vop3pControl(0, 0b000, 0, 0, false);
        var program = Program(
            Vop1(0, "VCvtF32U32", 1, Gen5Operand.Vector(0)),
            Vop3(4, "VFmaMixF32", 12, new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x34CD), Gen5Operand.Vector(1), Operand(0)) with { Control = halfLiteral },
            Vop3(16, "VFmaMixF32", 13, new Gen5Operand(Gen5OperandKind.LiteralConstant, Bits(0.25f)), Gen5Operand.Vector(1), Operand(0)) with { Control = floatLiteral },
            Vop2(28, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(32, "BufferStoreDword", 4, vectorData: 12, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(40, "BufferStoreDword", 4, offset: 4, vectorData: 13, offsetEnabled: true, vectorAddress: 7),
            EndProgram(48));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = ThreadCount, ThreadCountX = ThreadCount, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ThreadCount * RowBytes);
        var registers = new uint[256];
        registers[6] = ThreadCount * RowBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ThreadCount * RowBytes);
        var weight = (float)BitConverter.UInt16BitsToHalf(0x34CD);
        for (var lane = 0u; lane < ThreadCount; lane++)
        {
            var row = actual.AsSpan((int)(lane * RowBytes));
            Assert.Equal(Bits(MathF.FusedMultiplyAdd(weight, lane, 0f)), BinaryPrimitives.ReadUInt32LittleEndian(row));
            Assert.Equal(Bits(lane * 0.25f), BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
        }
    }

    // V_CVT_PKRTZ_F16_F32 rounds toward zero: finite values past the f16 range saturate at
    // +-65504 instead of becoming infinity, and subnormal results keep whole 2^-24 steps.
    [Fact]
    public void PackToHalfRoundsTowardZeroWithoutOverflowingToInfinity()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Vop1(0, "VCvtF32U32", 1, Gen5Operand.Vector(0)),
            MoveScalar(4, 8, Bits(5003.7f)),
            MoveScalar(12, 9, Bits(-80000.3f)),
            MoveScalar(20, 10, Bits(3.3e-6f)),
            MoveScalar(28, 11, Bits(-5e-5f)),
            MoveScalar(36, 12, Bits(float.NaN)),
            MoveScalar(44, 13, Bits(float.PositiveInfinity)),
            Vop3(52, "VMadF32", 2, Gen5Operand.Vector(1), Gen5Operand.Scalar(8), Gen5Operand.Scalar(9)),
            Vop3(60, "VMadF32", 3, Gen5Operand.Vector(1), Gen5Operand.Scalar(10), Gen5Operand.Scalar(11)),
            Vop2(68, "VCvtPkrtzF16F32", 12, Gen5Operand.Vector(2), Gen5Operand.Vector(3)),
            Vop3(72, "VCvtPkrtzF16F32", 13, Gen5Operand.Scalar(12), Gen5Operand.Scalar(13)),
            Vop2(80, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(84, "BufferStoreDword", 4, vectorData: 12, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(92, "BufferStoreDword", 4, offset: 4, vectorData: 13, offsetEnabled: true, vectorAddress: 7),
            EndProgram(100));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = ThreadCount, ThreadCountX = ThreadCount, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ThreadCount * RowBytes);
        var registers = new uint[256];
        registers[6] = ThreadCount * RowBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ThreadCount * RowBytes);
        for (var lane = 0u; lane < ThreadCount; lane++)
        {
            var row = actual.AsSpan((int)(lane * RowBytes));
            var large = MathF.FusedMultiplyAdd(lane, 5003.7f, -80000.3f);
            var small = MathF.FusedMultiplyAdd(lane, 3.3e-6f, -5e-5f);
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(row);
            Assert.Equal(HalfTowardZero(large), packed & 0xFFFF);
            Assert.Equal(HalfTowardZero(small), packed >> 16);
            var special = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            Assert.True((special & 0x7C00) == 0x7C00 && (special & 0x3FF) != 0, $"lane {lane}: 0x{special:X8}");
            Assert.Equal(0x7C00u, special >> 16);
        }
    }

    private static uint HalfTowardZero(float value)
    {
        var sign = value < 0 || (value == 0 && float.IsNegative(value)) ? 0x8000u : 0u;
        var magnitude = MathF.Abs(value);
        if (magnitude >= 65504f) return sign | 0x7BFF;
        if (magnitude < 6.10351562e-05f) return sign | (uint)Math.Floor(magnitude * 16777216.0);
        var bits = BitConverter.SingleToUInt32Bits(magnitude);
        return sign | ((((bits >> 23) - 127 + 15) << 10) | ((bits >> 13) & 0x3FF));
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}

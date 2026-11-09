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

    // V_PK_ADD_I16 and V_PK_SUB_I16 read a negated source the same way: the literal 0x7FFF with the
    // negate bit is -1 in both lanes, so the sum is lane - 1 and the difference lane + 1.
    [Fact]
    public void PackedAddAndSubtractFlipTheSignBitOfNegatedSources()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var negatedLiteral = new Gen5Vop3pControl(0, 0b001, 0b010, 0b010, false);
        var program = Program(
            Vop2(0, "VLshlrevB32", 3, Operand(16), Gen5Operand.Vector(0)),
            Vop2(4, "VOrB32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(3)),
            Vop3(8, "VPkAddI16", 12, Gen5Operand.Vector(2), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x7FFF), Operand(0))
                with { Control = negatedLiteral },
            Vop3(20, "VPkSubI16", 13, Gen5Operand.Vector(2), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x7FFF), Operand(0))
                with { Control = negatedLiteral },
            Vop2(32, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(36, "BufferStoreDword", 4, vectorData: 12, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(44, "BufferStoreDword", 4, offset: 4, vectorData: 13, offsetEnabled: true, vectorAddress: 7),
            EndProgram(52));
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
        for (var lane = 0; lane < ThreadCount; lane++)
        {
            var row = actual.AsSpan((int)(lane * RowBytes));
            var sum = BinaryPrimitives.ReadUInt32LittleEndian(row);
            var difference = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            Assert.Equal((ushort)(lane - 1), sum & 0xFFFF);
            Assert.Equal((ushort)(lane - 1), sum >> 16);
            Assert.Equal((ushort)(lane + 1), difference & 0xFFFF);
            Assert.Equal((ushort)(lane + 1), difference >> 16);
        }
    }

    // The negate bits of a packed op flip bit 15 of the selected half whatever the opcode: a
    // compiler writes the integer -1 as the literal 0x7FFF with the bit set. A temporal
    // upscaler clamps its left and upper taps with v_pk_max_i16(-1, edge - position) that way.
    [Fact]
    public void PackedIntegerOpsFlipTheSignBitOfNegatedSources()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Vop2(0, "VLshlrevB32", 3, Operand(16), Gen5Operand.Vector(0)),
            Vop2(4, "VOrB32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(3)),
            Vop3(8, "VPkSubI16", 1, Gen5Operand.Vector(2), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x0008_0014), Operand(0))
                with { Control = new Gen5Vop3pControl(0, 0b011, 0, 0, false) },
            Vop3(20, "VPkMaxI16", 12, new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x7FFF), Gen5Operand.Vector(1), Operand(0))
                with { Control = new Gen5Vop3pControl(0, 0b110, 0b001, 0b001, false) },
            Vop3(32, "VPkMinI16", 13, Gen5Operand.Vector(1), Gen5Operand.Vector(1), Operand(0))
                with { Control = new Gen5Vop3pControl(0, 0b111, 0b010, 0, false) },
            Vop2(40, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(44, "BufferStoreDword", 4, vectorData: 12, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(52, "BufferStoreDword", 4, offset: 4, vectorData: 13, offsetEnabled: true, vectorAddress: 7),
            EndProgram(60));
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
        for (var lane = 0; lane < ThreadCount; lane++)
        {
            var row = actual.AsSpan((int)(lane * RowBytes));
            var low = (short)(lane - 20);
            var high = (short)(lane - 8);
            var maximum = BinaryPrimitives.ReadUInt32LittleEndian(row);
            Assert.Equal((ushort)Math.Max((short)-1, low), maximum & 0xFFFF);
            Assert.Equal((ushort)Math.Max((short)-1, high), maximum >> 16);
            var minimum = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            Assert.Equal((ushort)Math.Min(low, (short)(ushort)((ushort)low ^ 0x8000)), minimum & 0xFFFF);
            Assert.Equal((ushort)high, minimum >> 16);
        }
    }

    // The clamp bit saturates the exact result of a packed add, subtract or multiply-add to the 16-bit
    // range instead of wrapping it. Each op has lanes on both sides of its limit.
    [Fact]
    public void PackedIntegerClampSaturatesTheExactResult()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var clamped = new Gen5Vop3pControl(0, 0b111, 0, 0, true);
        static Gen5Operand Literal(uint value) => new(Gen5OperandKind.LiteralConstant, value);
        var program = Program(
            Vop2(0, "VLshlrevB32", 3, Operand(16), Gen5Operand.Vector(0)),
            Vop2(4, "VOrB32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(3)),
            Vop3(8, "VPkAddU16", 10, Gen5Operand.Vector(2), Literal(0xFFF0_FFF0), Operand(0)) with { Control = clamped },
            Vop3(20, "VPkSubU16", 11, Gen5Operand.Vector(2), Literal(0x0010_0010), Operand(0)) with { Control = clamped },
            Vop3(32, "VPkMadI16", 12, Gen5Operand.Vector(2), Literal(0xF800_0800), Gen5Operand.Vector(2)) with { Control = clamped },
            Vop3(44, "VPkMadU16", 13, Gen5Operand.Vector(2), Literal(0x1000_1000), Gen5Operand.Vector(2)) with { Control = clamped },
            Vop2(56, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(60, "BufferStoreDword", 4, vectorData: 10, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(68, "BufferStoreDword", 4, offset: 4, vectorData: 11, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(76, "BufferStoreDword", 4, offset: 8, vectorData: 12, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(84, "BufferStoreDword", 4, offset: 12, vectorData: 13, offsetEnabled: true, vectorAddress: 7),
            EndProgram(92));
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
        static uint Pair(int low, int high) => (ushort)low | ((uint)(ushort)high << 16);
        for (var lane = 0; lane < ThreadCount; lane++)
        {
            var row = actual.AsSpan((int)(lane * RowBytes));
            var sum = Math.Min(0xFFF0 + lane, 0xFFFF);
            var difference = Math.Max(lane - 16, 0);
            var unsignedProduct = Math.Min(4097 * lane, 0xFFFF);
            Assert.Equal(Pair(sum, sum), BinaryPrimitives.ReadUInt32LittleEndian(row));
            Assert.Equal(Pair(difference, difference), BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
            Assert.Equal(
                Pair(Math.Min(2049 * lane, short.MaxValue), Math.Max(-2047 * lane, short.MinValue)),
                BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            Assert.Equal(Pair(unsignedProduct, unsignedProduct), BinaryPrimitives.ReadUInt32LittleEndian(row[12..]));
        }
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}

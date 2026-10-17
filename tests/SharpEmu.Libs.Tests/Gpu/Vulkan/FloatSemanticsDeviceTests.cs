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

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}

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

public sealed class FmaMixDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    public static IEnumerable<object[]> LiteralCases()
    {
        foreach (var opcode in new[] { "VFmaMixF32", "VFmaMixloF16", "VFmaMixhiF16" })
        foreach (var sourceIndex in Enumerable.Range(0, 3))
        foreach (var highHalf in new[] { false, true })
            yield return [opcode, sourceIndex, highHalf];
    }

    [Theory]
    [MemberData(nameof(LiteralCases))]
    public void LiteralUsesSelectedHalf(string opcode, int sourceIndex, bool highHalf)
    {
        var sources = new[] { Gen5Operand.Vector(1), Gen5Operand.Vector(1), Gen5Operand.Vector(1) };
        sources[sourceIndex] = new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x42004000);
        var mask = 1u << sourceIndex;
        CheckResult(opcode, sources, new Gen5Vop3pControl(highHalf ? mask : 0, mask, 0, 0, false),
            highHalf ? 4f : 3f);
    }

    [Theory]
    [InlineData(0x34CDu)]
    [InlineData(0x38B8u)]
    [InlineData(0x2F0Au)]
    public void LiteralWeightUsesHalfPrecision(uint literal)
    {
        CheckResult("VFmaMixF32",
            [Gen5Operand.Vector(1), new(Gen5OperandKind.LiteralConstant, literal), Gen5Operand.Source(128)],
            new Gen5Vop3pControl(0, 2, 0, 0, false),
            (float)BitConverter.UInt16BitsToHalf((ushort)literal));
    }

    [Theory]
    [InlineData(0x40000000u, false, false, false, 3f)]
    [InlineData(0xC000u, true, true, false, 3f)]
    [InlineData(0xC000u, true, true, true, -1f)]
    public void LiteralHonorsPrecisionAndModifiers(uint literal, bool half, bool absolute, bool negate, float expected)
    {
        CheckResult("VFmaMixF32",
            [new(Gen5OperandKind.LiteralConstant, literal), Gen5Operand.Vector(1), Gen5Operand.Vector(1)],
            new Gen5Vop3pControl(0, half ? 1u : 0, negate ? 1u : 0, absolute ? 1u : 0, false), expected);
    }

    [Fact]
    public void InlineFloatRemainsFullPrecisionWithHalfSelection()
    {
        CheckResult("VFmaMixF32", [Gen5Operand.Source(242), Gen5Operand.Vector(1), Gen5Operand.Vector(1)],
            new Gen5Vop3pControl(1, 1, 0, 0, false), 2f);
    }

    // A half operand widens exactly: fma(h, 1, -0) returns every f16 value as its f32 value, the subnormals and signed
    // zeros included, and a NaN as a NaN. Each dispatch widens 1,024 consecutive bit patterns, one per lane.
    [Fact]
    public void EveryHalfOperandWidensExactly()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        const uint lanes = 1024;
        var (plan, resources, layout) = Prepare(Program([
            Vop2(0, "VAddU32", 1, Gen5Operand.Scalar(8), Gen5Operand.Vector(0)),
            new Gen5ShaderInstruction(4, Gen5ShaderEncoding.Vop3p, "VFmaMixF32", [0u, 0u],
                [Gen5Operand.Vector(1), Gen5Operand.Source(242), new(Gen5OperandKind.LiteralConstant, 0x8000_0000)],
                [Gen5Operand.Vector(2)], new Gen5Vop3pControl(0, 1, 0, 0, false)),
            Vop2(16, "VLshlrevB32", 3, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(20, "BufferStoreDword", 4, vectorData: 2, offsetEnabled: true, vectorAddress: 3),
            EndProgram(28),
        ]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = lanes, ThreadCountX = lanes, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(lanes * sizeof(uint));
        var registers = new uint[256];
        registers[6] = lanes * sizeof(uint);
        for (uint first = 0; first <= ushort.MaxValue; first += lanes)
        {
            registers[8] = first;
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
            var widened = runner.ReadBack(output, 0, lanes * sizeof(uint));
            for (var lane = 0u; lane < lanes; lane++)
            {
                var half = BitConverter.UInt16BitsToHalf((ushort)(first + lane));
                var actual = BinaryPrimitives.ReadUInt32LittleEndian(widened.AsSpan((int)(lane * sizeof(uint))));
                if (Half.IsNaN(half))
                {
                    Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(actual)), $"f16 0x{first + lane:X4} widened to 0x{actual:X8}");
                }
                else
                {
                    Assert.Equal(BitConverter.SingleToUInt32Bits((float)half), actual);
                }
            }
        }

        harness.AssertNoValidationMessages();
    }

    private void CheckResult(string opcode, Gen5Operand[] sources, Gen5Vop3pControl control, float expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        const uint previous = 0xA5A55A5A;
        var (plan, resources, layout) = Prepare(Program([
            MoveVector(0, 1, 0x3F800000),
            MoveVector(4, 2, previous),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Vop3p, opcode, [0u, 0u], sources,
                [Gen5Operand.Vector(2)], control),
            BufferAccess(16, "BufferStoreDword", 4, 0, 1, 2),
            EndProgram(24),
        ]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1, ThreadCountX = 1, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var expectedBits = opcode switch
        {
            "VFmaMixloF16" => (previous & 0xFFFF0000) | BitConverter.HalfToUInt16Bits((Half)expected),
            "VFmaMixhiF16" => (previous & 0x0000FFFF) | ((uint)BitConverter.HalfToUInt16Bits((Half)expected) << 16),
            _ => BitConverter.SingleToUInt32Bits(expected),
        };
        Assert.Equal(expectedBits, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(output, 0, 4)));
        harness.AssertNoValidationMessages();
    }
}

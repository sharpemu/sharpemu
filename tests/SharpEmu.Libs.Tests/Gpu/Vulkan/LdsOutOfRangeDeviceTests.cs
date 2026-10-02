// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// An LDS access past the allocation reads 0 and drops writes. Ghost of Yotei's exposure blur
// (0xD06A706D2994D2B1) reads neighbours at negative addresses for its edge lanes; wrapping them
// into the array read the wave64 scratch instead and turned the exposure into NaN.
public sealed class LdsOutOfRangeDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void OutOfRangeReadsZeroAndWritesAreDropped()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        const uint Pattern = 0x7FC0_1234;
        var program = Program(
            Vop2(0, "VLshlrevB32", 1, Operand(2), Gen5Operand.Vector(0)),
            MoveVectorFromScalar(4, 2, 8),
            MoveVectorFromScalar(8, 3, 9),
            // LDS[lane] = pattern; a write at 0xFFFFFFF0 must be dropped.
            DataShare(12, "DsWriteB32", false, [Gen5Operand.Vector(1), Gen5Operand.Vector(2)], []),
            DataShare(20, "DsWriteB32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(2)], []),
            // v4 = LDS[0xFFFFFFF0] (out of range: 0), v5 = LDS[lane] (the pattern).
            DataShare(28, "DsReadB32", false, [Gen5Operand.Vector(3)], [4]),
            DataShare(36, "DsReadB32", false, [Gen5Operand.Vector(1)], [5]),
            Vop2(44, "VLshlrevB32", 6, Operand(3), Gen5Operand.Vector(0)),
            BufferAccess(48, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 4, offsetEnabled: true, vectorAddress: 6),
            EndProgram(56));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        const uint bytes = 64 * 8;
        var output = runner.CreateBuffer(bytes);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = Enumerable.Repeat(output, Math.Max(1, plan.Info.Buffers.Count)).ToArray(),
        };
        var registers = new uint[256];
        registers[6] = bytes;
        registers[8] = Pattern;
        registers[9] = 0xFFFF_FFF0;
        harness.Run(() => runner.Dispatch(registers, bindings, 1));
        harness.AssertNoValidationMessages();
        var data = runner.ReadBack(output, 0, bytes);
        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal(0u, BitConverter.ToUInt32(data, lane * 8));
            Assert.Equal(Pattern, BitConverter.ToUInt32(data, lane * 8 + 4));
        }
    }
}

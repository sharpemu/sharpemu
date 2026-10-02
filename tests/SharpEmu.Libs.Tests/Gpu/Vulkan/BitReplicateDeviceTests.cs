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

// S_BITREPLICATE_B64_B32 doubles each source bit (bit i -> bits 2i and 2i+1). Ghost of Yotei's
// bloom blur builds its halo exec mask from 0x003F003F (lanes 0-11 and 32-43); copying the dword
// into both halves left halo LDS unwritten and turned the bloom, then the whole frame, to NaN.
public sealed class BitReplicateDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(0x003F_003Fu, 0x0000_0FFFu, 0x0000_0FFFu)]
    [InlineData(0x8000_0001u, 0x0000_0003u, 0xC000_0000u)]
    [InlineData(0x0000_A5C3u, 0xCC33_F00Fu, 0x0000_0000u)]
    public void EachBitIsDoubled(uint source, uint low, uint high)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Sop1(0, "SBitreplicateB64B32", 20, Gen5Operand.Scalar(8)),
            MoveVectorFromScalar(4, 20, 20),
            MoveVectorFromScalar(8, 21, 21),
            Vop2(12, "VAndB32", 1, Operand(0), Gen5Operand.Vector(1)),
            BufferAccess(16, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 20, offsetEnabled: true, vectorAddress: 1),
            EndProgram(24));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(8);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = Enumerable.Repeat(output, Math.Max(1, plan.Info.Buffers.Count)).ToArray(),
        };
        var registers = new uint[256];
        registers[6] = 8;
        registers[8] = source;
        harness.Run(() => runner.Dispatch(registers, bindings, 1));
        harness.AssertNoValidationMessages();
        var bytes = runner.ReadBack(output, 0, 8);
        Assert.Equal(low, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(high, BitConverter.ToUInt32(bytes, 4));
    }
}

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

public sealed class LogicalShift64DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, true)]
    [InlineData(31u, true)]
    [InlineData(32u, true)]
    [InlineData(63u, true)]
    [InlineData(64u, true)]
    [InlineData(127u, true)]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(31u, false)]
    [InlineData(32u, false)]
    [InlineData(63u, false)]
    [InlineData(64u, false)]
    [InlineData(127u, false)]
    public void LogicalShift64MasksCountAndPreservesOverlappingSource(uint shift, bool left)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        const ulong value = 0x8123456789ABCDEF;
        var program = Program(
            MoveVector(0, 4, unchecked((uint)value)),
            MoveVector(8, 5, (uint)(value >> 32)),
            Vop3(16, left ? "VLshlrevB64" : "VLshrrevB64", 4, Operand(shift), Gen5Operand.Vector(4)),
            BufferAccess(24, "BufferStoreDwordx2", 8, dwords: 2, vectorData: 4),
            EndProgram(32));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1, ThreadCountX = 1, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(8);
        var registers = new uint[256];
        registers[10] = 8;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var expected = left ? value << (int)(shift & 63) : value >> (int)(shift & 63);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt64LittleEndian(runner.ReadBack(output, 0, 8)));
        harness.AssertNoValidationMessages();
    }

}

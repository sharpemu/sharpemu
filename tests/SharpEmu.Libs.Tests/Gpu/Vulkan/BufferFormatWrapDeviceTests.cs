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

// A formatted load reads component c from byte (elementAddress + 4c) in 32 bits. With the element at 2^32 - 4 (plus 0-3
// bytes), Y, Z and W wrap to the view's first bytes; with only those selected the load reads them, aligned or not.
public sealed class BufferFormatWrapDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    public void ComponentsPastTheTopOfTheByteSpaceWrapToTheViewsStart(uint misalignment)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Program(MoveScalar(0, 126, 1), MoveScalar(4, 127, 0),
            MoveVector(8, 0, 0xFFFF_FFFC + misalignment),
            BufferAccess(16, "BufferLoadFormatXyzw", 8, dwords: 4, vectorData: 4, offsetEnabled: true),
            BufferAccess(24, "BufferStoreDwordx4", 12, dwords: 4, vectorData: 4), EndProgram(32));
        var (plan, resources, layout) = Prepare(program);
        // 32_32_32_32 UINT; x = Y, y = Z, z = W, w = 0.
        resources.Info.Buffers[0].DescriptorFormat = 75;
        resources.Info.Buffers[0].DescriptorSwizzle = 5 | (6 << 3) | (7 << 6);
        var request = new ShaderCompileRequest(plan, resources, layout) { WaveSize = 64, LocalSizeX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var bytes = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        var input = runner.CreateBuffer(bytes);
        var output = runner.CreateBuffer(16);
        var registers = new uint[256];
        registers[10] = 64;
        registers[14] = 16;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [input, output] }, 1));
        var actual = runner.ReadBack(output, 0, 16);
        uint Bytes(uint first) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)first));
        Assert.Equal([Bytes(misalignment), Bytes(misalignment + 4), Bytes(misalignment + 8), 0u],
            Enumerable.Range(0, 4).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * 4))).ToArray());
        harness.AssertNoValidationMessages();
    }
}

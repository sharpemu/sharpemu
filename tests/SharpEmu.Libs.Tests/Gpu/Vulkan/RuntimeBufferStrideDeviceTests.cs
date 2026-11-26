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

// An indexed buffer access multiplies its index by its binding's V# stride at run time, so one module serves every
// stride a game binds.
public sealed class RuntimeBufferStrideDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void OneModuleLoadsTheIndexedElementOfEachStride()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        // v1 = 2; v4 = source[v1 * stride]; result[0] = v4.
        var program = Program(
            MoveVector(0, 1, 2),
            BufferAccess(8, "BufferLoadDword", 4, offset: 0, vectorData: 4, indexEnabled: true, vectorAddress: 1),
            BufferAccess(16, "BufferStoreDword", 8, offset: 0, vectorData: 4),
            EndProgram(24));
        var input = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(2 * 4), 0xAAAA0004);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(2 * 12), 0xBBBB000C);
        var (plan, resources, layout) = Prepare(program);
        Assert.Equal(2u, layout.BufferStrideCount);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan!);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var source = runner.CreateBuffer(input);
        foreach (var (stride, expected) in new[] { (4u, 0xAAAA0004u), (12u, 0xBBBB000Cu) })
        {
            var result = runner.CreateBuffer(64);
            var registers = new uint[256];
            registers[4] = 0x1000; registers[5] = stride << 16; registers[6] = 4;
            registers[8] = 0x2000; registers[10] = 64;
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [source, result] }, 1,
                bufferRanges: [64, 64], memoryOffsets: [0, 0], bufferStrides: [stride, 0]));
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, 4)));
        }
    }
}

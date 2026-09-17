// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class FlattenedReadReuseDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    public void EquivalentReads_StoreBothResultsWithoutAPageTable(uint components)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        var request = Request(FlattenedReadReuseTests.RepeatedReadProgram(components), userDataCount: 9);
        Assert.False(request.Resources.Info.UsesDeviceAddresses);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [result],
        };
        uint[] flattenedTable = [0x12345678, 0xABCDEF01, 0xFEDCBA98, 0x76543210];
        harness.Run(() => runner.Dispatch(registers, bindings, 1, flattenedTable: flattenedTable[..(int)components]));
        harness.Finish();
        var bytes = runner.ReadBack(result, 0, components * 8);
        for (uint index = 0; index < components * 2; index++)
        {
            Assert.Equal(flattenedTable[index % components], BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)index * 4)));
        }

        harness.AssertNoValidationMessages();
    }
}

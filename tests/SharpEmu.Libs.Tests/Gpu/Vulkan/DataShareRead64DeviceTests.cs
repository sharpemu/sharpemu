// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class DataShareRead64DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [MemberData(nameof(Gen5DataShareRead64Tests.PairedReadCases), MemberType = typeof(Gen5DataShareRead64Tests))]
    public void PairedReadReturnsFourWordsFromIndependentOffsets(bool global, uint firstOffset, uint secondOffset, bool readEnabled)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Gen5DataShareRead64Tests.CreatePairedReadbackProgram(global, firstOffset, secondOffset, readEnabled);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [result],
        };
        if (global) bindings[DescriptorBindingKind.GlobalDataShare] = [runner.CreateBuffer(4096)];
        var registers = new uint[256];
        registers[6] = 64;
        harness.Run(() => runner.Dispatch(registers, bindings, 1));
        var actual = runner.ReadBack(result, 0, 16);
        var expected = Gen5DataShareRead64Tests.PairedReadExpected(firstOffset, secondOffset, readEnabled);
        for (var component = 0; component < expected.Length; component++)
            Assert.Equal(expected[component], BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(component * sizeof(uint))));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(false, 0u, true)]
    [InlineData(false, 0x108u, true)]
    [InlineData(false, 0u, false)]
    [InlineData(false, 0x108u, false)]
    [InlineData(true, 0u, true)]
    [InlineData(true, 0x108u, true)]
    [InlineData(true, 0u, false)]
    [InlineData(true, 0x108u, false)]
    public void ReadReturnsBothWordsAndPreservesInactiveRegisters(bool global, uint offset, bool readEnabled)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var program = Gen5DataShareRead64Tests.CreateReadbackProgram(global, offset, readEnabled);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [result],
        };
        if (global) bindings[DescriptorBindingKind.GlobalDataShare] = [runner.CreateBuffer(1024)];
        var registers = new uint[256];
        registers[6] = 64;
        harness.Run(() => runner.Dispatch(registers, bindings, 1));

        var actual = runner.ReadBack(result, 0, 64);
        Assert.Equal(readEnabled ? Gen5DataShareRead64Tests.LowerWord : Gen5DataShareRead64Tests.SourceAddress,
            BinaryPrimitives.ReadUInt32LittleEndian(actual));
        Assert.Equal(readEnabled ? Gen5DataShareRead64Tests.UpperWord : Gen5DataShareRead64Tests.UnchangedUpperWord,
            BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(4)));
        harness.AssertNoValidationMessages();
    }
}

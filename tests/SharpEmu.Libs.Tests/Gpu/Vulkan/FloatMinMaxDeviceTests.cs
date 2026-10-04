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

public sealed class FloatMinMaxDeviceTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void NanAndSignedZeroEdgesMatchGuestSemantics()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var (plan, resources, layout) = Prepare(
            Gen5FloatMinMaxTests.CreateReadbackProgram(),
            userDataCount: 15);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var resultSize = Gen5FloatMinMaxTests.ExpectedResults.Length * sizeof(uint);
        var result = runner.CreateBuffer((ulong)resultSize);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [result],
        };
        var registers = new uint[256];
        registers[6] = (uint)resultSize;
        Gen5FloatMinMaxTests.Inputs.CopyTo(registers, 8);

        harness.Run(() => runner.Dispatch(registers, bindings, 1));

        var actual = runner.ReadBack(result, 0, (ulong)resultSize);
        for (var index = 0; index < Gen5FloatMinMaxTests.ExpectedResults.Length; index++)
        {
            Assert.Equal(
                Gen5FloatMinMaxTests.ExpectedResults[index],
                BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }
}

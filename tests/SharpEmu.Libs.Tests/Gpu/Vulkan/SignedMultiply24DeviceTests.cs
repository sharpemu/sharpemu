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

public sealed class SignedMultiply24DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void MultiplyUsesSignedInputsAndPreservesInactiveDestination(bool extendedEncoding, bool multiplyEnabled)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Gen5SignedMultiply24Tests.CreateReadbackProgram(extendedEncoding, multiplyEnabled));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [result],
        };
        var registers = new uint[256];
        registers[6] = 64;
        foreach (var product in Gen5SignedMultiply24Tests.Products)
        {
            registers[8] = (uint)product[0];
            registers[9] = (uint)product[1];
            harness.Run(() => runner.Dispatch(registers, bindings, 1));
            var actual = runner.ReadBack(result, 0, 4);
            Assert.Equal(multiplyEnabled ? (uint)product[2] : 0xCAFEBABE,
                BinaryPrimitives.ReadUInt32LittleEndian(actual));
        }
        harness.AssertNoValidationMessages();
    }
}

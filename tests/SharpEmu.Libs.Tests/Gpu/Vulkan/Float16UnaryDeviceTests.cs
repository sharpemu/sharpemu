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

public sealed class Float16UnaryDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(0x0000u, 0xFC00u)]
    [InlineData(0x8000u, 0xFC00u)]
    [InlineData(0x3C00u, 0x0000u)]
    [InlineData(0x4000u, 0x3C00u)]
    [InlineData(0x3800u, 0xBC00u)]
    [InlineData(0x0001u, 0xCE00u)]
    [InlineData(0x7C00u, 0x7C00u)]
    [InlineData(0xFC00u, 0xFE00u)]
    [InlineData(0xBC00u, 0xFE00u)]
    [InlineData(0x7E00u, 0xFE00u)]
    public void LogarithmProducesHalfResultAndPreservesUpperHalf(uint input, uint expected)
        => CheckResult("VLogF16", input, expected);

    [Theory]
    [InlineData(0x0000u, 0x3C00u)]
    [InlineData(0x8000u, 0x3C00u)]
    [InlineData(0x3C00u, 0x4000u)]
    [InlineData(0xBC00u, 0x3800u)]
    [InlineData(0xCE00u, 0x0001u)]
    [InlineData(0xCE40u, 0x0000u)]
    [InlineData(0xCC00u, 0x0100u)]
    [InlineData(0x4C00u, 0x7C00u)]
    [InlineData(0x7C00u, 0x7C00u)]
    [InlineData(0xFC00u, 0x0000u)]
    [InlineData(0x7E00u, 0x7E00u)]
    public void ExponentialProducesHalfResultAndPreservesUpperHalf(uint input, uint expected)
        => CheckResult("VExpF16", input, expected);

    [Theory]
    [InlineData(0x0000u, 0x7C00u)]
    [InlineData(0x8000u, 0xFC00u)]
    [InlineData(0x7C00u, 0x0000u)]
    [InlineData(0xFC00u, 0x8000u)]
    [InlineData(0x3C00u, 0x3C00u)]
    [InlineData(0xC000u, 0xB800u)]
    [InlineData(0x4200u, 0x3555u)]
    [InlineData(0x0001u, 0x7C00u)]
    [InlineData(0x0400u, 0x7400u)]
    [InlineData(0x7400u, 0x0400u)]
    [InlineData(0x7800u, 0x0200u)]
    [InlineData(0x7E00u, 0x7E00u)]
    public void ReciprocalProducesHalfResultAndPreservesUpperHalf(uint input, uint expected)
        => CheckResult("VRcpF16", input, expected);

    [Theory]
    [InlineData(0x0000u, 0x0000u)]
    [InlineData(0x8000u, 0x8000u)]
    [InlineData(0x4400u, 0x4000u)]
    [InlineData(0x4000u, 0x3DA8u)]
    [InlineData(0x0001u, 0x0C00u)]
    [InlineData(0x0400u, 0x2000u)]
    [InlineData(0x7C00u, 0x7C00u)]
    [InlineData(0xFC00u, 0xFE00u)]
    [InlineData(0xBC00u, 0xFE00u)]
    [InlineData(0x7E00u, 0xFE00u)]
    public void SquareRootProducesHalfResultAndPreservesUpperHalf(uint input, uint expected)
        => CheckResult("VSqrtF16", input, expected);

    private void CheckResult(string opcode, uint input, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var (plan, resources, layout) = Prepare(Program([
            MoveVector(0, 1, input),
            MoveVector(4, 2, 0xA5A50000),
            Vop1(8, opcode, 2, Gen5Operand.Vector(1)),
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
        var bytes = runner.ReadBack(output, 0, 4);
        Assert.Equal(0xA5A50000 | expected, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        harness.AssertNoValidationMessages();
    }
}

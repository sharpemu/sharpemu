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

public sealed class ComputePrefixDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(false, 1u)]
    [InlineData(true, 1u)]
    [InlineData(false, 3u)]
    [InlineData(true, 3u)]
    public void ForwardPrefixSelectsOnePathBeforeTheRepeatedSuffix(bool firstPath, uint iterations)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var (plan, resources, layout) = Prepare(Program(
            MoveScalar(0, 20, 0),
            Sopc(4, "SCmpEqU32", Operand(firstPath ? 1u : 0u), Operand(1u)),
            Branch(8, "SCbranchScc0", 2),
            MoveScalar(12, 20, 10),
            Branch(16, "SBranch", 1),
            MoveScalar(20, 20, 20),
            MoveScalar(24, 21, 0),
            Sop2(28, "SAddU32", 21, Gen5Operand.Scalar(21), Operand(1u)),
            Sopc(32, "SCmpLtU32", Gen5Operand.Scalar(21), Operand(iterations)),
            Branch(36, "SCbranchScc1", -3),
            MoveVectorFromScalar(40, 2, 20),
            MoveVectorFromScalar(44, 3, 21),
            BufferAccess(48, "BufferStoreDwordx2", 4, 0, 2, 2),
            EndProgram(56)));
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
        var bytes = runner.ReadBack(output, 0, 8);
        Assert.Equal(firstPath ? 10u : 20u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(iterations, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        harness.AssertNoValidationMessages();
    }
}

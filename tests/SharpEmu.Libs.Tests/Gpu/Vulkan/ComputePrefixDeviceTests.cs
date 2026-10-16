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
    [InlineData(false)]
    [InlineData(true)]
    public void ForwardBlocksPreserveSharedValuesAcrossWaveHalves(bool executeUpperHalf)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Program(
            Vop3(0, "VLshlAddU32", 6, Gen5Operand.Vector(1), Operand(3), Gen5Operand.Vector(0)),
            Vop2(8, "VLshlrevB32", 2, Operand(2), Gen5Operand.Vector(6)),
            Vop2(12, "VAddU32", 3, Operand(100), Gen5Operand.Vector(6)),
            DataShare(16, "DsWriteB32", false, [Gen5Operand.Vector(2), Gen5Operand.Vector(3)], []),
            Vopc(24, executeUpperHalf ? "VCmpxLeU32" : "VCmpxGtU32", Operand(32), 6),
            Branch(28, "SCbranchExecz", 3),
            Vop2(32, "VAddU32", 3, Operand(200), Gen5Operand.Vector(6)),
            DataShare(36, "DsWriteB32", false, [Gen5Operand.Vector(2), Gen5Operand.Vector(3)], []),
            Sop1(44, "SMovB64", 126, Gen5Operand.Source(193)),
            Vop2(48, "VXorB32", 4, Operand(32), Gen5Operand.Vector(6)),
            Vop2(52, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(4)),
            DataShare(56, "DsReadB32", false, [Gen5Operand.Vector(4)], [5]),
            BufferAccess(64, "BufferStoreDword", 8, vectorData: 5, offsetEnabled: true, vectorAddress: 2),
            EndProgram(72)));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, ThreadCountX = 8, ThreadCountY = 8, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[10] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var bytes = runner.ReadBack(output, 0, 256);
        for (var lane = 0; lane < 64; lane++)
        {
            var sourceLane = lane ^ 32;
            var updated = executeUpperHalf ? sourceLane >= 32 : sourceLane < 32;
            Assert.Equal((uint)(sourceLane + (updated ? 200 : 100)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(lane * 4)));
        }
        harness.AssertNoValidationMessages();
    }

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

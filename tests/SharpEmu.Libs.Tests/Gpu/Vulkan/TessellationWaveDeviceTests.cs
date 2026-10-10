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

public sealed class TessellationWaveDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void MergedHull_BackAddressIsUploadedAndSurvivesLocalUserRegisterReuse()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var merged = Program(
            MoveScalar(0, 8, 0xBAD), MoveScalar(4, 9, 0xBAD),
            MoveVectorFromScalar(256, 10, 0), MoveVectorFromScalar(260, 11, 1),
            Vop2(264, "VLshlrevB32", 12, Operand(3), Gen5Operand.Vector(3)),
            BufferAccess(268, "BufferStoreDwordx2", 24, dwords: 2, vectorData: 10, offsetEnabled: true, vectorAddress: 12),
            EndProgram(276)) with { FusedContinuationPc = 256 };
        var program = Gen5TessellationLowering.PrepareMergedHull(merged, 32);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 8, 26, waveSize: 64);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 8, 26),
            false, false, false, usesTessellationData: true);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, WaveSize = 64, CooperativeWave64Workgroup = true,
            TessellationHull = new(4, 4, 16, program.FusedContinuationPc!.Value),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        SharpEmu.ShaderCompiler.Tests.Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var buffer = runner.CreateBuffer(512);
        var scalar = new uint[34]; scalar[8] = 0x1111; scalar[9] = 0x20; scalar[26] = 512;
        var data = new uint[Gen5TessellationData.DwordCount]; data[Gen5TessellationData.PatchCount] = 16;
        foreach (ulong address in new[] { 0x20_123456BCul, 0x21_87654304ul })
        {
            scalar[32] = (uint)address; scalar[33] = (uint)(address >> 32);
            harness.Run(() => runner.Dispatch(scalar,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1,
                tessellationData: data));
            var bytes = runner.ReadBack(buffer, 0, 512);
            for (var lane = 0; lane < 64; lane++)
                Assert.Equal(address, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(lane * 8)));
        }
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(128u)]
    [InlineData(256u)]
    public void MultipleWaves_CollectAllSixtyFourLaneBits(uint threads)
    {
        var values = Run(Program(
            Vopc(0, "VCmpLeU32", Operand(0), 0),
            MoveVectorFromScalar(4, 4, 106),
            Vop2(8, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(12, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(20)), threads);
        if (values is null) return;
        Assert.All(values, value => Assert.Equal(uint.MaxValue, value));
    }

    [Fact]
    public void IndependentlyBranchingWaves_PreserveTheirScalarAndVectorState()
    {
        var program = Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop1, "VReadfirstlaneB32", [0u],
                [Gen5Operand.Vector(0)], [Gen5Operand.Scalar(20)], null),
            Sopc(4, "SCmpLtU32", Gen5Operand.Scalar(20), Operand(64)),
            Branch(8, "SCbranchScc0", 5), // else at 32
            MoveScalar(12, 21, 111),
            Vop1(16, "VMovB32", 4, Operand(333)),
            Branch(20, "SBranch", 4), // join at 40
            Nop(24), Nop(28),
            MoveScalar(32, 21, 222),
            Vop1(36, "VMovB32", 4, Operand(444)),
            Vop2(40, "VAddI32", 4, Gen5Operand.Scalar(21), Gen5Operand.Vector(4)),
            Vop2(44, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(48, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(56));
        var values = Run(program, 256);
        if (values is null) return;
        for (var lane = 0; lane < values.Length; lane++) Assert.Equal(lane < 64 ? 444u : 666u, values[lane]);
    }

    [Fact]
    public void LocalAndHullPhases_ExchangeDataAcrossDifferentWaves()
    {
        var program = Program(
            Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            Vop2(4, "VAddI32", 4, Operand(37), Gen5Operand.Vector(0)),
            DataShare(8, "DsWriteB32", false, [Gen5Operand.Vector(5), Gen5Operand.Vector(4)], []),
            new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null),
            Vop2(20, "VXorB32", 6, Operand(64), Gen5Operand.Vector(0)),
            Vop2(24, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(6)),
            DataShare(28, "DsReadB32", false, [Gen5Operand.Vector(6)], [4]),
            BufferAccess(36, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(44));
        var values = Run(program, 256, 256);
        if (values is null) return;
        for (var lane = 0; lane < values.Length; lane++) Assert.Equal((uint)(lane ^ 64) + 37, values[lane]);
    }

    // Null when the device cannot run the test.
    private uint[]? Run(Gen5ShaderProgram program, uint threads, uint ldsDwords = 0)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return null;
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 16, waveSize: 64);
        var registers = new uint[16]; registers[6] = threads * 4;
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        { LocalSizeX = threads, WaveSize = 64, CooperativeWave64Workgroup = true, LocalDataShareDwords = ldsDwords };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        SharpEmu.ShaderCompiler.Tests.Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var buffer = runner.CreateBuffer(threads * 4);
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
        var bytes = runner.ReadBack(buffer, 0, threads * 4);
        var values = new uint[threads];
        for (var lane = 0; lane < threads; lane++) values[lane] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(lane * 4));
        harness.AssertNoValidationMessages();
        return values;
    }

    [Fact]
    public void MergedHullInputs_SplitPatchesNumberedAcrossInstances()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Vop1(0, "VMovB32", 10, Gen5Operand.Vector(1)),
            Vop1(4, "VMovB32", 11, Gen5Operand.Vector(0)),
            Vop1(8, "VMovB32", 12, Gen5Operand.Vector(2)),
            Vop1(12, "VMovB32", 13, Gen5Operand.Vector(5)),
            Vop2(16, "VLshlrevB32", 14, Operand(4), Gen5Operand.Vector(3)),
            BufferAccess(20, "BufferStoreDwordx4", 24, dwords: 4, vectorData: 10, offsetEnabled: true, vectorAddress: 14),
            EndProgram(28));
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 64, waveSize: 64);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 64),
            false, false, false, usesTessellationData: true);
        var request = new ShaderCompileRequest(plan, resources, layout)
        { LocalSizeX = 256, WaveSize = 64, CooperativeWave64Workgroup = true, TessellationHull = new(4, 4, 63, 0) };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var buffer = runner.CreateBuffer(4096);
        var scalar = new uint[64]; scalar[26] = 4096;
        var data = new uint[Gen5TessellationData.DwordCount];
        data[Gen5TessellationData.FirstPatch] = 7;
        data[Gen5TessellationData.PatchCount] = 63;
        data[Gen5TessellationData.VertexOffset] = 11;
        data[Gen5TessellationData.InstanceId] = 100;
        data[Gen5TessellationData.PatchesPerInstance] = 10;
        harness.Run(() => runner.Dispatch(scalar,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1,
            tessellationData: data));
        var bytes = runner.ReadBack(buffer, 0, 4096);
        for (uint lane = 0; lane < 252; lane++)
        {
            uint Read(int component) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)lane * 16 + component * 4));
            var patch = 7 + lane / 4;
            Assert.Equal((lane / 4) | ((lane % 4) << 8), Read(0));
            Assert.Equal(patch % 10, Read(1));
            Assert.Equal(patch % 10 * 4 + lane % 4 + 11, Read(2));
            Assert.Equal(100 + patch / 10, Read(3));
        }
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(2u, 2u)]
    [InlineData(4u, 0u)]
    public void MergedHullInputs_IncludePartialWaveCountsAndPatchControlPointIds(uint indexSize, uint byteBias)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            MoveVectorFromScalar(0, 10, 3),
            Vop1(4, "VMovB32", 11, Gen5Operand.Vector(0)),
            Vop1(8, "VMovB32", 12, Gen5Operand.Vector(1)),
            Vop1(12, "VMovB32", 13, Gen5Operand.Vector(2)),
            Vop2(16, "VLshlrevB32", 14, Operand(4), Gen5Operand.Vector(3)),
            BufferAccess(20, "BufferStoreDwordx4", 24, dwords: 4, vectorData: 10, offsetEnabled: true, vectorAddress: 14),
            EndProgram(28));
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 64, waveSize: 64);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 64),
            false, false, false, usesTessellationData: true);
        var request = new ShaderCompileRequest(plan, resources, layout)
        { LocalSizeX = 256, WaveSize = 64, CooperativeWave64Workgroup = true, TessellationHull = new(4, 4, 63, 0) };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        SharpEmu.ShaderCompiler.Tests.Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var buffer = runner.CreateBuffer(4096);
        var scalar = new uint[64]; scalar[26] = 4096;
        var data = new uint[Gen5TessellationData.DwordCount];
        data[Gen5TessellationData.FirstPatch] = 7;
        data[Gen5TessellationData.PatchCount] = 63;
        data[Gen5TessellationData.VertexOffset] = 11;
        data[Gen5TessellationData.IndexSize] = indexSize;
        data[Gen5TessellationData.IndexByteOffset] = byteBias;
        uint Index(uint index) => indexSize switch { 1 => index % 127, 2 => 1000 + index, _ => 0xFEDC0000 + index };
        if (indexSize != 0)
        {
            var indices = new byte[(int)((284 * indexSize + byteBias + 3) & ~3u)];
            for (uint index = 0; index < 284; index++)
            {
                var offset = (int)(byteBias + index * indexSize);
                if (indexSize == 1) indices[offset] = (byte)Index(index);
                else if (indexSize == 2) BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(offset), (ushort)Index(index));
                else BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(offset), Index(index));
            }
            var indexBuffer = runner.CreateBuffer(indices);
            data[Gen5TessellationData.IndexAddress] = (uint)indexBuffer.DeviceAddress;
            data[Gen5TessellationData.IndexAddress + 1] = (uint)(indexBuffer.DeviceAddress >> 32);
        }
        harness.Run(() => runner.Dispatch(scalar,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1,
            tessellationData: data));
        var bytes = runner.ReadBack(buffer, 0, 4096);
        for (uint lane = 0; lane < 256; lane++)
        {
            uint Read(int component) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)lane * 16 + component * 4));
            var count = lane < 192 ? 64u : 60u;
            Assert.Equal(count | (count << 8), Read(0));
            Assert.Equal(7 + lane / 4, Read(1));
            Assert.Equal((lane / 4) | ((lane % 4) << 8), Read(2));
            var sourceIndex = 7 * 4 + lane;
            Assert.Equal((indexSize != 0 && lane < 252 ? Index(sourceIndex) : sourceIndex) + 11, Read(3));
        }
        harness.AssertNoValidationMessages();
    }
}

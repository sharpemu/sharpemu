// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A buffer's stride reaches the shader as data, so the same compiled program indexes
// correctly with every stride a title binds, and the stride is not part of the key.
public sealed class RuntimeBufferStrideTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong BufferAddress = 0x2_0000_0000;
    private const int BufferBytes = 0x1000;
    private const int ResultOffset = 0x800;
    private const uint Index = 3;

    // v6 = s8; v4 = buffer[s0:3] at v6 * stride; buffer[0x800] = v4.
    private static Gen5ShaderProgram IndexedLoadProgram() => Program(
        MoveVectorFromScalar(0, 6, 8),
        BufferAccess(4, "BufferLoadDword", 0, dwords: 1, vectorData: 4, indexEnabled: true, vectorAddress: 6),
        BufferAccess(12, "BufferStoreDword", 0, offset: ResultOffset, dwords: 1, vectorData: 4),
        EndProgram(20));

    private static uint[] UserData(uint stride)
    {
        var registers = new uint[20];
        registers[0] = unchecked((uint)BufferAddress);
        registers[1] = (uint)((BufferAddress >> 32) & 0xFFFF) | (stride << 16);
        registers[2] = stride == 0 ? BufferBytes : BufferBytes / stride;
        registers[3] = DescriptorConstants.IdentityDestinationSelect | (20u << 12);
        registers[8] = Index;
        return registers;
    }

    private static ResourceSpecialization Specialize(ShaderResourcePlan plan, uint stride)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        static bool NoMemory(ulong address, out uint value) { value = 0; return false; }
        var inputs = ResourceTestProgram.Inputs(UserData(stride), NoMemory, NoMemory);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.Equal(stride, Assert.Single(specialization.Buffers).PackedStride & BufferSpecialization.StrideMask);
        for (var index = 0; index < specialization.Buffers.Count; index++)
        {
            specialization.Buffers[index] = specialization.Buffers[index].WithoutRuntimeStride();
        }

        return specialization;
    }

    [Fact]
    public void OneProgram_IndexesWithEveryBoundStride()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        var plan = ShaderResourcePlan.Extract(IndexedLoadProgram(), ShaderStage.Compute, 1, 0, 20);
        var specialization = Specialize(plan, 8);
        // Another stride is the same permutation.
        Assert.Equal(specialization, Specialize(plan, 20));

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 20), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false,
            usesRuntimeBufferStrides: true);
        Assert.True(layout.UsesRuntimeBufferStrides);
        var request = new ShaderCompileRequest(plan, resources, layout) { ThreadCountX = 1, ThreadCountY = 1, ThreadCountZ = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        foreach (var stride in new uint[] { 8, 20 })
        {
            var initial = new byte[BufferBytes];
            for (var word = 0; word < ResultOffset / 4; word++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(word * 4), 0xA000_0000u + (uint)word);
            }

            var records = runner.CreateBuffer(initial);
            var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [records] };
            harness.Run(() => runner.Dispatch(UserData(stride), bindings, 1, bufferStrides: [stride]));
            var result = harness.ReadBack(records.Handle, 0, BufferBytes);
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(initial.AsSpan((int)(Index * stride)));
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(ResultOffset)));
            output.WriteLine($"stride={stride} loaded 0x{expected:X8} on {vulkan.DeviceName}");
        }

        harness.AssertNoValidationMessages();
        vulkan.AssertNoValidationMessages();
    }
}

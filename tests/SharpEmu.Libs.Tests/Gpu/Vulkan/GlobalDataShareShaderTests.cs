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

// DS operations with the GDS bit run over the shared global data share buffer.
public sealed class GlobalDataShareShaderTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ResultRegister = 4;
    private const uint ResultBytes = 256;
    private const uint M0 = 124;

    [Fact]
    public void AtomicAddFromEveryLane_ThenAReadFromASecondDispatch_SeesTheSum()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        // Dispatch 1: every lane adds 5 to word 1; dispatch 2 reads word 1 back.
        var add = Program(
            MoveVector(0, 3, 4),
            MoveVector(8, 5, 5),
            DataShare(16, "DsAddU32", gds: true, [Gen5Operand.Vector(3), Gen5Operand.Vector(5)], []),
            EndProgram(24));
        var read = Program(
            MoveVector(0, 3, 4),
            DataShare(8, "DsReadB32", gds: true, [Gen5Operand.Vector(3)], [1]),
            BufferAccess(16, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 1),
            EndProgram(24));

        using var harness = new ImageTestHarness(vulkan);
        var addRequest = RequestFor(add, threadCount: 32);
        var readRequest = RequestFor(read, threadCount: 1);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(addRequest, out var addShader, out var addError), addError);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(readRequest, out var readShader, out var readError), readError);
        using var adder = new LayoutComputeRunner(harness, addRequest, addShader.Spirv);
        using var reader = new LayoutComputeRunner(harness, readRequest, readShader.Spirv);
        var share = adder.CreateBuffer(64);
        var result = reader.CreateBuffer(ResultBytes);
        var registers = new uint[256];
        registers[ResultRegister + 2] = ResultBytes;
        harness.Run(() => adder.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.GlobalDataShare] = [share] }, 1));
        harness.Run(() => reader.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.GlobalDataShare] = [share],
            [DescriptorBindingKind.Buffers] = [result],
        }, 1));

        Assert.Equal(160u, ReadWord(reader.ReadBack(share, 0, 64), 4));
        Assert.Equal(160u, ReadWord(reader.ReadBack(result, 0, ResultBytes), 0));
        harness.AssertNoValidationMessages();
        output.WriteLine($"Verified GDS atomics and reads on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    // M0 carries the base in its high half and any nonzero size in its low half; the buffer bounds the word.
    [Theory]
    [InlineData(64u)]
    [InlineData(1u)]
    public void AppendOverTheGlobalDataShare_CountsTheActiveLanesOnce(uint m0Size)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        // M0 = base 0; every lane receives the old counter, which grows by the lane count.
        var program = Program(
            MoveScalar(0, M0, m0Size),
            DataShare(8, "DsAppend", gds: true, [Gen5Operand.Scalar(M0)], [1]),
            Vop2(16, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(20, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 1, offsetEnabled: true, vectorAddress: 5),
            EndProgram(28));

        using var harness = new ImageTestHarness(vulkan);
        var request = RequestFor(program, threadCount: 32);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var share = runner.CreateBuffer(new byte[64]);
        var result = runner.CreateBuffer(ResultBytes);
        var registers = new uint[256];
        registers[ResultRegister + 2] = ResultBytes;
        var bound = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.GlobalDataShare] = [share],
            [DescriptorBindingKind.Buffers] = [result],
        };
        harness.Run(() => runner.Dispatch(registers, bound, 1));
        harness.Run(() => runner.Dispatch(registers, bound, 1));

        Assert.Equal(64u, ReadWord(runner.ReadBack(share, 0, 64), 0));
        var results = runner.ReadBack(result, 0, ResultBytes);
        for (var lane = 0; lane < 32; lane++)
        {
            Assert.Equal(32u, ReadWord(results, lane * 4));
        }

        harness.AssertNoValidationMessages();
        output.WriteLine($"Verified GDS append on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    private static ShaderCompileRequest RequestFor(Gen5ShaderProgram program, uint threadCount)
    {
        var (plan, resources, layout) = Prepare(program);
        Assert.NotNull(layout.Find(DescriptorBindingKind.GlobalDataShare));
        return new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = threadCount, ThreadCountX = threadCount };
    }

    [Theory]
    [InlineData(0u, 32u)]
    [InlineData(32u, 32u)]
    [InlineData(0u, 64u)]
    [InlineData(32u, 64u)]
    public void RuntimeThreadLimitsMaskEachDispatchWithOnePipeline(uint pushCursor, uint waveSize)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            MoveVector(0, 3, 0),
            MoveVector(8, 5, 1),
            DataShare(16, "DsAddU32", gds: true, [Gen5Operand.Vector(3), Gen5Operand.Vector(5)], []),
            EndProgram(24));
        var (plan, resources, _) = Prepare(program);
        var layout = BindingLayout.Allocate(resources.Info, [], true, false, false, pushCursor, usesDispatchThreadLimits: true);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, LocalSizeY = 2, LocalSizeZ = 2, WaveSize = waveSize,
        };
        Assert.Equal(pushCursor == 0, layout.UsesPushData);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var counter = runner.CreateBuffer(new byte[4]);
        var buffers = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.GlobalDataShare] = [counter] };
        uint[][] limits = [[0, 3, 3], [1, 1, 1], [63, 2, 2], [64, 2, 2], [65, 3, 3], [100, 3, 3]];
        foreach (var limit in limits)
        {
            harness.Run(() => runner.Dispatch(new uint[256], buffers, 2, 2, 2, dispatchThreadLimits: limit));
        }

        Assert.Equal(limits.Sum(limit => (long)limit[0] * limit[1] * limit[2]),
            (long)ReadWord(runner.ReadBack(counter, 0, 4), 0));
        harness.AssertNoValidationMessages();
    }

    private static uint ReadWord(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
}

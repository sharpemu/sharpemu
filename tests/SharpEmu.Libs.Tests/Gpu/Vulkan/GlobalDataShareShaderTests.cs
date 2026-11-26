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

    // GDS addresses are relative to M0's window: M0[31:16] is the base and M0[15:0] the size in bytes, so word 1 of a
    // window at base 0x20 is word 9 of the buffer, and an address past the size reaches nothing.
    [Theory]
    [InlineData(0u, 1)]
    [InlineData(0x20u, 9)]
    public void AtomicAddFromEveryLane_ThenAReadFromASecondDispatch_SeesTheSum(uint windowBase, int sumWord)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var m0 = (windowBase << 16) | 0x10u;
        // Dispatch 1: every lane adds 5 to window word 1 and 7 to window byte 0x10, past the size; dispatch 2 reads word 1.
        var add = Program(
            MoveScalar(0, M0, m0),
            MoveVector(8, 3, 4),
            MoveVector(16, 5, 5),
            DataShare(24, "DsAddU32", gds: true, [Gen5Operand.Vector(3), Gen5Operand.Vector(5)], []),
            MoveVector(32, 3, 0x10),
            MoveVector(40, 5, 7),
            DataShare(48, "DsAddU32", gds: true, [Gen5Operand.Vector(3), Gen5Operand.Vector(5)], []),
            EndProgram(56));
        var read = Program(
            MoveScalar(0, M0, m0),
            MoveVector(8, 3, 4),
            DataShare(16, "DsReadB32", gds: true, [Gen5Operand.Vector(3)], [1]),
            BufferAccess(24, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 1),
            EndProgram(32));

        using var harness = new ImageTestHarness(vulkan);
        var addRequest = RequestFor(add, threadCount: 32);
        var readRequest = RequestFor(read, threadCount: 1);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(addRequest, out var addShader, out var addError), addError);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(readRequest, out var readShader, out var readError), readError);
        using var adder = new LayoutComputeRunner(harness, addRequest, addShader.Spirv);
        using var reader = new LayoutComputeRunner(harness, readRequest, readShader.Spirv);
        var share = adder.CreateBuffer(128);
        var result = reader.CreateBuffer(ResultBytes);
        var registers = new uint[256];
        registers[ResultRegister + 2] = ResultBytes;
        harness.Run(() => adder.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.GlobalDataShare] = [share] }, 1));
        harness.Run(() => reader.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.GlobalDataShare] = [share],
            [DescriptorBindingKind.Buffers] = [result],
        }, 1));

        var words = reader.ReadBack(share, 0, 128);
        for (var word = 0; word < 32; word++)
        {
            Assert.Equal(word == sumWord ? 160u : 0u, ReadWord(words, word * 4));
        }

        Assert.Equal(160u, ReadWord(reader.ReadBack(result, 0, ResultBytes), 0));
        harness.AssertNoValidationMessages();
        output.WriteLine($"Verified GDS atomics and reads at window base 0x{windowBase:X} on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    // Each dword of a 64-bit GDS access checks M0's window on its own: a pair straddling the size keeps its first dword
    // only, and a pair past the size writes nothing (neither half may wrap around to word 0 of the buffer).
    [Fact]
    public void Write64OverTheWindowEdge_KeepsOnlyTheDwordsInsideTheWindow()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        // Window base 0x20, size 8: byte 4 is buffer word 9, byte 8 is already outside.
        const uint m0 = (0x20u << 16) | 0x8u;
        var program = Program(
            MoveScalar(0, M0, m0),
            MoveVector(8, 5, 0x11),
            MoveVector(16, 6, 0x22),
            MoveVector(24, 3, 4),
            DataShare(32, "DsWriteB64", gds: true, [Gen5Operand.Vector(3), Gen5Operand.Vector(5), Gen5Operand.Vector(6)], []),
            MoveVector(40, 3, 0x10),
            DataShare(48, "DsWriteB64", gds: true, [Gen5Operand.Vector(3), Gen5Operand.Vector(5), Gen5Operand.Vector(6)], []),
            EndProgram(56));

        using var harness = new ImageTestHarness(vulkan);
        var request = RequestFor(program, threadCount: 1);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var share = runner.CreateBuffer(new byte[128]);
        harness.Run(() => runner.Dispatch(new uint[256], new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.GlobalDataShare] = [share] }, 1));

        var words = runner.ReadBack(share, 0, 128);
        for (var word = 0; word < 32; word++)
        {
            Assert.Equal(word == 9 ? 0x11u : 0u, ReadWord(words, word * 4));
        }

        harness.AssertNoValidationMessages();
        output.WriteLine($"Verified 64-bit GDS writes against M0's window on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
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

    // DS_ORDERED_COUNT adds the count the first active lane holds (not the lane count) to the dword offset0[7:2] picks and
    // hands every lane the old value. M0's size carries the wave's launch order here, so a zero size must not turn it off,
    // and its low two bits are the packer id only for a pixel wave (offset1 bit 2). A wave64 on a 32-lane host pairs its
    // halves in one invocation.
    [Theory]
    [InlineData(0u, 3u, 1)]
    [InlineData(6u, 3u, 1)]
    [InlineData(0u, 7u, 1)]
    [InlineData(6u, 7u, 3)]
    [InlineData(6u, 3u, 1, true)]
    public void OrderedCountOverTheGlobalDataShare_AddsTheFirstLanesCountAndReturnsTheOldValue(uint m0, uint offset1, int counterWord, bool pairedWave64 = false)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        // v1 = lane + 10: the first lane's count (10) differs from the lane count (32) and from the sum over the lanes.
        var program = Program(
            MoveScalar(0, M0, m0),
            Vop2(8, "VAddI32", 1, Operand(10), Gen5Operand.Vector(0)),
            DataShare(12, "DsOrderedCount", gds: true, [Gen5Operand.Scalar(M0), Gen5Operand.Vector(1)], [2], offset0: 4, offset1: offset1),
            Vop2(20, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(24, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 2, offsetEnabled: true, vectorAddress: 5),
            EndProgram(32));

        using var harness = new ImageTestHarness(vulkan);
        var lanes = pairedWave64 ? 64 : 32;
        var request = RequestFor(program, threadCount: (uint)lanes, pairedWave64);
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

        var counters = runner.ReadBack(share, 0, 64);
        for (var word = 0; word < 16; word++)
        {
            Assert.Equal(word == counterWord ? 20u : 0u, ReadWord(counters, word * 4));
        }

        var results = runner.ReadBack(result, 0, ResultBytes);
        for (var lane = 0; lane < lanes; lane++)
        {
            Assert.Equal(10u, ReadWord(results, lane * 4));
        }

        harness.AssertNoValidationMessages();
        output.WriteLine($"Verified GDS ordered count on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    private static ShaderCompileRequest RequestFor(Gen5ShaderProgram program, uint threadCount, bool pairedWave64 = false)
    {
        var (plan, resources, layout) = Prepare(program);
        Assert.NotNull(layout.Find(DescriptorBindingKind.GlobalDataShare));
        return pairedWave64
            ? new ShaderCompileRequest(plan, resources, layout)
            {
                LocalSizeX = threadCount, ThreadCountX = threadCount, WaveSize = 64, HostComputeSubgroup32 = true, HostWave64Supported = false,
            }
            : new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = threadCount, ThreadCountX = threadCount };
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
        var program = WithWholeGdsWindow(Program(
            MoveVector(0, 3, 0),
            MoveVector(8, 5, 1),
            DataShare(16, "DsAddU32", gds: true, [Gen5Operand.Vector(3), Gen5Operand.Vector(5)], []),
            EndProgram(24)));
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

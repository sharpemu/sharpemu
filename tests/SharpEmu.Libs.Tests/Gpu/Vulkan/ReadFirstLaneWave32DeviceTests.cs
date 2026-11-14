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

// V_READFIRSTLANE_B32 reads the first lane EXEC enables, and lane 0 when EXEC is empty (RDNA2 ISA 12.8).
public sealed class ReadFirstLaneWave32DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ThreadCount = 32;
    private const uint Exec = 126;

    [Theory]
    [InlineData(0u, 100u)]
    [InlineData(7u, 107u)]
    [InlineData(31u, 131u)]
    [InlineData(32u, 100u)]
    public void ReadsTheFirstActiveLaneAndLaneZeroWhenNoneIsActive(uint firstActiveLane, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = pc });
            pc += 8;
        }

        Add(Vop2(0, "VAddU32", 3, Operand(100), Gen5Operand.Vector(0)));
        Add(Vopc(0, "VCmpxLeU32", Operand(firstActiveLane), 0));
        Add(ReadFirstLane(0, 20, 3));
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Source(193)));
        Add(MoveVectorFromScalar(0, 4, 20));
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = ThreadCount, ThreadCountX = ThreadCount, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ThreadCount * sizeof(uint));
        var registers = new uint[256];
        registers[6] = ThreadCount * sizeof(uint);
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ThreadCount * sizeof(uint));
        for (var lane = 0; lane < ThreadCount; lane++)
        {
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }
}

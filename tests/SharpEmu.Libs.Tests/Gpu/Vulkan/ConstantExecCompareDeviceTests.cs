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

// V_CMPX_F_{I32,U32} writes an empty execution mask and V_CMPX_T_{I32,U32} leaves the mask as it was. The 64-bit forms and the plain compares already worked.
public sealed class ConstantExecCompareDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ThreadCount = 32;

    [Theory]
    [InlineData("VCmpxFI32", false)]
    [InlineData("VCmpxFU32", false)]
    [InlineData("VCmpxTI32", true)]
    [InlineData("VCmpxTU32", true)]
    public void WritesTheExecutionMaskFromTheConstantResult(string opcode, bool lanesKeepRunning)
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

        Add(MoveVector(0, 4, 1));
        Add(Vopc(0, opcode, Operand(0), 0));
        Add(MoveVector(0, 4, 2)); // runs only in the lanes the compare left enabled
        Add(MoveScalar(0, 126, uint.MaxValue));
        Add(MoveScalar(0, 127, uint.MaxValue));
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
            Assert.Equal(lanesKeepRunning ? 2u : 1u,
                BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }
}

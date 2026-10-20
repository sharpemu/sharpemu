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

// Only the even lanes are enabled, so every enabled lane's DPP partner (the lane one
// above it) is inactive. ISA: bound_ctrl clear keeps the old destination, bound_ctrl
// set writes zero; reading the partner's register would give lane + 2.
public sealed class WaveShuffleInactivePartnerDeviceTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(false, 1000u)]
    [InlineData(true, 0u)]
    public void Dpp_InactivePartner_UsesTheIsaValue(bool boundControl, uint expected)
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

        Add(Vop2(0, "VLshlrevB32", 2, Operand(3), Gen5Operand.Vector(1)));
        Add(Vop2(0, "VAddI32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddI32", 3, Operand(1), Gen5Operand.Vector(2)));
        Add(MoveVector(0, 4, 1000));
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(2)));
        Add(MoveScalar(0, 126, 0x5555_5555));
        Add(MoveScalar(0, 127, 0x5555_5555));
        Add(Vop1(0, "VMovB32", 4, Gen5Operand.Vector(3)) with
        {
            Control = new Gen5DppControl(0x101, false, boundControl, 0, 0, 15, 15),
        });
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, ThreadCountX = 8, ThreadCountY = 8, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 256);
        for (uint lane = 0; lane < 64; lane += 2)
        {
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan((int)lane * 4)));
        }

        harness.AssertNoValidationMessages();
    }
}

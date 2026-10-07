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

public sealed class Float16ArithmeticDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{

    [Theory]
    [InlineData(0x4000u, 0xBC00u, 0x3C00u, 0xBC00u)]
    [InlineData(0x8000u, 0x0000u, 0x3C00u, 0x8000u)]
    [InlineData(0x0000u, 0x8000u, 0x0000u, 0x8000u)]
    [InlineData(0x7E00u, 0x4000u, 0x3C00u, 0x3C00u)]
    [InlineData(0x4000u, 0x7E00u, 0x3C00u, 0x3C00u)]
    [InlineData(0x4000u, 0x3C00u, 0x7E00u, 0x3C00u)]
    [InlineData(0x0002u, 0x0001u, 0x0400u, 0x0001u)]
    [InlineData(0x7C00u, 0xFC00u, 0x3C00u, 0xFC00u)]
    public void MinimumThreeUsesSelectedSourceHalves(uint first, uint second, uint third, uint expected)
        => CheckResult("VMin3F16", first, expected, second, third);


    [Theory]
    [InlineData(0x4000u, 0x4200u, 0x3C00u, 0x4700u)]
    [InlineData(0x3C01u, 0x3BFEu, 0xBC00u, 0x8010u)]
    [InlineData(0x0001u, 0x3C00u, 0x0001u, 0x0002u)]
    public void FusedAddLiteralRoundsOnlyAfterAddition(uint first, uint second, uint literal, uint expected)
        => CheckResult("VFmaAkF16", first, expected, second, literal);

    [Theory]
    [InlineData(false, 0x7C01u, 0x3C00u, 0x3C00u)]
    [InlineData(true, 0x7C01u, 0x3C00u, 0x7E01u)]
    [InlineData(true, 0x3C00u, 0xFC15u, 0xFE15u)]
    [InlineData(true, 0x7C01u, 0xFC15u, 0x7E01u)]
    [InlineData(true, 0x7E11u, 0xFC15u, 0xFE15u)]
    [InlineData(true, 0x7E11u, 0x7E22u, 0x7E22u)]
    [InlineData(true, 0x7E11u, 0x3C00u, 0x3C00u)]
    [InlineData(true, 0x0000u, 0x8000u, 0x8000u)]
    [InlineData(true, 0x8000u, 0x0000u, 0x8000u)]
    public void MinimumHonorsIeeeModeAndNanPriority(bool ieeeMode, uint first, uint second, uint expected)
        => CheckResult("VMinF16", first, expected, second, ieeeMode: ieeeMode);

    [Theory]
    [InlineData(0x7C01u, 0x3C00u, 0x4000u, 0x4000u)]
    [InlineData(0x7C01u, 0x3C00u, 0xFC15u, 0xFE15u)]
    [InlineData(0x3C00u, 0x4000u, 0x7C19u, 0x7E19u)]
    public void MinimumThreeAppliesIeeeRulesAtEachStep(uint first, uint second, uint third, uint expected)
        => CheckResult("VMin3F16", first, expected, second, third, ieeeMode: true);

    private void CheckResult(string opcode, uint input, uint expected, uint second = 0, uint third = 0,
        bool ieeeMode = false)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var (plan, resources, layout) = Prepare(Program([
            MoveVector(0, 1, opcode == "VMin3F16" ? (input << 16) | 0xFC00u : input),
            MoveVector(4, 2, 0xA5A50000),
            MoveVector(8, 3, second | 0xFC000000u),
            MoveVector(12, 4, (third << 16) | 0xFC00u),
            opcode == "VMin3F16"
                ? Vop1(16, opcode, 2, Gen5Operand.Vector(1)) with
                {
                    Sources = [Gen5Operand.Vector(1), Gen5Operand.Vector(3), Gen5Operand.Vector(4)],
                    Control = new Gen5Vop3Control(0, 0, 0, false, 5, null),
                }
                : opcode == "VFmaAkF16"
                    ? Vop1(16, opcode, 2, Gen5Operand.Vector(1)) with
                    {
                        Sources = [Gen5Operand.Vector(1), Gen5Operand.Vector(3),
                            new Gen5Operand(Gen5OperandKind.LiteralConstant, third | 0xDEAD0000u)],
                    }
                    : opcode == "VMinF16"
                        ? Vop2(16, opcode, 2, Gen5Operand.Vector(1), Gen5Operand.Vector(3))
                        : Vop1(16, opcode, 2, Gen5Operand.Vector(1)),
            BufferAccess(24, "BufferStoreDword", 4, 0, 1, 2),
            EndProgram(32),
        ]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1, ThreadCountX = 1, WaveSize = 32,
            IeeeMode = ieeeMode,
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

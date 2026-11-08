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

// 16-bit VOP3 operations on the device: the halves op_sel selects, the f16 input modifiers and the clamp bit.
public sealed class SixteenBitVop3DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Previous = 0xDEADBEEF;

    private sealed record Case(string Label, string Opcode, Gen5Vop3Control Control, uint A, uint B, uint C, uint Expected);

    // abs clears the sign bit of the selected half and neg flips it; the halves are not converted.
    [Fact]
    public void PackB32F16AppliesTheInputModifiers() =>
        Check(
            new Case("neg src0", "VPackB32F16", new Gen5Vop3Control(0, 0b01, 0, false, 0, null), 0x3C00u, 0x4000u, 0u, 0x4000_BC00u),
            new Case("abs src1", "VPackB32F16", new Gen5Vop3Control(0b10, 0, 0, false, 0, null), 0x3C00u, 0xC000u, 0u, 0x4000_3C00u),
            new Case("no modifiers", "VPackB32F16", new Gen5Vop3Control(0, 0, 0, false, 0, null), 0xBC00u, 0xC000u, 0u, 0xC000_BC00u));

    // V_MAD_U32_U16 is in the op_sel table: (lo 5, hi 3) and (lo 2, hi 7) with op_sel on both sources give 3 * 7 + 100.
    [Fact]
    public void MadU32U16ReadsTheHalvesOpSelPicks() =>
        Check(
            new Case("high halves", "VMadU32U16", new Gen5Vop3Control(0, 0, 0, false, 0b011, null), 0x0003_0005u, 0x0007_0002u, 100u, 121u),
            new Case("low halves", "VMadU32U16", new Gen5Vop3Control(0, 0, 0, false, 0, null), 0x0003_0005u, 0x0007_0002u, 100u, 110u));

    // The clamp bit saturates V_ADD_NC_U16 in the unsigned 16-bit domain; the high half stays.
    [Fact]
    public void AddNcU16SaturatesWithTheClampBit() =>
        Check(
            new Case("clamp", "VAddNcU16", new Gen5Vop3Control(0, 0, 0, true, 0, null), 0xFFF0u, 0x0020u, 0u, 0xDEAD_FFFFu),
            new Case("no clamp", "VAddNcU16", new Gen5Vop3Control(0, 0, 0, false, 0, null), 0xFFF0u, 0x0020u, 0u, 0xDEAD_0010u));

    private void Check(params Case[] cases)
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

        for (var index = 0; index < cases.Length; index++)
        {
            var current = cases[index];
            Add(MoveVector(0, 40, current.A));
            Add(MoveVector(0, 41, current.B));
            Add(MoveVector(0, 42, current.C));
            Add(MoveVector(0, 43, Previous));
            Add(Vop3(0, current.Opcode, 43, Gen5Operand.Vector(40), Gen5Operand.Vector(41), Gen5Operand.Vector(42)) with { Control = current.Control });
            Add(BufferAccess(0, "BufferStoreDword", 4, offset: index * 4, vectorData: 43));
        }

        Add(EndProgram(0));
        var size = (uint)(cases.Length * sizeof(uint));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(size);
        var registers = new uint[256];
        registers[6] = size;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, size);
        var failures = new List<string>();
        for (var index = 0; index < cases.Length; index++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * sizeof(uint)));
            if (value != cases[index].Expected)
                failures.Add($"{cases[index].Label}: expected 0x{cases[index].Expected:X8}, got 0x{value:X8}");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
        harness.AssertNoValidationMessages();
    }
}

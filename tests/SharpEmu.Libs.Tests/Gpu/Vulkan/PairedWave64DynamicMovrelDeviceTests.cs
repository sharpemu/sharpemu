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

// A V_MOVRELS whose M0 the static analysis cannot bound turns the VGPR file into one dynamic array.
// Under the paired lowering both lane halves of a host thread must still own a separate bank:
// guest lane L and lane L+32 run in the same invocation and must not see each other's registers.
public sealed class PairedWave64DynamicMovrelDeviceTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    private const uint SlotBytes = 256;
    private const uint Lanes = 64;
    private const uint Total = 3 * SlotBytes;

    [Fact]
    public void UnboundedMovrels_KeepsTheLowerAndUpperLaneHalvesApart()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var request = Compile();
        if (!Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var message))
        {
            Assert.Fail($"compile failed: {message}");
            return;
        }

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var input = new byte[Total];
        // Slot 0 holds M0 (the register distance from v18 to v20) for every lane.
        for (var lane = 0; lane < Lanes; lane++)
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(lane * 4), 2);
        var buffer = runner.CreateBuffer(input, Total);
        var registers = new uint[256];
        registers[6] = Total;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
        var bytes = runner.ReadBack(buffer, 0, Total);

        var wrong = new List<string>();
        for (uint lane = 0; lane < Lanes; lane++)
        {
            var relative = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)(SlotBytes + lane * 4)));
            var own = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)(2 * SlotBytes + lane * 4)));
            if (relative != 1000 + lane || own != 1000 + lane)
                wrong.Add($"lane {lane}: movrels={relative} v20={own} expected {1000 + lane}");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} lane(s) read another lane half's register:\n" + string.Join("\n", wrong.Take(12)));
    }

    private static ShaderCompileRequest Compile()
    {
        var list = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            list.Add(instruction with { Pc = pc });
            pc += 8;
        }

        const uint lane = 10, laneBytes = 11, m0Source = 12, zero = 13, own = 20, relative = 30, outAddress = 40;
        Add(Sop1(0, "SMovB64", 126, Gen5Operand.Source(193)));
        // v10 = lane (v0 + 8 * v1), v11 = lane * 4.
        Add(Vop2(0, "VLshlrevB32", lane, Operand(3), Gen5Operand.Vector(1)));
        Add(Vop2(0, "VAddU32", lane, Gen5Operand.Vector(0), Gen5Operand.Vector(lane)));
        Add(Vop2(0, "VLshlrevB32", laneBytes, Operand(2), Gen5Operand.Vector(lane)));
        // M0 comes from the buffer, so no static analysis can bound it.
        Add(MoveVector(0, zero, 0));
        Add(BufferAccess(0, "BufferLoadDword", 4, vectorData: m0Source, offsetEnabled: true, vectorAddress: zero));
        Add(ReadFirstLane(0, 124, m0Source));
        // v20 = lane + 1000, then v30 = vgpr[v18 + M0] = v20.
        Add(Vop2(0, "VAddU32", own, Operand(1000), Gen5Operand.Vector(lane)));
        Add(Vop1(0, "VMovrelsB32", relative, Gen5Operand.Vector(18)));
        Add(Vop2(0, "VAddU32", outAddress, Operand(SlotBytes), Gen5Operand.Vector(laneBytes)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: relative, offsetEnabled: true, vectorAddress: outAddress));
        Add(Vop2(0, "VAddU32", outAddress, Operand(2 * SlotBytes), Gen5Operand.Vector(laneBytes)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: own, offsetEnabled: true, vectorAddress: outAddress));
        Add(EndProgram(0));

        var (plan, resources, layout) = Prepare(Program([.. list]));
        return new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, ThreadCountX = 8, ThreadCountY = 8, WaveSize = 64,
            HostComputeSubgroup32 = true,
            EnableExecGuardElision = false,
        };
    }
}

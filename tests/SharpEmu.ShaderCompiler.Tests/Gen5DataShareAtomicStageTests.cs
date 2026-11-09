// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataShareAtomicStageTests
{
    public static TheoryData<string, int, bool> Atomics => new()
    {
        { "DsAddU32", 2, false },
        { "DsSubU32", 2, false },
        { "DsIncU32", 2, false },
        { "DsDecU32", 2, false },
        { "DsMinI32", 2, false },
        { "DsMaxI32", 2, false },
        { "DsMinU32", 2, false },
        { "DsMaxU32", 2, false },
        { "DsAndB32", 2, false },
        { "DsOrB32", 2, false },
        { "DsXorB32", 2, false },
        { "DsAddRtnU32", 2, true },
        { "DsOrRtnB32", 2, true },
        { "DsWrxchgRtnB32", 2, true },
        { "DsCmpstB32", 3, false },
        { "DsCmpstRtnB32", 3, true },
        { "DsMskorB32", 3, false },
        { "DsMinF32", 2, false },
        { "DsMaxF32", 2, false },
        { "DsAddU64", 3, false },
        { "DsOrB64", 3, false },
    };

    [Theory]
    [MemberData(nameof(Atomics))]
    public void DataShareAtomicsAreNotEmittedOnTheLdsOfAGraphicsStage(
        string opcode,
        int sourceCount,
        bool returnsValue)
    {
        var sources = Enumerable
            .Range(0, sourceCount)
            .Select(index => Gen5Operand.Vector((uint)index))
            .ToArray();
        var program = Program(
            DataShare(0, opcode, gds: false, sources, returnsValue ? [4u] : []),
            EndProgram(8));

        // A vertex or pixel stage keeps the LDS in a per-invocation Private array, where an OpAtomic
        // instruction is invalid SPIR-V (and can crash the driver's pipeline compiler).
        Assert.DoesNotContain(CompiledOpcodes(program, ShaderStage.Pixel), IsAtomic);
        // A compute workgroup and a mesh thread group share their LDS, so they keep the atomics.
        Assert.Contains(CompiledOpcodes(program, ShaderStage.Compute), IsAtomic);
        Assert.Contains(CompiledMeshOpcodes(program), IsAtomic);
    }

    // The GDS is shared by every wave of every stage, so its float min and max stay atomic in a pixel shader.
    [Theory]
    [InlineData("DsMinF32")]
    [InlineData("DsMaxF32")]
    public void GlobalDataShareFloatMinMaxStaysAtomicInAPixelStage(string opcode)
    {
        var program = Program(
            DataShare(0, opcode, gds: true, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], []),
            EndProgram(8));

        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, CompiledOpcodes(program, ShaderStage.Pixel));
    }

    private static bool IsAtomic(ushort opcode) =>
        opcode is >= (ushort)SpirvOp.AtomicLoad and <= (ushort)SpirvOp.AtomicXor;

    private static HashSet<ushort> CompiledOpcodes(Gen5ShaderProgram program, ShaderStage stage) =>
        CompiledOpcodes(Request(program, stage));

    // A mesh thread group of 64 lanes with 16 dwords of LDS.
    private static HashSet<ushort> CompiledMeshOpcodes(Gen5ShaderProgram program)
    {
        var (plan, resources, layout) = Prepare(program, ShaderStage.Mesh);
        return CompiledOpcodes(new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 64,
            Mesh = new MeshShaderConfiguration(64, 64, 0, 21, 63, 16, false, 32),
        });
    }

    private static HashSet<ushort> CompiledOpcodes(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return new SpirvModuleInspector(shader.Spirv).Opcodes;
    }
}

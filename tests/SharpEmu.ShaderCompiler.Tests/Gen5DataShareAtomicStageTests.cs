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
        { "DsMinF32", 3, false },
        { "DsMaxF32", 3, false },
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

        // A graphics stage keeps the LDS in a per-invocation Private array, where an OpAtomic
        // instruction is invalid SPIR-V (and can crash the driver's pipeline compiler).
        Assert.DoesNotContain(CompiledOpcodes(program, ShaderStage.Pixel), IsAtomic);
        // The LDS of a compute shader is shared, so it keeps its atomics.
        Assert.Contains(CompiledOpcodes(program, ShaderStage.Compute), IsAtomic);
    }

    private static bool IsAtomic(ushort opcode) =>
        opcode is >= (ushort)SpirvOp.AtomicLoad and <= (ushort)SpirvOp.AtomicXor;

    private static HashSet<ushort> CompiledOpcodes(Gen5ShaderProgram program, ShaderStage stage)
    {
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(Request(program, stage), out var shader, out var error),
            error);
        return new SpirvModuleInspector(shader.Spirv).Opcodes;
    }
}

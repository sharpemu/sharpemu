// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Lowering of a bounded runtime V# table: the emitted module must bind one native candidate
// per descriptor and dispatch the normal buffer operation through a flattened key mapping.
public sealed class BufferCandidateTableSpirvTests
{
    private static Gen5ShaderProgram CandidateProgram(uint guardRegister = 10)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction);
            pc += (uint)instruction.Words.Count * sizeof(uint);
        }

        Add(MoveScalar(pc, 0, 0x3000));
        Add(MoveScalar(pc, 1, 16u << 16));
        Add(MoveScalar(pc, 2, 4));
        Add(MoveScalar(pc, 3, 0));
        Add(MoveScalar(pc, 10, 0));
        Add(Branch(pc, "SBranch", 0));
        var headerPc = pc;

        Add(Sop2(pc, "SMulI32", 12, Gen5Operand.Scalar(10), Operand(16)));
        Add(ScalarBufferLoad(pc, 0, destination: 16, count: 4, dynamicOffsetRegister: 12));
        Add(BufferLoad(pc, 16, formatted: true));
        Add(Sop2(pc, "SAddU32", 10, Gen5Operand.Scalar(10), Operand(1)));
        Add(Sopc(pc, "SCmpLtU32", Gen5Operand.Scalar(guardRegister), Operand(4)));
        var branchPc = pc;
        Add(Branch(branchPc, "SCbranchScc1", (short)(((int)headerPc - (int)(branchPc + 4)) / 4)));
        Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    private static TestWordMemory CandidateMemory()
    {
        var memory = new TestWordMemory { Base = 0x3000, Words = new uint[0x2000 / 4], RequireAlignment = true };
        for (var index = 0; index < 4; index++)
        {
            var address = 0x3000 + (ulong)index * 16;
            memory.At(address + 0) = 0x4000u + (uint)index * 0x100;
            memory.At(address + 4) = 16u << 16;
            memory.At(address + 8) = 4;
            memory.At(address + 12) = 77u << 12;
        }

        return memory;
    }

    private static ShaderCompileRequest CompileRequest(ShaderResourcePlan plan, SpecializedResourceInfo resources)
    {
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, plan.UserDataBase, plan.UserDataCount),
            BindingLayout.UsesGlobalDataShare(plan.Graph.Program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(plan.Graph.Program));
        return new ShaderCompileRequest(plan, resources, layout);
    }

    [Fact]
    public void BoundedCandidateTable_CompilesToValidSpirv()
    {
        var plan = ShaderResourcePlan.Extract(CandidateProgram(), ShaderStage.Compute, Hash, 0, 64);
        Assert.Single(plan.BufferCandidateTables);

        var memory = CandidateMemory();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([], readCleanMemory: memory.Read), ref snapshot, ref specialization));

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var request = CompileRequest(plan, resources);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Contains((0u, BindingLayout.NativeBindingIndex(ShaderStage.Compute, DescriptorBindingKind.FlattenedResourceTable)), module.DescriptorBindings);
    }
}

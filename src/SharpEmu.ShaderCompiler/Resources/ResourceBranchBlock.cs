// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler.Resources;

public sealed record ResourceBranchBlock(ScalarValue? Condition, int[] Successors, uint[] Sources)
{
    // SHARPEMU_BRANCHES_WITH_WRITES (on by default; =0 turns it off): a shader that writes memory keeps its blocks too
    // when every write goes through a static buffer descriptor or to LDS or scratch; each dispatch checks those
    // buffers' records against the words its predicates read (ResourceBranchWriteGuard). Tests set the override.
    private static readonly bool WritesConfigured = Environment.GetEnvironmentVariable("SHARPEMU_BRANCHES_WITH_WRITES") != "0";
    [ThreadStatic] internal static bool? WritesOverride;
    internal static bool WritesAllowed => WritesOverride ?? WritesConfigured;

    internal static bool Writes(ShaderResourcePlan plan) =>
        plan.Memory.Entries.Any(access => access.Access is MemoryAccess.Write or MemoryAccess.Atomic);

    internal static IReadOnlyList<ResourceBranchBlock> Build(ShaderResourcePlan plan, Func<ScalarValue, ScalarValue> rewrite)
    {
        // A shader write can change a predicate through an alias or a later loop iteration.
        if (Writes(plan) && WrittenBuffers(plan) is null) return [];
        var instructions = plan.Graph.Program.Instructions;
        if (instructions.Any(instruction => instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64" or
            "SCbranchJoin" or "SCbranchIFork" or "SCbranchGFork")) return [];

        var flow = plan.Graph.ControlFlow;
        var blocks = new ResourceBranchBlock[flow.Blocks.Count];
        var hasCondition = false;
        var lastByBlock = new Gen5ShaderInstruction?[blocks.Length];
        var instructionPcs = new HashSet<uint>();
        foreach (var instruction in instructions)
        {
            instructionPcs.Add(instruction.Pc);
            var block = flow.BlockOf(instruction.Pc);
            if (block >= 0) lastByBlock[block] = instruction;
        }

        for (var blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            var range = flow.Blocks[blockIndex];
            var last = lastByBlock[blockIndex];
            if (last is null) return [];
            var successors = flow.Successors[blockIndex].ToArray();
            ScalarValue? condition = null;
            var resolver = Gen5IrBranchResolver.Instance;
            if (resolver.IsConditional(last) || Gen5IrBranchResolver.IsUnconditionalBranch(last))
            {
                if (!resolver.TryGetBranchTarget(last, out var target) || !instructionPcs.Contains(target)) return [];
                if (resolver.IsConditional(last))
                {
                    if (blockIndex + 1 >= blocks.Length) return [];
                    // Keep taken and fallthrough edges separate even when both name the same block.
                    successors = [flow.BlockByStartPc[target], blockIndex + 1];
                    if (plan.Graph.BranchConditions.TryGetValue(last.Pc, out var original))
                    {
                        var candidate = rewrite(original);
                        if (plan.ValidateRuntimeValue(candidate)) condition = candidate;
                    }
                }
            }

            var sources = new HashSet<uint>();
            foreach (var access in plan.Memory.Entries)
            {
                if (access.Pc < range.StartPc || access.Pc >= range.EndPc || access.PlanningOnly) continue;
                if (access.Kind is MemoryResourceKind.Buffer or MemoryResourceKind.ScalarBuffer && access.Resource < plan.Info.Buffers.Count)
                    sources.Add(plan.Info.Buffers[(int)access.Resource].Source);
                if (access.Kind == MemoryResourceKind.Image && access.Resource < plan.Info.Images.Count)
                {
                    sources.Add(plan.Info.Images[(int)access.Resource].Source);
                    if (access.NeedsSampler && access.Sampler < plan.Info.Samplers.Count)
                        sources.Add(plan.Info.Samplers[(int)access.Sampler].Source);
                }
            }

            blocks[blockIndex] = new ResourceBranchBlock(condition, successors, sources.Order().ToArray());
            hasCondition |= condition is not null;
        }

        return hasCondition ? blocks : [];
    }

    // The static buffers a shader writes through, when writes are allowed and every write and atomic goes through one of
    // them or to LDS or scratch, which no scalar load reads; otherwise null.
    internal static uint[]? WrittenBuffers(ShaderResourcePlan plan)
    {
        if (!WritesAllowed) return null;
        var buffers = new SortedSet<uint>();
        foreach (var access in plan.Memory.Entries)
        {
            if (access.Access is not (MemoryAccess.Write or MemoryAccess.Atomic) ||
                access.Kind is MemoryResourceKind.LocalDataShare or MemoryResourceKind.Scratch) continue;
            if (access.Kind != MemoryResourceKind.Buffer || access.DeviceDescriptor ||
                access.BufferDescriptor is not { Provenance: BufferDescriptorProvenance.Static } descriptor ||
                descriptor.StaticResource >= plan.Info.Buffers.Count) return null;
            buffers.Add(descriptor.StaticResource);
        }

        return [.. buffers];
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler.Resources;

public sealed record ResourceBranchBlock(ScalarValue? Condition, int[] Successors, uint[] Sources)
{
    internal static IReadOnlyList<ResourceBranchBlock> Build(ShaderResourcePlan plan, Func<ScalarValue, ScalarValue> rewrite)
    {
        // A shader write can change a predicate through an alias or a later loop iteration.
        if (plan.Memory.Entries.Any(access => access.Access is MemoryAccess.Write or MemoryAccess.Atomic)) return [];
        var instructions = plan.Graph.Program.Instructions;
        if (instructions.Any(instruction => instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64" or
            "SCbranchJoin" or "SCbranchIFork" or "SCbranchGFork")) return [];

        var flow = plan.Graph.ControlFlow;
        var blocks = new ResourceBranchBlock[flow.Blocks.Count];
        var hasCondition = false;
        for (var blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            var range = flow.Blocks[blockIndex];
            var last = instructions.LastOrDefault(instruction => instruction.Pc >= range.StartPc && instruction.Pc < range.EndPc);
            if (last is null) return [];
            var successors = flow.Successors[blockIndex].ToArray();
            ScalarValue? condition = null;
            var resolver = Gen5IrBranchResolver.Instance;
            if (resolver.IsConditional(last) || Gen5IrBranchResolver.IsUnconditionalBranch(last))
            {
                if (!resolver.TryGetBranchTarget(last, out var target) || !instructions.Any(instruction => instruction.Pc == target)) return [];
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
}

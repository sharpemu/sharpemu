// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

// Structured control flow for translated programs.
//
// GCN branches are scalar: S_BRANCH and S_CBRANCH_* take the same path for every lane of a wave,
// and lane divergence lives in EXEC instead. Every invocation therefore follows the block path the
// dispatcher would run, and the block graph can be emitted as nested SPIR-V selections and loops.
// That removes the PC dispatcher, whose one loop around thousands of blocks is what makes drivers
// spend seconds compiling a large program.
//
// Supported: reducible graphs. A selection merges at the first block its arms share; returns,
// breaks and continues are legal from any depth, so they do not take part in the merge. A loop with
// several exit blocks records which exit it took in a selector, breaks, and switches on it after the
// loop. A block that arms share without merging at it is copied into each path; the copies add at
// most the size of the program, so the code never more than doubles.
// Anything else keeps the dispatcher. The walk that emits the code also runs dry first, so a
// planned program cannot fail structurally while it is emitted.
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private const int ExitNode = -1;

        // The merge of a loop with several exit blocks: a switch on the loop's selector follows it.
        private const int MultiExitNode = -2;

        private static readonly bool ForceDispatcher = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_SHADER_CONTROL_FLOW"),
            "dispatcher",
            StringComparison.OrdinalIgnoreCase);

        private static readonly bool TraceControlFlow = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_CONTROL_FLOW"),
            "1",
            StringComparison.Ordinal);

        private enum TerminatorKind
        {
            Exit,
            Jump,
            Conditional,
        }

        // Taken and Fallthrough name block indices, or ExitNode when control leaves the program.
        private readonly record struct BlockTerminator(TerminatorKind Kind, int Taken, int Fallthrough, string Opcode);

        private sealed class NaturalLoop
        {
            public required int Header;
            public required HashSet<int> Body;
            public int Merge = ExitNode;
            public List<int> Exits = [];
            public uint Selector;
            public NaturalLoop? Parent;
        }

        private sealed class ControlFlowPlan
        {
            public required IReadOnlyList<ShaderBlock> Blocks;
            public required BlockTerminator[] Terminators;
            public required Dictionary<int, NaturalLoop> LoopsByHeader;
            public required NaturalLoop?[] InnermostLoop;
            public required bool[] Reachable;
        }

        // The kind of place a region's post-dominator walk ends at, other than a block.
        private enum VirtualExit
        {
            None,
            Return,
            Continue,
            Break,
            Mixed,
        }

        private readonly record struct MergeTarget(int Block, VirtualExit Virtual);

        private sealed class LoopFrame
        {
            public required NaturalLoop Loop;
            public required uint ContinueLabel;
            public required uint BreakLabel;
        }

        private ControlFlowPlan? _structuredPlan;
        private uint _structuredBody;
        private bool _structuredDry;
        private bool[] _structuredEmitted = [];
        private int _copiedInstructions;
        private int _copyBudget;
        private Dictionary<(NaturalLoop? Region, int Block), MergeTarget>? _mergeCache;

        private void PrepareControlFlow(IReadOnlyList<ShaderBlock> blocks)
        {
            var reason = "forced";
            var forced = ForceDispatcher || _request.ForceDispatcher;
            if (!forced && TryPlanStructuredControlFlow(blocks, out var plan, out reason))
            {
                _structuredPlan = plan;
                _structuredBody = _module.AllocateId();
                if (TraceControlFlow)
                {
                    Console.Error.WriteLine(
                        $"[SHADER][CFG] structured address=0x{_request.Program.Address:X16} blocks={blocks.Count} loops={plan.LoopsByHeader.Count}");
                }

                return;
            }

            if (TraceControlFlow)
            {
                Console.Error.WriteLine(
                    $"[SHADER][CFG] dispatcher address=0x{_request.Program.Address:X16} blocks={blocks.Count} reason={reason}");
            }

        }

        private bool TryEmitControlFlow(IReadOnlyList<ShaderBlock> blocks, out string error)
        {
            error = string.Empty;
            if (_structuredPlan is null) return TryEmitDispatcher(blocks, out error);
            _module.AddInstruction(SpirvOp.FunctionCall, _voidType, _structuredBody);
            return true;
        }

        // Emits the structured body as its own function; call after the entry point is finished.
        private bool TryEmitStructuredBodyFunction(out string error)
        {
            error = string.Empty;
            if (_structuredPlan is not { } plan)
            {
                return true;
            }

            _module.BeginFunction(_voidType, _module.TypeFunction(_voidType), _structuredBody);
            _module.AddLabel();
            if (_functionScopeState) DeclareRegisterFiles();
            EmitInitialState();
            // Check bounds after initialization, in the same function as the registers.
            var active = Load(_boolType, _programActive);
            var execute = _module.AllocateId();
            var inactive = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, execute, 0);
            _module.AddStatement(SpirvOp.BranchConditional, active, execute, inactive);
            _module.AddLabel(inactive);
            _module.AddStatement(SpirvOp.Return);
            _module.AddLabel(execute);
            _structuredDry = false;
            _structuredEmitted = new bool[plan.Blocks.Count];
            _copiedInstructions = 0;
            _mergeCache = new();
            _blockLabels.Clear();
            if (!TryWalkSequence(plan, 0, null, null, atLoopHeader: false, out error))
            {
                return false;
            }

            _module.EndFunction();
            return true;
        }

        private bool TryPlanStructuredControlFlow(
            IReadOnlyList<ShaderBlock> blocks,
            out ControlFlowPlan plan,
            out string reason)
        {
            plan = null!;
            var count = blocks.Count;
            var terminators = new BlockTerminator[count];
            for (var index = 0; index < count; index++)
            {
                if (!TryClassifyTerminator(blocks, index, out terminators[index]))
                {
                    reason = $"unclassified terminator in block {index}";
                    return false;
                }
            }

            var reachable = new bool[count];
            var order = ReversePostOrder(terminators, reachable);
            var dominators = ComputeDominators(terminators, order, reachable);

            // Reducibility: every retreating edge must be a back edge to a dominating header.
            var loops = new Dictionary<int, NaturalLoop>();
            var position = new int[count];
            for (var index = 0; index < order.Count; index++)
            {
                position[order[index]] = index;
            }

            foreach (var source in order)
            {
                foreach (var target in Successors(terminators[source]))
                {
                    if (target == ExitNode || position[target] > position[source])
                    {
                        continue;
                    }

                    if (!Dominates(dominators, target, source))
                    {
                        reason = $"irreducible edge {source}->{target}";
                        return false;
                    }

                    if (!loops.TryGetValue(target, out var loop))
                    {
                        loop = new NaturalLoop { Header = target, Body = [target] };
                        loops.Add(target, loop);
                    }

                    CollectLoopBody(terminators, loop.Body, target, source);
                }
            }

            // Nest the loops and collect their exit blocks.
            var ordered = loops.Values.OrderByDescending(static loop => loop.Body.Count).ToList();
            var innermost = new NaturalLoop?[count];
            foreach (var loop in ordered)
            {
                foreach (var block in loop.Body)
                {
                    innermost[block] = loop;
                }
            }

            foreach (var loop in ordered)
            {
                loop.Parent = ordered
                    .Where(outer => outer != loop && outer.Body.IsSupersetOf(loop.Body))
                    .OrderBy(static outer => outer.Body.Count)
                    .FirstOrDefault();
                var exits = new HashSet<int>();
                foreach (var block in loop.Body)
                {
                    foreach (var target in Successors(terminators[block]))
                    {
                        if (target != ExitNode && !loop.Body.Contains(target))
                        {
                            exits.Add(target);
                        }
                    }
                }

                loop.Exits = [.. exits.Order()];
                loop.Merge = exits.Count switch
                {
                    0 => ExitNode,
                    1 => loop.Exits[0],
                    _ => MultiExitNode,
                };
            }

            plan = new ControlFlowPlan
            {
                Blocks = blocks,
                Terminators = terminators,
                LoopsByHeader = loops,
                InnermostLoop = innermost,
                Reachable = reachable,
            };

            // Dry run: the same walk that emits, so emission cannot fail structurally.
            _structuredDry = true;
            _structuredEmitted = new bool[count];
            _copiedInstructions = 0;
            _copyBudget = Math.Max(256, _request.Program.Instructions.Count);
            _mergeCache = new();
            _blockLabels.Clear();
            if (!TryWalkSequence(plan, 0, null, null, atLoopHeader: false, out reason))
            {
                return false;
            }

            for (var index = 0; index < count; index++)
            {
                if (reachable[index] && !_structuredEmitted[index])
                {
                    reason = $"reachable block {index} was not placed";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        // Where a sequence ends: reaching `Block` means branching to `Label`, the merge block of
        // the enclosing selection.
        private readonly record struct StopTarget(int Block, uint Label);

        // Walks from `block` until control leaves the sequence: at `stop`, at a loop's continue or
        // break, or at a return. Leaves the current SPIR-V block terminated.
        private bool TryWalkSequence(
            ControlFlowPlan plan,
            int block,
            StopTarget? stop,
            LoopFrame? loop,
            bool atLoopHeader,
            out string error)
        {
            error = string.Empty;
            while (true)
            {
                if (stop is { } stopTarget && block == stopTarget.Block)
                {
                    Emit(SpirvOp.Branch, stopTarget.Label);
                    return true;
                }

                if (block == ExitNode)
                {
                    EmitReturn();
                    return true;
                }

                if (loop is not null && block == loop.Loop.Header && !atLoopHeader)
                {
                    EmitBackEdge(loop);
                    return true;
                }

                if (loop is not null && !loop.Loop.Body.Contains(block))
                {
                    // Leaving the loop: a loop with several exits records which one it took.
                    if (loop.Loop.Merge == MultiExitNode && !_structuredDry)
                    {
                        Store(loop.Loop.Selector, UInt((uint)loop.Loop.Exits.IndexOf(block)));
                    }

                    Emit(SpirvOp.Branch, loop.BreakLabel);
                    return true;
                }

                if (plan.LoopsByHeader.TryGetValue(block, out var nested) && !(atLoopHeader && loop?.Loop == nested))
                {
                    if (!TryWalkLoop(plan, nested, out error))
                    {
                        return false;
                    }

                    if (nested.Merge == ExitNode)
                    {
                        return true;
                    }

                    if (nested.Merge != MultiExitNode)
                    {
                        block = nested.Merge;
                        continue;
                    }

                    // Several exits: switch on the one the loop took. The switch merges where the
                    // exits meet, as a selection does.
                    if (!TryWalkArms(plan, nested.Header, nested.Exits, stop, loop, nested.Selector, out var next, out error))
                    {
                        return false;
                    }

                    if (next < 0)
                    {
                        return true;
                    }

                    block = next;
                    continue;
                }

                atLoopHeader = false;
                uint label;
                if (_structuredEmitted[block])
                {
                    // Reached from a second construct (arms that share a block their selection does
                    // not merge at): the path gets its own copy. Copies are bounded, since each can
                    // copy further shared blocks.
                    _copiedInstructions += plan.Blocks[block].EndIndex - plan.Blocks[block].StartIndex;
                    if (_copiedInstructions > _copyBudget)
                    {
                        error = $"copies of shared blocks exceed {_copyBudget} instructions";
                        return false;
                    }

                    label = AllocateLabel();
                }
                else
                {
                    _structuredEmitted[block] = true;
                    label = BlockLabel(block);
                }

                Emit(SpirvOp.Branch, label);
                EmitLabel(label);
                if (!_structuredDry && !TryEmitBlockInstructions(plan.Blocks, block, out error))
                {
                    return false;
                }

                var terminator = plan.Terminators[block];
                if (terminator.Kind == TerminatorKind.Exit)
                {
                    EmitReturn();
                    return true;
                }

                if (terminator.Kind == TerminatorKind.Jump)
                {
                    block = terminator.Taken;
                    continue;
                }

                if (!TryWalkArms(plan, block, [terminator.Taken, terminator.Fallthrough], stop, loop, 0, out var merged, out error))
                {
                    return false;
                }

                if (merged < 0)
                {
                    return true;
                }

                block = merged;
            }
        }

        // Emits a selection from `header` over `targets`: a conditional branch on the header's
        // terminator, or, given a selector, a switch on the index of the target. Its merge block is
        // synthetic: arms that reach the real merge block branch to it, and it then falls through
        // to that block. `next` is the block the sequence continues at, or -1 when it has ended.
        private bool TryWalkArms(
            ControlFlowPlan plan,
            int header,
            IReadOnlyList<int> targets,
            StopTarget? stop,
            LoopFrame? loop,
            uint selector,
            out int next,
            out string error)
        {
            next = -1;
            error = string.Empty;
            var merge = FindMerge(plan, loop?.Loop, header);
            if (merge.Virtual != VirtualExit.None && stop is { } outer)
            {
                // No block of its own to merge at, but inside a selection: arms that reach the
                // enclosing merge go through this merge, which forwards to it.
                merge = new MergeTarget(outer.Block, VirtualExit.None);
            }

            var mergeLabel = AllocateLabel();
            var armLabels = targets.Select(_ => AllocateLabel()).ToArray();
            if (!_structuredDry)
            {
                // The condition is computed before the merge instruction, which must directly
                // precede the branch.
                if (selector != 0)
                {
                    var operands = new List<uint> { Load(_uintType, selector), armLabels[0] };
                    for (var index = 1; index < armLabels.Length; index++)
                    {
                        operands.Add((uint)index);
                        operands.Add(armLabels[index]);
                    }

                    _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
                    _module.AddStatement(SpirvOp.Switch, [.. operands]);
                }
                else
                {
                    if (!TryGetBranchCondition(plan.Terminators[header].Opcode, out var condition))
                    {
                        error = $"no condition for {plan.Terminators[header].Opcode}";
                        return false;
                    }

                    _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
                    _module.AddStatement(SpirvOp.BranchConditional, condition, armLabels[0], armLabels[1]);
                }
            }

            // Arms stop at the real merge block. With a virtual merge every path leaves through a
            // continue, break or return of its own, so the arms have no stop.
            StopTarget? armStop = merge.Virtual == VirtualExit.None
                ? new StopTarget(merge.Block, mergeLabel)
                : null;
            for (var index = 0; index < targets.Count; index++)
            {
                EmitLabel(armLabels[index]);
                if (!TryWalkSequence(plan, targets[index], armStop, loop, atLoopHeader: false, out error))
                {
                    return false;
                }
            }

            EmitLabel(mergeLabel);
            if (merge.Virtual != VirtualExit.None)
            {
                Emit(SpirvOp.Unreachable);
                return true;
            }

            // The merge is also the enclosing selection's merge: leave through its label.
            if (stop is { } enclosing && merge.Block == enclosing.Block)
            {
                Emit(SpirvOp.Branch, enclosing.Label);
                return true;
            }

            next = merge.Block;
            return true;
        }

        private bool TryWalkLoop(ControlFlowPlan plan, NaturalLoop loop, out string error)
        {
            var header = AllocateLabel();
            var body = AllocateLabel();
            var frame = new LoopFrame
            {
                Loop = loop,
                ContinueLabel = AllocateLabel(),
                BreakLabel = AllocateLabel(),
            };
            if (loop.Merge == MultiExitNode && !_structuredDry)
            {
                loop.Selector = _module.AddGlobalVariable(
                    _privateUintPointer,
                    SpirvStorageClass.Private,
                    _module.Constant(_uintType, 0));
                _interfaces.Add(loop.Selector);
            }

            Emit(SpirvOp.Branch, header);
            EmitLabel(header);
            if (!_structuredDry)
            {
                _module.AddStatement(SpirvOp.LoopMerge, frame.BreakLabel, frame.ContinueLabel, 0);
                _module.AddStatement(SpirvOp.Branch, body);
            }

            EmitLabel(body);
            if (!TryWalkSequence(plan, loop.Header, null, frame, atLoopHeader: true, out error))
            {
                return false;
            }

            // The continue construct: count the iteration for the step guard, then loop.
            EmitLabel(frame.ContinueLabel);
            Emit(SpirvOp.Branch, header);
            EmitLabel(frame.BreakLabel);
            if (!_structuredDry && _maxDispatcherSteps > 0)
            {
                // An exhausted step guard breaks out with the program inactive; it then ends the
                // whole program, as the dispatcher's guard does.
                var active = Load(_boolType, _programActive);
                var stopLabel = AllocateLabel();
                var goOn = AllocateLabel();
                _module.AddStatement(SpirvOp.SelectionMerge, goOn, 0);
                _module.AddStatement(SpirvOp.BranchConditional, active, goOn, stopLabel);
                _module.AddLabel(stopLabel);
                _module.AddStatement(SpirvOp.Return);
                _module.AddLabel(goOn);
            }

            if (loop.Merge == ExitNode)
            {
                Emit(SpirvOp.Unreachable);
            }

            return true;
        }

        private void EmitBackEdge(LoopFrame loop)
        {
            if (!_structuredDry && _maxDispatcherSteps > 0)
            {
                var steps = IAdd(Load(_uintType, _iterationGuard), UInt(1));
                Store(_iterationGuard, steps);
                var withinLimit = _module.AddInstruction(SpirvOp.ULessThan, _boolType, steps, UInt((uint)_maxDispatcherSteps));
                var exhausted = AllocateLabel();
                var goOn = AllocateLabel();
                _module.AddStatement(SpirvOp.SelectionMerge, goOn, 0);
                _module.AddStatement(SpirvOp.BranchConditional, withinLimit, goOn, exhausted);
                _module.AddLabel(exhausted);
                Store(_programActive, _module.ConstantBool(false));
                _module.AddStatement(SpirvOp.Branch, loop.BreakLabel);
                _module.AddLabel(goOn);
            }

            Emit(SpirvOp.Branch, loop.ContinueLabel);
        }

        private void EmitReturn()
        {
            if (!_structuredDry)
            {
                Store(_programActive, _module.ConstantBool(false));
            }

            Emit(SpirvOp.Return);
        }

        // The immediate post-dominator of `block` in its region. A region is a loop body (or the
        // program), with inner loops collapsed to their headers, and the ways control leaves it
        // (continue, break, return) as virtual nodes.
        //
        // Edges to those virtual nodes are left out when a node also has a real successor: a return,
        // break or continue may leave a selection at any depth, so only the blocks the arms share
        // decide the merge. A node whose paths only leave goes to its virtual nodes.
        private MergeTarget FindMerge(ControlFlowPlan plan, NaturalLoop? region, int block)
        {
            if (_mergeCache!.TryGetValue((region, block), out var cached))
            {
                return cached;
            }

            // Nodes: region blocks, then virtual Return / Continue / Break, then the sink.
            var members = new List<int>();
            for (var index = 0; index < plan.Blocks.Count; index++)
            {
                if (plan.Reachable[index] && InRegion(plan, region, index))
                {
                    members.Add(index);
                }
            }

            var id = new Dictionary<int, int>();
            for (var index = 0; index < members.Count; index++)
            {
                id[members[index]] = index;
            }

            var returnNode = members.Count;
            var continueNode = members.Count + 1;
            var breakNode = members.Count + 2;
            var sink = members.Count + 3;
            var successors = new List<int>[sink + 1];
            for (var index = 0; index <= sink; index++)
            {
                successors[index] = [];
            }

            successors[returnNode].Add(sink);
            successors[continueNode].Add(sink);
            successors[breakNode].Add(sink);
            foreach (var member in members)
            {
                var real = new List<int>();
                var leaving = new List<int>();
                foreach (var target in RegionSuccessors(plan, region, member))
                {
                    var node = target switch
                    {
                        ExitNode => returnNode,
                        _ when region is not null && target == region.Header => continueNode,
                        _ when region is not null && !region.Body.Contains(target) => breakNode,
                        _ => id.TryGetValue(target, out var inRegion) ? inRegion : breakNode,
                    };
                    (node < members.Count ? real : leaving).Add(node);
                }

                successors[id[member]].AddRange(real.Count != 0 ? real : leaving.Distinct());
            }

            var postDominators = ComputePostDominators(successors, sink);
            var start = id[block];
            var merge = postDominators[start];
            if (merge >= members.Count)
            {
                // Every path leaves the region, but arms can still share blocks before leaving (an
                // early return on one side). Those arms merge at the first block they share.
                var shared = FindFirstSharedBlock(successors, members.Count, start);
                if (shared >= 0)
                {
                    merge = shared;
                }
            }
            var result = merge == returnNode ? new MergeTarget(0, VirtualExit.Return)
                : merge == continueNode ? new MergeTarget(0, VirtualExit.Continue)
                : merge == breakNode ? new MergeTarget(0, VirtualExit.Break)
                : merge == sink || merge < 0 ? new MergeTarget(0, VirtualExit.Mixed)
                : new MergeTarget(members[merge], VirtualExit.None);
            _mergeCache[(region, block)] = result;
            return result;
        }

        // The block reached from more than one successor of `start` that no other such block
        // reaches, or -1. The region graph is acyclic (inner loops are collapsed), so reach sets
        // are finite and "first" is well defined when one candidate precedes all others.
        private static int FindFirstSharedBlock(List<int>[] successors, int memberCount, int start)
        {
            var reachedBy = new int[memberCount];
            var arms = successors[start].Where(node => node < memberCount).Distinct().ToList();
            foreach (var arm in arms)
            {
                var seen = new bool[memberCount];
                var work = new Stack<int>();
                seen[arm] = true;
                work.Push(arm);
                while (work.Count != 0)
                {
                    var node = work.Pop();
                    reachedBy[node]++;
                    foreach (var next in successors[node])
                    {
                        if (next < memberCount && !seen[next])
                        {
                            seen[next] = true;
                            work.Push(next);
                        }
                    }
                }
            }

            var candidates = Enumerable.Range(0, memberCount).Where(node => reachedBy[node] > 1).ToHashSet();
            if (candidates.Count == 0)
            {
                return -1;
            }

            // Drop every candidate reached from another candidate; exactly one must remain.
            var later = new HashSet<int>();
            foreach (var candidate in candidates)
            {
                var work = new Stack<int>(successors[candidate].Where(node => node < memberCount));
                while (work.Count != 0)
                {
                    var node = work.Pop();
                    if (later.Add(node))
                    {
                        foreach (var next in successors[node])
                        {
                            if (next < memberCount)
                            {
                                work.Push(next);
                            }
                        }
                    }
                }
            }

            var first = candidates.Where(candidate => !later.Contains(candidate)).ToList();
            return first.Count == 1 ? first[0] : -1;
        }

        // A block belongs to a region when its innermost loop is the region itself, or when it is
        // the header of a loop nested directly inside the region.
        private static bool InRegion(ControlFlowPlan plan, NaturalLoop? region, int block)
        {
            var innermost = plan.InnermostLoop[block];
            if (innermost == region)
            {
                return true;
            }

            return innermost is not null && innermost.Header == block && innermost.Parent == region;
        }

        // Successors as the region sees them: a nested loop's header leads to that loop's merge.
        private static IEnumerable<int> RegionSuccessors(ControlFlowPlan plan, NaturalLoop? region, int block)
        {
            var innermost = plan.InnermostLoop[block];
            if (innermost is not null && innermost != region && innermost.Header == block)
            {
                if (innermost.Exits.Count == 0)
                {
                    yield return ExitNode;
                }

                foreach (var exit in innermost.Exits)
                {
                    yield return exit;
                }

                yield break;
            }

            foreach (var target in Successors(plan.Terminators[block]))
            {
                yield return target;
            }
        }

        private static IEnumerable<int> Successors(BlockTerminator terminator)
        {
            switch (terminator.Kind)
            {
                case TerminatorKind.Exit:
                    yield return ExitNode;
                    break;
                case TerminatorKind.Jump:
                    yield return terminator.Taken;
                    break;
                default:
                    yield return terminator.Taken;
                    if (terminator.Fallthrough != terminator.Taken)
                    {
                        yield return terminator.Fallthrough;
                    }

                    break;
            }
        }

        private bool TryClassifyTerminator(IReadOnlyList<ShaderBlock> blocks, int index, out BlockTerminator terminator)
        {
            var instructions = _request.Program.Instructions;
            var last = instructions[blocks[index].EndIndex - 1];
            var fallthrough = index + 1 < blocks.Count ? index + 1 : ExitNode;
            terminator = default;
            if (last.Opcode == "SEndpgm")
            {
                terminator = new BlockTerminator(TerminatorKind.Exit, ExitNode, ExitNode, last.Opcode);
                return true;
            }

            if (last.Opcode == "SBranch" || last.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                if (!TryGetBranchTargetPc(last, out var targetPc))
                {
                    return false;
                }

                int target;
                if (IsExitBranchTarget(instructions, targetPc))
                {
                    target = ExitNode;
                }
                else if (!TryFindBlock(blocks, targetPc, out target))
                {
                    return false;
                }

                terminator = last.Opcode == "SBranch" || target == fallthrough
                    ? new BlockTerminator(TerminatorKind.Jump, target, target, last.Opcode)
                    : new BlockTerminator(TerminatorKind.Conditional, target, fallthrough, last.Opcode);
                return true;
            }

            terminator = new BlockTerminator(TerminatorKind.Jump, fallthrough, fallthrough, last.Opcode);
            return true;
        }

        private static List<int> ReversePostOrder(BlockTerminator[] terminators, bool[] reachable)
        {
            var postOrder = new List<int>();
            var stack = new Stack<(int Block, IEnumerator<int> Next)>();
            reachable[0] = true;
            stack.Push((0, Successors(terminators[0]).GetEnumerator()));
            while (stack.Count != 0)
            {
                var (block, next) = stack.Peek();
                if (next.MoveNext())
                {
                    var target = next.Current;
                    if (target != ExitNode && !reachable[target])
                    {
                        reachable[target] = true;
                        stack.Push((target, Successors(terminators[target]).GetEnumerator()));
                    }

                    continue;
                }

                stack.Pop();
                postOrder.Add(block);
            }

            postOrder.Reverse();
            return postOrder;
        }

        // Cooper, Harvey and Kennedy's iterative dominator algorithm over reverse post-order.
        private static int[] ComputeDominators(BlockTerminator[] terminators, List<int> order, bool[] reachable)
        {
            var count = terminators.Length;
            var position = new int[count];
            for (var index = 0; index < order.Count; index++)
            {
                position[order[index]] = index;
            }

            var predecessors = new List<int>[count];
            for (var index = 0; index < count; index++)
            {
                predecessors[index] = [];
            }

            foreach (var block in order)
            {
                foreach (var target in Successors(terminators[block]))
                {
                    if (target != ExitNode)
                    {
                        predecessors[target].Add(block);
                    }
                }
            }

            var dominators = Enumerable.Repeat(-1, count).ToArray();
            dominators[order[0]] = order[0];
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in order.Skip(1))
                {
                    var candidate = -1;
                    foreach (var predecessor in predecessors[block])
                    {
                        if (dominators[predecessor] < 0)
                        {
                            continue;
                        }

                        candidate = candidate < 0 ? predecessor : Intersect(dominators, position, candidate, predecessor);
                    }

                    if (candidate >= 0 && dominators[block] != candidate)
                    {
                        dominators[block] = candidate;
                        changed = true;
                    }
                }
            }

            return dominators;
        }

        private static int Intersect(int[] dominators, int[] position, int left, int right)
        {
            while (left != right)
            {
                while (position[left] > position[right])
                {
                    left = dominators[left];
                }

                while (position[right] > position[left])
                {
                    right = dominators[right];
                }
            }

            return left;
        }

        private static bool Dominates(int[] dominators, int dominator, int block)
        {
            while (true)
            {
                if (block == dominator)
                {
                    return true;
                }

                var next = dominators[block];
                if (next < 0 || next == block)
                {
                    return false;
                }

                block = next;
            }
        }

        private static void CollectLoopBody(BlockTerminator[] terminators, HashSet<int> body, int header, int latch)
        {
            var predecessors = new Dictionary<int, List<int>>();
            for (var index = 0; index < terminators.Length; index++)
            {
                foreach (var target in Successors(terminators[index]))
                {
                    if (target == ExitNode)
                    {
                        continue;
                    }

                    if (!predecessors.TryGetValue(target, out var list))
                    {
                        predecessors[target] = list = [];
                    }

                    list.Add(index);
                }
            }

            var work = new Stack<int>();
            if (body.Add(latch))
            {
                work.Push(latch);
            }

            while (work.Count != 0)
            {
                var block = work.Pop();
                if (block == header || !predecessors.TryGetValue(block, out var list))
                {
                    continue;
                }

                foreach (var predecessor in list)
                {
                    if (body.Add(predecessor))
                    {
                        work.Push(predecessor);
                    }
                }
            }
        }

        // Immediate post-dominators on a small graph with a single sink, by the same iterative
        // scheme run on the reversed edges. Returns -1 for nodes that cannot reach the sink.
        private static int[] ComputePostDominators(List<int>[] successors, int sink)
        {
            var count = successors.Length;
            var predecessors = new List<int>[count];
            for (var index = 0; index < count; index++)
            {
                predecessors[index] = [];
            }

            for (var node = 0; node < count; node++)
            {
                foreach (var target in successors[node])
                {
                    predecessors[target].Add(node);
                }
            }

            // Post-order of the reversed graph from the sink.
            var visited = new bool[count];
            var postOrder = new List<int>();
            var stack = new Stack<(int Node, int Next)>();
            visited[sink] = true;
            stack.Push((sink, 0));
            while (stack.Count != 0)
            {
                var (node, next) = stack.Pop();
                if (next < predecessors[node].Count)
                {
                    stack.Push((node, next + 1));
                    var predecessor = predecessors[node][next];
                    if (!visited[predecessor])
                    {
                        visited[predecessor] = true;
                        stack.Push((predecessor, 0));
                    }

                    continue;
                }

                postOrder.Add(node);
            }

            postOrder.Reverse();
            var position = new int[count];
            for (var index = 0; index < postOrder.Count; index++)
            {
                position[postOrder[index]] = index;
            }

            var ipdom = Enumerable.Repeat(-1, count).ToArray();
            ipdom[sink] = sink;
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var node in postOrder.Skip(1))
                {
                    var candidate = -1;
                    foreach (var successor in successors[node])
                    {
                        if (ipdom[successor] < 0)
                        {
                            continue;
                        }

                        candidate = candidate < 0 ? successor : Intersect(ipdom, position, candidate, successor);
                    }

                    if (candidate >= 0 && ipdom[node] != candidate)
                    {
                        ipdom[node] = candidate;
                        changed = true;
                    }
                }
            }

            return ipdom;
        }

        private readonly Dictionary<int, uint> _blockLabels = new();

        private uint BlockLabel(int block)
        {
            if (!_blockLabels.TryGetValue(block, out var label))
            {
                label = _structuredDry ? 0u : _module.AllocateId();
                _blockLabels[block] = label;
            }

            return label;
        }

        private uint AllocateLabel() => _structuredDry ? 0u : _module.AllocateId();

        private void EmitLabel(uint label)
        {
            if (!_structuredDry)
            {
                _module.AddLabel(label);
            }
        }

        private void Emit(SpirvOp op, params uint[] operands)
        {
            if (!_structuredDry)
            {
                _module.AddStatement(op, operands);
            }
        }
    }
}

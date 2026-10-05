// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Prove the range of a two-level mask built and consumed by one complete wave.
// A failed proof leaves the general selector analysis unchanged.
internal sealed class WaveMaskSelectorBounds(ScalarValue firstRecord, ScalarValue recordCount, uint rowShift)
{
    internal bool TryEvaluate(RuntimeValueEvaluator evaluator, ComputeSelectorState? state, out uint[] values)
    {
        values = [];
        if (state is not { WaveSize: 64, ThreadsZ: 1, HasPartialWorkgroups: false, LocalInvocationIdComponents: >= 2 } compute ||
            rowShift > 6 || compute.ThreadsX != 1u << (int)rowShift ||
            (ulong)compute.ThreadsX * compute.ThreadsY != 64 ||
            !evaluator.Evaluate(firstRecord, out var first) ||
            !evaluator.Evaluate(recordCount, out var count) || count is 0 or > 1024 ||
            ((count + 63) / 64) * 2 > compute.LocalDataShareDwords)
            return false;

        values = Enumerable.Range(0, (int)count).Select(index => unchecked(first + (uint)index)).ToArray();
        return true;
    }

    internal static WaveMaskSelectorBounds? TryCreate(ShaderResourcePlan plan, Gen5ShaderInstruction selector)
    {
        if (plan.Stage != ShaderStage.Compute) return null;
        var proof = new MaskConstructionProof(plan);
        return proof.TryCreateBounds(selector);
    }

    private sealed class MaskConstructionProof
    {
        private readonly ShaderResourcePlan _plan;
        private readonly Gen5ShaderInstruction[] _instructions;
        private static readonly Gen5Operand Execution = Gen5Operand.Scalar(126);
        private static readonly Gen5Operand Comparison = Gen5Operand.Scalar(106);

        internal MaskConstructionProof(ShaderResourcePlan plan)
        {
            _plan = plan;
            _instructions = plan.Graph.Program.Instructions
                .Where(instruction => instruction.Opcode is not ("SWaitcnt" or "SNop")).ToArray();
        }

        internal WaveMaskSelectorBounds? TryCreateBounds(Gen5ShaderInstruction selector)
        {
            // These instructions can hide register writes or add control-flow edges.
            if (_instructions.Any(instruction => instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal) ||
                instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SCallB64" or "SRfeB64" or
                    "SCbranchJoin" or "SCbranchIFork" or "SCbranchGFork")) return null;

            var selectorIndex = Array.IndexOf(_instructions, selector);
            if (selectorIndex < 0 || selector.Sources.Count != 1) return null;
            var additionIndex = FindPreviousDefinition(selector.Sources[0], selectorIndex);
            if (additionIndex < 0) return null;
            var addition = _instructions[additionIndex];
            if (addition.Opcode != "VAdd3U32" || addition.Sources.Count != 3 || !HasUnmodifiedOperands(addition)) return null;
            var first = addition.Sources[0];
            var groupOffset = addition.Sources[1];
            var bitIndex = addition.Sources[2];
            if (first.Kind != Gen5OperandKind.ScalarRegister || groupOffset.Kind != Gen5OperandKind.ScalarRegister ||
                bitIndex.Kind != Gen5OperandKind.VectorRegister) return null;

            var bitScanIndex = FindPreviousDefinition(bitIndex, additionIndex);
            if (bitScanIndex < 0 || !MatchesOperation(bitScanIndex, "VFfblB32", bitIndex)) return null;
            var wordMask = _instructions[bitScanIndex].Sources.SingleOrDefault();
            var wordLoadIndex = FindPreviousDefinition(wordMask, bitScanIndex);
            if (wordLoadIndex < 0 || !MatchesOperation(wordLoadIndex, "DsReadB32", wordMask) ||
                _instructions[wordLoadIndex].Control is not Gen5DataShareControl { Gds: false, Offset0: 0, Offset1: 0 }) return null;
            var offsetIndex = FindPreviousDefinition(groupOffset, additionIndex);
            if (offsetIndex < 0 || _instructions[offsetIndex].Sources.Count != 2) return null;
            var groupIndex = _instructions[offsetIndex].Sources[0];
            var groupScanIndex = FindPreviousDefinition(groupIndex, offsetIndex);
            if (groupScanIndex < 0 || !MatchesOperation(groupScanIndex, "SFF1I32B32", groupIndex) ||
                !MatchesInstruction(offsetIndex, "SLshlB32", groupOffset, groupIndex, LiteralOperand(5))) return null;
            var summaryMask = _instructions[groupScanIndex].Sources.SingleOrDefault();
            if (summaryMask.Kind != Gen5OperandKind.ScalarRegister ||
                !MatchesInstruction(groupScanIndex + 1, "VLshlrevB32", wordMask, LiteralOperand(2), groupIndex) ||
                offsetIndex != groupScanIndex + 2 || wordLoadIndex != groupScanIndex + 3 ||
                !MatchesInstruction(wordLoadIndex, "DsReadB32", wordMask, wordMask)) return null;

            var stores = Enumerable.Range(0, _instructions.Length)
                .Where(index => _instructions[index].Encoding == Gen5ShaderEncoding.Ds && index != wordLoadIndex).ToArray();
            if (stores.Length != 1) return null;
            var storeIndex = stores[0];
            if (_instructions[storeIndex] is not { Opcode: "DsWrite2B32", Sources.Count: 3,
                Control: Gen5DataShareControl { Gds: false, Offset0: 0, Offset1: 1 } } store || storeIndex < 14 ||
                storeIndex + 12 >= groupScanIndex) return null;

            var address = store.Sources[0];
            var lowData = store.Sources[1];
            var highData = store.Sources[2];
            var wordCounter = _instructions[storeIndex - 3].Sources.ElementAtOrDefault(1);
            var savedExecution = _instructions[storeIndex - 2].Sources.SingleOrDefault();
            var innerExecution = _instructions[storeIndex + 4].Sources.SingleOrDefault();
            var nextWord = GetDestination(storeIndex - 8);
            var localIndex = _instructions[storeIndex - 7].Sources.ElementAtOrDefault(1);
            var predicate = _instructions[storeIndex - 6].Sources.ElementAtOrDefault(1);
            var count = _instructions[storeIndex - 11].Sources.ElementAtOrDefault(0);
            var recordIndex = _instructions[storeIndex - 11].Sources.ElementAtOrDefault(1);
            var recordCounter = GetDestination(storeIndex + 11);
            if (new[] { wordCounter, savedExecution, innerExecution, nextWord, count, recordCounter, summaryMask, first }
                .Any(operand => operand.Kind != Gen5OperandKind.ScalarRegister) ||
                new[] { localIndex, predicate, recordIndex, address, lowData, highData }
                    .Any(operand => operand.Kind != Gen5OperandKind.VectorRegister)) return null;
            if (!HaveDisjointScalarRanges((wordCounter, 1), (recordCounter, 1), (summaryMask, 1), (first, 1),
                    (count, 1), (savedExecution, 2), (innerExecution, 2), (nextWord, 1), (Comparison, 2)) ||
                new[] { address, lowData, highData, predicate, recordIndex }.Contains(localIndex)) return null;

            if (!MatchesMaskStoreAndSummaryUpdate(storeIndex, address, lowData, highData, wordCounter, nextWord,
                    savedExecution, innerExecution, summaryMask, localIndex, predicate, count, recordIndex, recordCounter)) return null;

            var loopStart = BranchTargetIndex(storeIndex + 12);
            if (loopStart < 0 || loopStart + 6 >= storeIndex - 13 ||
                !MatchesInstruction(loopStart, "VAddI32", recordIndex, recordCounter, localIndex) ||
                !MatchesInstruction(loopStart + 1, "SCmpLtU32", null, recordCounter, count) ||
                !MatchesInstruction(loopStart + 2, "VCmpGtU32", null, count, recordIndex) ||
                !MatchesBranch(loopStart + 3, "SCbranchScc0", storeIndex + 13) ||
                !MatchesInstruction(loopStart + 4, "SAndSaveexecB64", savedExecution, Comparison) ||
                !MatchesBranch(loopStart + 5, "SCbranchExecz", storeIndex - 12)) return null;

            var initialization = new[] { wordCounter, recordCounter, summaryMask }
                .Select(register => FindPreviousDefinition(register, loopStart)).ToArray();
            if (initialization.Any(index => index < 0) ||
                !MatchesInstruction(initialization[0], "SMovB32", wordCounter, LiteralOperand(0)) ||
                !MatchesInstruction(initialization[1], "SMovB32", recordCounter, LiteralOperand(0)) ||
                !MatchesInstruction(initialization[2], "SMovB32", summaryMask, LiteralOperand(0))) return null;

            var localDefinition = FindPreviousDefinition(localIndex, loopStart);
            if (localDefinition < 0 || _instructions[localDefinition] is not { Opcode: "VLshlAddU32", Sources.Count: 3 } local ||
                !HasUnmodifiedOperands(local) || local.Sources[0] != Gen5Operand.Vector(1) || local.Sources[2] != Gen5Operand.Vector(0) ||
                !TryGetConstant(local.Sources[1], out var shift) || shift > 6 ||
                !DominatesInstruction(localDefinition, loopStart) ||
                Enumerable.Range(localDefinition, _instructions.Length - localDefinition)
                    .Any(index => IsBranch(_instructions[index]) && BranchTargetIndex(index) <= localDefinition) ||
                _instructions.Take(localDefinition).Any(instruction => WritesRegister(instruction, Gen5Operand.Vector(0)) ||
                    WritesRegister(instruction, Gen5Operand.Vector(1))) ||
                HasRegisterWrite(localIndex, localDefinition + 1, storeIndex - 7)) return null;

            if (!HasCompleteEntryExecutionMask(loopStart) || !VerifyMaskConstructionBody(loopStart, storeIndex, savedExecution, innerExecution, recordIndex,
                    [wordCounter, recordCounter, summaryMask, first, count, localIndex]) ||
                !HasOnlyAllowedEntries(initialization.Min(), storeIndex + 13, [initialization.Min()]) ||
                initialization.Select((index, register) => HasRegisterWrite(new[] { wordCounter, recordCounter, summaryMask }[register],
                    index + 1, loopStart) || !DominatesInstruction(index, groupScanIndex)).Any(changed => changed)) return null;

            if (!VerifyMaskConsumption(groupScanIndex, bitScanIndex, additionIndex, selectorIndex, storeIndex,
                    summaryMask, groupIndex, groupOffset, wordMask, bitIndex, first)) return null;
            var firstValue = RuntimeRead(first, loopStart);
            var countValue = RuntimeRead(count, loopStart);
            if (firstValue is null || countValue is null || HasRegisterWrite(first, loopStart, selectorIndex + 1) ||
                HasRegisterWrite(count, loopStart, storeIndex + 13)) return null;
            return new(firstValue, countValue, shift);
        }

        private bool MatchesMaskStoreAndSummaryUpdate(int store, Gen5Operand address, Gen5Operand lowData, Gen5Operand highData,
            Gen5Operand wordCounter, Gen5Operand nextWord, Gen5Operand savedExecution, Gen5Operand innerExecution,
            Gen5Operand summaryMask, Gen5Operand localIndex, Gen5Operand predicate, Gen5Operand count,
            Gen5Operand recordIndex, Gen5Operand recordCounter) =>
            MatchesInstruction(store - 13, "SMovB64", Execution, innerExecution) &&
            MatchesInstruction(store - 12, "SAndn2B64", Execution, savedExecution, Execution) &&
            MatchesInstruction(store - 11, "VCmpGtU32", null, count, recordIndex) &&
            MatchesInstruction(store - 10, "VCndmaskB32", predicate, LiteralOperand(0), LiteralOperand(1)) &&
            MatchesInstruction(store - 9, "SMovB64", Execution, savedExecution) &&
            MatchesInstruction(store - 8, "SAddI32", nextWord, wordCounter, LiteralOperand(1)) &&
            MatchesInstruction(store - 7, "VCmpEqU32", null, LiteralOperand(0), localIndex) &&
            CompareDestination(store - 6, savedExecution) &&
            MatchesInstruction(store - 6, "VCmpNeU32", savedExecution, LiteralOperand(0), predicate) &&
            MatchesInstruction(store - 5, "SAndSaveexecB64", innerExecution, Comparison) &&
            MatchesBranch(store - 4, "SCbranchExecz", store + 1) &&
            MatchesInstruction(store - 3, "VLshlrevB32", address, LiteralOperand(2), wordCounter) &&
            MatchesInstruction(store - 2, "VMovB32", lowData, savedExecution) &&
            MatchesInstruction(store - 1, "VMovB32", highData, Gen5Operand.Scalar(savedExecution.Value + 1)) &&
            MatchesInstruction(store + 1, "SMovB32", Comparison, summaryMask) &&
            MatchesInstruction(store + 2, "SCmpLgU32", null, savedExecution, LiteralOperand(0)) &&
            MatchesInstruction(store + 3, "SBitset1B32", Comparison, wordCounter) &&
            MatchesInstruction(store + 4, "SMovB64", Execution, innerExecution) &&
            MatchesInstruction(store + 5, "SCselectB32", Gen5Operand.Scalar(107), Comparison, summaryMask) &&
            MatchesInstruction(store + 6, "SCmpLgU32", null, Gen5Operand.Scalar(savedExecution.Value + 1), LiteralOperand(0)) &&
            MatchesInstruction(store + 7, "SMovB32", Comparison, Gen5Operand.Scalar(107)) &&
            MatchesInstruction(store + 8, "SBitset1B32", Comparison, nextWord) &&
            MatchesInstruction(store + 9, "SCselectB32", summaryMask, Comparison, Gen5Operand.Scalar(107)) &&
            MatchesInstruction(store + 10, "SAddI32", wordCounter, wordCounter, LiteralOperand(2)) &&
            MatchesInstruction(store + 11, "SAddI32", recordCounter, recordCounter, LiteralOperand(64)) &&
            MatchesOperation(store + 12, "SBranch", null);

        private bool VerifyMaskConstructionBody(int start, int store, Gen5Operand savedExecution, Gen5Operand innerExecution,
            Gen5Operand recordIndex, Gen5Operand[] preserved)
        {
            var innerSave = Enumerable.Range(start + 6, store - 13 - start - 6)
                .Where(index => MatchesInstruction(index, "SAndSaveexecB64", innerExecution, Comparison)).ToArray();
            if (innerSave.Length != 1 || !MatchesBranch(innerSave[0] + 1, "SCbranchExecz", store - 13)) return false;
            // Work outside the range must retain its original index until the false predicate is written.
            for (var index = start + 6; index < store - 13; index++)
            {
                var instruction = _instructions[index];
                if (preserved.Any(register => WritesRegister(instruction, register)) || WritesRegister(instruction, savedExecution) ||
                    WritesRegister(instruction, Gen5Operand.Scalar(savedExecution.Value + 1))) return false;
                if (index == innerSave[0] || index == innerSave[0] + 1) continue;
                if (WritesRegister(instruction, innerExecution) || WritesRegister(instruction, Gen5Operand.Scalar(innerExecution.Value + 1)) ||
                    ChangesExecution(instruction) || IsBranch(instruction) || instruction.Opcode.Contains("Store", StringComparison.Ordinal) ||
                    instruction.Opcode.Contains("Atomic", StringComparison.Ordinal)) return false;
            }
            return !HasRegisterWrite(recordIndex, start + 1, start + 6);
        }

        private bool VerifyMaskConsumption(int scan, int bitScan, int addition, int selector, int store,
            Gen5Operand summary, Gen5Operand group, Gen5Operand groupOffset, Gen5Operand mask,
            Gen5Operand bit, Gen5Operand first)
        {
            var guard = scan - 5;
            if (guard < 0 || !MatchesInstruction(guard, "SCmpLgU32", null, LiteralOperand(0), summary) ||
                !MatchesOperation(guard + 1, "SCbranchScc0", null) ||
                !MatchesOperation(scan - 3, "VCmpGtF32", null) ||
                !MatchesInstruction(scan - 2, "SAndB64", Comparison, Comparison, Execution) ||
                BranchTargetIndex(scan - 1) != BranchTargetIndex(guard + 1) ||
                !MatchesOperation(scan - 1, "SCbranchScc0", null) ||
                !MatchesInstruction(scan + 4, "VCmpNeU32", null, LiteralOperand(0), mask) ||
                !MatchesOperation(scan + 5, "SCbranchVccz", null) || bitScan != scan + 7 ||
                !MatchesOperation(scan + 6, "VCmpGtF32", null) || addition != bitScan + 3) return false;
            var savedExecution = GetDestination(bitScan + 1);
            if (!MatchesInstruction(bitScan + 1, "SAndSaveexecB64", savedExecution, Comparison) ||
                !MatchesOperation(bitScan + 2, "SCbranchExecz", null)) return false;
            var outerTail = BranchTargetIndex(scan + 5);
            var innerTail = outerTail - 4;
            if (innerTail <= selector || !MatchesBranch(bitScan + 2, "SCbranchExecz", innerTail) ||
                !MatchesInstruction(innerTail, "SMovB64", Execution, savedExecution)) return false;
            var clearWord = GetDestination(innerTail + 1);
            var clearSummary = GetDestination(outerTail);
            if (!MatchesInstruction(innerTail + 1, "VLshlrevB32", clearWord, bit, LiteralOperand(1)) ||
                !MatchesInstruction(innerTail + 2, "VXorB32", mask, clearWord, mask) ||
                !MatchesBranch(innerTail + 3, "SBranch", scan + 4) ||
                !MatchesInstruction(outerTail, "SLshlB32", clearSummary, LiteralOperand(1), group) ||
                !MatchesInstruction(outerTail + 1, "SXorB32", summary, clearSummary, summary) ||
                !MatchesBranch(outerTail + 2, "SBranch", guard) ||
                !MatchesBranch(guard + 1, "SCbranchScc0", outerTail + 3)) return false;
            if (HasRegisterWrite(summary, store + 10, outerTail + 1) ||
                HasRegisterWrite(group, scan + 1, outerTail) || HasRegisterWrite(groupOffset, scan + 3, addition) ||
                HasRegisterWrite(mask, scan + 4, innerTail + 2) || HasRegisterWrite(bit, bitScan + 1, innerTail + 1) ||
                HasRegisterWrite(_instructions[addition].Destinations[0], addition + 1, selector) ||
                HasRegisterWrite(first, store + 13, outerTail + 3)) return false;
            // Nested execution masks may shrink the wave, but must restore the saved mask.
            if (!HaveDisjointScalarRanges((summary, 1), (group, 1), (groupOffset, 1), (first, 1), (savedExecution, 2), (Comparison, 2)) ||
                mask == bit || mask == _instructions[addition].Destinations[0] || bit == _instructions[addition].Destinations[0]) return false;
            var stack = new Stack<Gen5Operand>();
            var scopes = new Dictionary<int, Gen5Operand[]>();
            for (var index = addition + 1; index < innerTail; index++)
            {
                scopes[index] = stack.ToArray();
                var instruction = _instructions[index];
                if (instruction.Opcode == "SAndSaveexecB64" && HasUnmodifiedOperands(instruction))
                {
                    if (instruction.Sources.Count != 1 || instruction.Sources[0] != Comparison) return false;
                    var saved = GetDestination(index);
                    if (!HaveDisjointScalarRanges([(savedExecution, 2), (Comparison, 2), (saved, 2),
                        .. stack.Select(parent => (parent, 2))])) return false;
                    stack.Push(saved);
                }
                else if (stack.Count != 0 && MatchesInstruction(index, "SMovB64", Execution, stack.Peek()))
                    stack.Pop();
                else if (ChangesExecution(instruction) || WritesRegister(instruction, savedExecution) ||
                    WritesRegister(instruction, Gen5Operand.Scalar(savedExecution.Value + 1)) ||
                    stack.Any(saved => WritesRegister(instruction, saved) || WritesRegister(instruction, Gen5Operand.Scalar(saved.Value + 1)))) return false;
            }
            scopes[innerTail] = [];
            foreach (var index in scopes.Keys.Where(index => IsBranch(_instructions[index])))
            {
                var target = BranchTargetIndex(index);
                if (!scopes.TryGetValue(target, out var targetScope) || !scopes[index].SequenceEqual(targetScope)) return false;
            }
            return stack.Count == 0 && HasOnlyAllowedEntries(guard, outerTail + 3, [guard]) &&
                HasOnlyAllowedEntries(addition + 1, innerTail, [addition + 1]) &&
                !HasRegisterWrite(_instructions[addition].Destinations[0], addition + 1, innerTail) &&
                _instructions.Take(outerTail + 3)
                    .All(instruction => !instruction.Opcode.Contains("Store", StringComparison.Ordinal) &&
                        !instruction.Opcode.Contains("Atomic", StringComparison.Ordinal));
        }

        private static bool HaveDisjointScalarRanges(params (Gen5Operand Register, int Width)[] ranges)
        {
            var seen = new HashSet<uint>();
            foreach (var range in ranges)
                for (uint component = 0; component < range.Width; component++)
                    if (range.Register.Value + component >= 126 || !seen.Add(range.Register.Value + component)) return false;
            return true;
        }

        private ScalarValue? RuntimeRead(Gen5Operand register, int before)
        {
            var index = FindPreviousDefinition(register, before);
            if (index < 0 || _instructions[index].Encoding != Gen5ShaderEncoding.Smem || !DominatesInstruction(index, before)) return null;
            var instruction = _instructions[index];
            var component = instruction.Destinations.ToList().IndexOf(register);
            if (component < 0 || !_plan.Memory.TryGetIndex(instruction.Pc, (uint)component, out var memory)) return null;
            var read = _plan.Graph.Accesses[memory]?.Read;
            return read is not null && _plan.ValidateRuntimeValue(read) ? read : null;
        }

        private bool DominatesInstruction(int definition, int target)
        {
            var pending = new Stack<int>();
            var visited = new HashSet<int>();
            pending.Push(0);
            while (pending.TryPop(out var index))
            {
                if (index == definition || index < 0 || index >= _instructions.Length || !visited.Add(index)) continue;
                if (index == target) return false;
                var instruction = _instructions[index];
                if (instruction.Opcode == "SEndpgm") continue;
                if (IsBranch(instruction)) pending.Push(BranchTargetIndex(index));
                if (instruction.Opcode != "SBranch") pending.Push(index + 1);
            }
            return true;
        }

        private bool HasCompleteEntryExecutionMask(int target)
        {
            // Before the producer, accept only saves and restores of the complete entry mask.
            var saved = new Dictionary<Gen5Operand, int>();
            for (var index = 0; index < target; index++)
            {
                var instruction = _instructions[index];
                if (instruction.Opcode == "SOrn2SaveexecB64" && HasUnmodifiedOperands(instruction) && instruction.Sources.Count == 1 &&
                    (instruction.Sources[0] == Execution || saved.TryGetValue(instruction.Sources[0], out var source) && DominatesInstruction(source, index)))
                {
                    saved[GetDestination(index)] = index;
                    continue;
                }
                if (instruction.Opcode == "SMovB64" && instruction.Sources.Count == 1 &&
                    instruction.Destinations.SequenceEqual([Execution]) && saved.TryGetValue(instruction.Sources[0], out var save) && DominatesInstruction(save, index)) continue;
                if (ChangesExecution(instruction)) return false;
                foreach (var register in saved.Keys.Where(register => WritesRegister(instruction, register) ||
                    WritesRegister(instruction, Gen5Operand.Scalar(register.Value + 1))).ToArray()) saved.Remove(register);
            }
            return true;
        }

        private bool HasOnlyAllowedEntries(int start, int end, int[] allowed)
        {
            for (var index = 0; index < _instructions.Length; index++)
            {
                if (!IsBranch(_instructions[index]) || index >= start && index < end) continue;
                var target = BranchTargetIndex(index);
                if (target >= start && target < end && !allowed.Contains(target)) return false;
            }
            return true;
        }

        private int FindPreviousDefinition(Gen5Operand register, int before)
        {
            for (var index = before - 1; index >= 0; index--)
                if (WritesRegister(_instructions[index], register)) return index;
            return -1;
        }

        private bool HasRegisterWrite(Gen5Operand register, int start, int end) =>
            _instructions.Skip(start).Take(end - start).Any(instruction => WritesRegister(instruction, register));

        private Gen5Operand GetDestination(int index) => index >= 0 && index < _instructions.Length
            ? _instructions[index].Destinations.FirstOrDefault() : default;

        private bool MatchesOperation(int index, string opcode, Gen5Operand? destination) =>
            index >= 0 && index < _instructions.Length && _instructions[index].Opcode == opcode &&
            HasUnmodifiedOperands(_instructions[index]) && (destination is null
                ? _instructions[index].Destinations.Count == 0
                : _instructions[index].Destinations.Contains(destination.Value) || CompareDestination(index, destination.Value));

        private bool MatchesInstruction(int index, string opcode, Gen5Operand? destination, params Gen5Operand[] sources) =>
            MatchesOperation(index, opcode, destination) && _instructions[index].Sources.Count == sources.Length &&
            _instructions[index].Sources.Zip(sources).All(pair => pair.First == pair.Second ||
                TryGetConstant(pair.First, out var first) && TryGetConstant(pair.Second, out var second) && first == second);

        private bool CompareDestination(int index, Gen5Operand register) =>
            _instructions[index].Control is Gen5SdwaControl { ScalarDestination: { } destination } &&
            register == Gen5Operand.Scalar(destination);

        private bool MatchesBranch(int index, string opcode, int target) =>
            MatchesOperation(index, opcode, null) && BranchTargetIndex(index) == target;

        private int BranchTargetIndex(int index)
        {
            if (index < 0 || index >= _instructions.Length || !IsBranch(_instructions[index])) return -1;
            var instruction = _instructions[index];
            var target = unchecked(instruction.Pc + 4 + (uint)((short)instruction.Words[0] * 4));
            // A branch may target a wait that was removed from the matching sequence.
            return Array.FindIndex(_instructions, candidate => candidate.Pc >= target);
        }

        private static bool IsBranch(Gen5ShaderInstruction instruction) =>
            instruction.Opcode == "SBranch" || instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal);

        private static bool ChangesExecution(Gen5ShaderInstruction instruction) =>
            WritesRegister(instruction, Execution) || WritesRegister(instruction, Gen5Operand.Scalar(127)) ||
            instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
            instruction.Opcode.Contains("Wrexec", StringComparison.Ordinal) ||
            instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal);

        private static bool WritesRegister(Gen5ShaderInstruction instruction, Gen5Operand register)
        {
            var scalarDestination = instruction.Control switch
            {
                Gen5SdwaControl control => control.ScalarDestination,
                Gen5Vop3Control control => control.ScalarDestination,
                _ => null,
            };
            if (register.Kind == Gen5OperandKind.ScalarRegister && scalarDestination is { } scalar &&
                register.Value >= scalar && register.Value - scalar < 2) return true;
            if (register.Kind == Gen5OperandKind.ScalarRegister && register.Value is 106 or 107 &&
                instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal) && scalarDestination is null) return true;
            return instruction.Destinations.Any(destination => destination == register ||
                destination.Kind == register.Kind && instruction.Opcode.Contains("64", StringComparison.Ordinal) &&
                register.Value == destination.Value + 1);
        }

        private static Gen5Operand LiteralOperand(uint value) => new(Gen5OperandKind.LiteralConstant, value);

        private static bool TryGetConstant(Gen5Operand operand, out uint value)
        {
            value = operand.Value;
            return operand.Kind == Gen5OperandKind.LiteralConstant ||
                operand.Kind == Gen5OperandKind.EncodedConstant && Gen5InlineConstants.TryDecode(operand.Value, out value);
        }

        private static bool HasUnmodifiedOperands(Gen5ShaderInstruction instruction) => instruction.Control switch
        {
            Gen5Vop3Control control => control.AbsoluteMask == 0 && control.NegateMask == 0 &&
                !control.Clamp && control.OutputModifier == 0 && control.OperandSelect == 0,
            Gen5SdwaControl control => control.DestinationSelect == 6 && control.Source0Select == 6 &&
                control.Source1Select == 6 && !control.Source0SignExtend && !control.Source1SignExtend &&
                control.AbsoluteMask == 0 && control.NegateMask == 0 && !control.Clamp && control.OutputModifier == 0,
            Gen5DppControl or Gen5Dpp8Control or Gen5Vop3pControl => false,
            _ => true,
        };
    }
}

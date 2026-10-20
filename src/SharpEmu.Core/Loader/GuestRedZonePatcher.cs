// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Loader;

// Protect the guest red zone from host exception writes on Windows and macOS.
// Split unaligned vector stores when Rosetta requires smaller memory accesses.
internal static class GuestRedZonePatcher
{
    private const int GuestRedZoneBytes = 128;
    private const int MinimumJumpBytes = 5;
    private const int MaximumGroupedBytes = 128;
    private const ulong PageSize = 0x1000;
    private const ulong AllocationAlignment = 0x10000;
    private const ulong MaximumRelativeJumpDistance = 0x7FFF_FFFF;
    private const int MaximumJumpTableEntries = 4096;
    private const int MaximumJumpTableChainInstructions = 8;
    private const int MaximumJumpTableRounds = 4;
    private const ulong LargeExecutableBytes = 32UL << 20;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    private sealed class Progress(string hostName, string phase, int total)
    {
        private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
        private long _lastReport = System.Diagnostics.Stopwatch.GetTimestamp();

        public void Report(int done)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastReport, now) < ProgressInterval)
            {
                return;
            }

            _lastReport = now;
            Console.Error.WriteLine(
                $"[LOADER] {hostName} red-zone {phase}: {done}/{total} ({(total == 0 ? 100 : done * 100L / total)}%) " +
                $"after {System.Diagnostics.Stopwatch.GetElapsedTime(_started, now).TotalSeconds:F0} s");
        }
    }

    private static ulong ExecutableBytes(IReadOnlyList<ProgramHeader> programHeaders)
    {
        var bytes = 0UL;
        foreach (var header in programHeaders)
        {
            if (header.HeaderType == ProgramHeaderType.Load && (header.Flags & ProgramHeaderFlags.Execute) != 0)
            {
                bytes += header.FileSize;
            }
        }

        return bytes;
    }

    internal enum SpanRefusal
    {
        None,
        ControlFlow,
        BranchTargetAfter,
        StackAfter,
        TooShort,
        IndirectBranch,
    }

    public static PatchResult Patch(
        IVirtualMemory memory,
        PhysicalVirtualMemory physicalMemory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        ulong imageSize)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(physicalMemory);
        ArgumentNullException.ThrowIfNull(programHeaders);

        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return default;
        }

        var protectRedZone = (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) &&
            !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RED_ZONE_PATCH"), "1", StringComparison.Ordinal);
        var splitVectorStores = RosettaVectorStorePatch.IsRequired;
        var rewriteInstructions = ShaInstructionRewrite.IsRequired || Sse4aExtractRewrite.IsRequired;
        if (!protectRedZone && !splitVectorStores && !rewriteInstructions)
        {
            return default;
        }

        var hostName = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

        if (!TryDecodeFunctionStarts(memory, programHeaders, imageBase, out var functionStarts))
        {
            Console.Error.WriteLine($"[LOADER][WARN] {hostName} red-zone patch skipped: no usable .eh_frame_hdr table.");
            return default;
        }

        var executableBytes = ExecutableBytes(programHeaders);
        var large = executableBytes >= LargeExecutableBytes;
        if (large)
        {
            Console.Error.WriteLine(
                $"[LOADER] {hostName} red-zone patch: scanning {functionStarts.Length} functions in {executableBytes >> 20} MB of code; " +
                "a large executable takes a while before the game window opens.");
        }

        var sites = CollectPatchSites(
            memory, programHeaders, imageBase, functionStarts, protectRedZone, splitVectorStores, rewriteInstructions, out var scan,
            large ? new Progress(hostName, "scan", functionStarts.Length) : null);
        sites.Sort(static (left, right) => left.Address.CompareTo(right.Address));
        if (sites.Count == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER] {hostName} red-zone scan: functions={scan.Functions} red_zone={scan.RedZoneFunctions} " +
                $"sites=0 unrelocatable={scan.UnrelocatableSites}.");
            return scan;
        }

        var requiredBytes = PageSize;
        foreach (var site in sites)
        {
            // Reserve relocation expansion, stack guards, and alignment for each span.
            var relocatedInstructionCount = site.Instructions.Count + (site.TerminalBranch.HasValue ? 1 : 0);
            requiredBytes = checked(requiredBytes + 32UL + (ulong)relocatedInstructionCount * (splitVectorStores ? 64UL : 32UL));
        }
        requiredBytes = AlignUp(checked(requiredBytes +
            (ulong)scan.ShaInstructionCount * ShaInstructionRewrite.MaximumExpansionBytes +
            (ulong)scan.ExtractCandidates * Sse4aExtractRewrite.MaximumExpansionBytes), PageSize);
        if (!TryAllocateTrampolines(
                physicalMemory,
                imageBase,
                imageSize,
                requiredBytes,
                sites,
                out var trampolineBase))
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] {hostName} red-zone patch skipped: no nearby trampoline range for {sites.Count} sites.");
            return scan with { FailedSites = sites.Count };
        }

        var trampolineCursor = trampolineBase;
        var trampolineEnd = trampolineBase + requiredBytes;
        var patched = 0;
        var failed = 0;
        var extractRewrites = 0;
        var previousEnd = 0UL;
        var patchProgress = large ? new Progress(hostName, "patch", sites.Count) : null;
        foreach (var site in sites)
        {
            patchProgress?.Report(patched + failed);
            // Sites are produced in increasing address order. Patching two that
            // overlap would write one jump over another and send the trampoline
            // into whatever follows, so refuse rather than corrupt guest code.
            if (site.Address < previousEnd)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] {hostName} red-zone site 0x{site.Address:X16} overlaps the previous one " +
                    $"ending at 0x{previousEnd:X16}; left unpatched.");
                failed++;
                continue;
            }

            if (!TryPatchSite(memory, site, ref trampolineCursor, trampolineEnd, splitVectorStores, rewriteInstructions))
            {
                failed++;
                continue;
            }

            previousEnd = site.Address + (ulong)site.ByteLength;
            patched++;
            extractRewrites += site.Instructions.Count(static instruction =>
                Sse4aExtractRewrite.IsRequired && Sse4aExtractRewrite.CanRewrite(instruction));
        }

        var result = scan with
        {
            CandidateSites = sites.Count,
            PatchedSites = patched,
            FailedSites = failed,
            UnrelocatableSites = scan.UnrelocatableSites,
            ControlFlowRefusals = scan.ControlFlowRefusals,
            BranchTargetRefusals = scan.BranchTargetRefusals,
            StackRefusals = scan.StackRefusals,
            TooShortRefusals = scan.TooShortRefusals,
            IndirectBranchRefusals = scan.IndirectBranchRefusals,
            TrampolineBytes = trampolineCursor - trampolineBase,
            ExtractRewrites = extractRewrites,
        };
        Console.Error.WriteLine(
            $"[LOADER] {hostName} red-zone patch: functions={result.Functions} red_zone={result.RedZoneFunctions} " +
            $"sites={result.PatchedSites}/{result.CandidateSites} failed={result.FailedSites} " +
            $"unrelocatable={result.UnrelocatableSites} " +
            $"(control_flow={result.ControlFlowRefusals} branch_target_after={result.BranchTargetRefusals} " +
            $"stack_after={result.StackRefusals} too_short={result.TooShortRefusals} " +
            $"indirect_branch={result.IndirectBranchRefusals}) " +
            $"rosetta_vector_stores={result.VectorStoreCount} sha_rewrites={result.ShaInstructionCount} " +
            $"trampolines=0x{trampolineBase:X16}+0x{result.TrampolineBytes:X}.");
        if (Sse4aExtractRewrite.IsRequired)
        {
            Console.Error.WriteLine(
                $"[LOADER] {hostName} SSE4a EXTRQ rewrite: patched={extractRewrites} candidates={result.ExtractCandidates}.");
        }

        return result;
    }

    private static List<PatchSite> CollectPatchSites(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        IReadOnlyList<ulong> functionStarts,
        bool protectRedZone,
        bool splitVectorStores,
        bool rewriteInstructions,
        out PatchResult result,
        Progress? progress = null)
    {
        var sites = new List<PatchSite>();
        var scannedFunctions = 0;
        var functionCount = 0;
        var redZoneFunctionCount = 0;
        var instructionCount = 0;
        var vectorStoreCount = 0;
        var unrelocatableSites = 0;
        var refusalCounts = new int[6];
        var shaInstructionCount = 0;
        var extractCandidates = 0;
        var branchTargets = new HashSet<ulong>();
        var candidateFunctions = new List<(List<DecodedInstruction> Instructions, bool UsesRedZone, bool CanBorrowFollowingInstructions)>();

        foreach (var header in programHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.Load ||
                (header.Flags & ProgramHeaderFlags.Execute) == 0 ||
                header.FileSize == 0 ||
                header.FileSize > int.MaxValue)
            {
                continue;
            }

            var segmentStart = imageBase + header.VirtualAddress;
            var segmentEnd = segmentStart + header.FileSize;
            var segmentFunctions = functionStarts
                .Where(address => address >= segmentStart && address < segmentEnd)
                .Distinct()
                .Order()
                .ToArray();
            if (segmentFunctions.Length == 0)
            {
                continue;
            }

            var segmentBytes = GC.AllocateUninitializedArray<byte>((int)header.FileSize);
            if (!memory.TryRead(segmentStart, segmentBytes))
            {
                continue;
            }

            for (var functionIndex = 0; functionIndex < segmentFunctions.Length; functionIndex++)
            {
                progress?.Report(scannedFunctions++);
                var functionStart = segmentFunctions[functionIndex];
                var functionEnd = functionIndex + 1 < segmentFunctions.Length
                    ? segmentFunctions[functionIndex + 1]
                    : segmentEnd;
                if (functionEnd <= functionStart || functionEnd - functionStart > int.MaxValue)
                {
                    continue;
                }

                var decoded = DecodeFunction(segmentBytes, segmentStart, functionStart, functionEnd);
                if (decoded.Count == 0)
                {
                    continue;
                }

                functionCount++;
                instructionCount += decoded.Count;
                CollectBranchTargets(decoded, branchTargets);
                var usesRedZone = protectRedZone &&
                    (decoded.Any(static entry => UsesRedZone(entry.Instruction)) ||
                     (decoded.Any(static entry => entry.Instruction.MemoryBase == Register.RBP &&
                         unchecked((long)entry.Instruction.MemoryDisplacement64) < 0) &&
                      GuestStackFrameAnalysis.UsesFrameRedZone(decoded.Select(static entry => entry.Instruction).ToArray())));
                if (!usesRedZone && !splitVectorStores && !rewriteInstructions)
                {
                    continue;
                }

                if (usesRedZone)
                {
                    redZoneFunctionCount++;
                }
                var canBorrowFollowingInstructions = TryCollectJumpTableTargets(
                    memory,
                    programHeaders,
                    imageBase,
                    decoded,
                    functionStart,
                    functionEnd,
                    branchTargets);
                candidateFunctions.Add((decoded, usesRedZone, canBorrowFollowingInstructions));
            }
        }

        // Collect incoming branches from all functions and segments before replacing bytes.
        foreach (var (decoded, usesRedZone, canBorrowFollowingInstructions) in candidateFunctions)
        {
            extractCandidates += decoded.Count(static entry =>
                Sse4aExtractRewrite.IsRequired && Sse4aExtractRewrite.CanRewrite(entry.Instruction));
            // An unresolved indirect jump can enter the middle of a span.
            var groupInstructions = !decoded.Any(static entry => entry.Instruction.FlowControl == FlowControl.IndirectBranch);
            var lastSiteEnd = 0UL;
            for (var instructionIndex = 0; instructionIndex < decoded.Count; instructionIndex++)
            {
                var instruction = decoded[instructionIndex].Instruction;
                var isRewriteSite = rewriteInstructions && CanRewriteInstruction(instruction);
                if (!isRewriteSite &&
                    ((!usesRedZone && !(splitVectorStores && RosettaVectorStorePatch.RequiresStoreSplit(instruction))) ||
                     !IsFaultableGuestMemoryInstruction(instruction)))
                {
                    continue;
                }

                var forward = TryBuildPatchSpan(
                    decoded,
                    instructionIndex,
                    branchTargets,
                    canBorrowFollowingInstructions,
                    groupInstructions,
                    out var span,
                    out var refusal);
                var backward = !forward && canBorrowFollowingInstructions &&
                    TryBuildEnclosingPatchSpan(decoded, instructionIndex, branchTargets, out span);
                if (!forward && !backward)
                {
                    unrelocatableSites++;
                    refusalCounts[(int)refusal]++;
                    continue;
                }

                // A backward span must not replace bytes already covered by an earlier jump.
                if (span.Address < lastSiteEnd)
                {
                    unrelocatableSites++;
                    refusalCounts[(int)SpanRefusal.TooShort]++;
                    continue;
                }

                sites.Add(span);
                lastSiteEnd = span.Address + (ulong)span.ByteLength;
                if (splitVectorStores)
                {
                    vectorStoreCount += span.Instructions.Count(static instruction => RosettaVectorStorePatch.RequiresStoreSplit(instruction));
                }
                if (rewriteInstructions)
                {
                    shaInstructionCount += span.Instructions.Count(static instruction =>
                        ShaInstructionRewrite.IsRequired && ShaInstructionRewrite.CanRewrite(instruction));
                }
                if (!backward)
                {
                    instructionIndex += span.Instructions.Count - 1;
                }
            }
        }

        result = new PatchResult
        {
            Functions = functionCount,
            RedZoneFunctions = redZoneFunctionCount,
            Instructions = instructionCount,
            CandidateSites = sites.Count,
            VectorStoreCount = vectorStoreCount,
            UnrelocatableSites = unrelocatableSites,
            ControlFlowRefusals = refusalCounts[(int)SpanRefusal.ControlFlow],
            BranchTargetRefusals = refusalCounts[(int)SpanRefusal.BranchTargetAfter],
            StackRefusals = refusalCounts[(int)SpanRefusal.StackAfter],
            TooShortRefusals = refusalCounts[(int)SpanRefusal.TooShort],
            IndirectBranchRefusals = refusalCounts[(int)SpanRefusal.IndirectBranch],
            ShaInstructionCount = shaInstructionCount,
            ExtractCandidates = extractCandidates,
        };
        return sites;
    }

    private static List<DecodedInstruction> DecodeFunction(
        byte[] segmentBytes,
        ulong segmentStart,
        ulong functionStart,
        ulong functionEnd)
    {
        var offset = checked((int)(functionStart - segmentStart));
        var length = checked((int)(functionEnd - functionStart));
        var reader = new ArraySliceCodeReader(segmentBytes, offset, length);
        var decoder = Decoder.Create(64, reader, DecoderOptions.None);
        decoder.IP = functionStart;
        var decoded = new List<DecodedInstruction>();
        while (decoder.IP < functionEnd && reader.CanRead)
        {
            decoder.Decode(out var instruction);
            if (instruction.Code == Code.INVALID || instruction.Length <= 0 || instruction.NextIP > functionEnd)
            {
                break;
            }

            decoded.Add(new DecodedInstruction(instruction));
        }

        return decoded;
    }

    private static void CollectBranchTargets(IReadOnlyList<DecodedInstruction> instructions, HashSet<ulong> targets)
    {
        foreach (var decoded in instructions)
        {
            var instruction = decoded.Instruction;
            for (var operand = 0; operand < instruction.OpCount; operand++)
            {
                switch (instruction.GetOpKind(operand))
                {
                    case OpKind.NearBranch16:
                        targets.Add(instruction.NearBranch16);
                        break;
                    case OpKind.NearBranch32:
                        targets.Add(instruction.NearBranch32);
                        break;
                    case OpKind.NearBranch64:
                        targets.Add(instruction.NearBranch64);
                        break;
                }
            }
        }

    }

    private static bool TryCollectJumpTableTargets(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        IReadOnlyList<DecodedInstruction> decoded,
        ulong functionStart,
        ulong functionEnd,
        HashSet<ulong> branchTargets)
    {
        return TryCollectJumpTableTargets(
            (address, entries) =>
                IsRangeInReadableLoad(programHeaders, imageBase, address, (ulong)entries.Length) &&
                memory.TryRead(address, entries),
            decoded,
            functionStart,
            functionEnd,
            branchTargets);
    }

    // Resolve every indirect jump of the function as a bounded relative jump table, or refuse.
    // Bounds and table addresses are proven by walking the control-flow graph backwards
    // from the table load. The graph includes the edges of the tables themselves, so the
    // resolution is repeated until a round reproduces the targets it was built from.
    private static bool TryCollectJumpTableTargets(
        JumpTableReader readTable,
        IReadOnlyList<DecodedInstruction> decoded,
        ulong functionStart,
        ulong functionEnd,
        HashSet<ulong> branchTargets)
    {
        List<int>? jumps = null;
        for (var instructionIndex = 0; instructionIndex < decoded.Count; instructionIndex++)
        {
            if (decoded[instructionIndex].Instruction.FlowControl == FlowControl.IndirectBranch)
            {
                (jumps ??= []).Add(instructionIndex);
            }
        }

        if (jumps is null)
        {
            return true;
        }

        var indexByAddress = new Dictionary<ulong, int>(decoded.Count);
        for (var instructionIndex = 0; instructionIndex < decoded.Count; instructionIndex++)
        {
            indexByAddress[decoded[instructionIndex].Instruction.IP] = instructionIndex;
        }

        var infoFactory = new InstructionInfoFactory();
        var tableTargets = new Dictionary<int, ulong[]>();
        for (var round = 0; round < MaximumJumpTableRounds; round++)
        {
            var predecessors = BuildPredecessors(decoded, indexByAddress, tableTargets);
            var resolved = new Dictionary<int, ulong[]>();
            foreach (var jumpIndex in jumps)
            {
                if (!TryResolveJumpTable(
                        readTable,
                        infoFactory,
                        decoded,
                        predecessors,
                        indexByAddress,
                        jumpIndex,
                        functionStart,
                        functionEnd,
                        out var targets))
                {
                    return false;
                }

                resolved[jumpIndex] = targets;
            }

            if (resolved.Count == tableTargets.Count &&
                resolved.All(entry => tableTargets.TryGetValue(entry.Key, out var previous) &&
                                      previous.AsSpan().SequenceEqual(entry.Value)))
            {
                foreach (var targets in resolved.Values)
                {
                    branchTargets.UnionWith(targets);
                }

                return true;
            }

            tableTargets = resolved;
        }

        return false;
    }

    private static List<(int Index, bool Fallthrough)>?[] BuildPredecessors(
        IReadOnlyList<DecodedInstruction> decoded,
        Dictionary<ulong, int> indexByAddress,
        Dictionary<int, ulong[]> tableTargets)
    {
        var predecessors = new List<(int Index, bool Fallthrough)>?[decoded.Count];
        for (var index = 0; index < decoded.Count; index++)
        {
            var instruction = decoded[index].Instruction;
            switch (instruction.FlowControl)
            {
                case FlowControl.UnconditionalBranch:
                case FlowControl.IndirectBranch:
                case FlowControl.Return:
                case FlowControl.Exception:
                    break;
                default:
                    if (index + 1 < decoded.Count && decoded[index + 1].Instruction.IP == instruction.NextIP)
                    {
                        (predecessors[index + 1] ??= []).Add((index, true));
                    }

                    break;
            }

            if (instruction.FlowControl is FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch &&
                TryGetNearBranchTarget(instruction, out var target) &&
                indexByAddress.TryGetValue(target, out var targetIndex))
            {
                (predecessors[targetIndex] ??= []).Add((index, false));
            }

            if (instruction.FlowControl == FlowControl.IndirectBranch &&
                tableTargets.TryGetValue(index, out var targets))
            {
                foreach (var tableTarget in targets)
                {
                    (predecessors[indexByAddress[tableTarget]] ??= []).Add((index, false));
                }
            }
        }

        return predecessors;
    }

    private static bool HasOnlyFallthroughPredecessor(
        List<(int Index, bool Fallthrough)>?[] predecessors,
        int index)
    {
        return predecessors[index] is [(var previous, true)] && previous == index - 1;
    }

    // jmp T, preceded by add T, B and movsxd T, dword [B + I*4] in one straight-line run.
    private static bool TryResolveJumpTable(
        JumpTableReader readTable,
        InstructionInfoFactory infoFactory,
        IReadOnlyList<DecodedInstruction> decoded,
        List<(int Index, bool Fallthrough)>?[] predecessors,
        Dictionary<ulong, int> indexByAddress,
        int jumpIndex,
        ulong functionStart,
        ulong functionEnd,
        out ulong[] targets)
    {
        targets = [];
        var jump = decoded[jumpIndex].Instruction;
        if (jump.OpCount != 1 || jump.GetOpKind(0) != OpKind.Register ||
            jump.GetOpRegister(0).GetSize() != sizeof(ulong))
        {
            return false;
        }

        var targetRegister = jump.GetOpRegister(0);
        var tableRegister = Register.None;
        var loadIndex = -1;
        for (var index = jumpIndex - 1;
             index >= 0 && index >= jumpIndex - MaximumJumpTableChainInstructions;
             index--)
        {
            if (!HasOnlyFallthroughPredecessor(predecessors, index + 1))
            {
                return false;
            }

            var candidate = decoded[index].Instruction;
            if (tableRegister == Register.None)
            {
                if (!WritesRegister(infoFactory, candidate, targetRegister))
                {
                    continue;
                }

                if (candidate.Mnemonic != Mnemonic.Add || candidate.OpCount != 2 ||
                    candidate.GetOpKind(0) != OpKind.Register || candidate.GetOpKind(1) != OpKind.Register ||
                    candidate.GetOpRegister(0) != targetRegister ||
                    candidate.GetOpRegister(1).GetSize() != sizeof(ulong) ||
                    candidate.GetOpRegister(1) == targetRegister)
                {
                    return false;
                }

                tableRegister = candidate.GetOpRegister(1);
                continue;
            }

            if (WritesRegister(infoFactory, candidate, tableRegister))
            {
                return false;
            }

            if (!WritesRegister(infoFactory, candidate, targetRegister))
            {
                continue;
            }

            if (candidate.Mnemonic != Mnemonic.Movsxd || candidate.OpCount != 2 ||
                candidate.GetOpKind(0) != OpKind.Register || candidate.GetOpKind(1) != OpKind.Memory ||
                candidate.GetOpRegister(0) != targetRegister ||
                candidate.MemorySize.GetSize() != sizeof(int) || candidate.MemoryIndexScale != sizeof(int) ||
                candidate.MemoryBase != tableRegister || candidate.MemoryDisplacement64 != 0 ||
                candidate.MemoryIndex.GetSize() != sizeof(ulong) ||
                candidate.MemoryIndex == tableRegister || candidate.SegmentPrefix != Register.None)
            {
                return false;
            }

            loadIndex = index;
            break;
        }

        if (loadIndex < 0)
        {
            return false;
        }

        var selectorRegister = decoded[loadIndex].Instruction.MemoryIndex;
        if (!TryProveSelectorBound(infoFactory, decoded, predecessors, loadIndex, selectorRegister, out var maximumIndex) ||
            !TryProveTableAddress(infoFactory, decoded, predecessors, loadIndex, tableRegister, out var tableAddress))
        {
            return false;
        }

        var entryCount = checked((int)maximumIndex + 1);
        var entries = GC.AllocateUninitializedArray<byte>(checked(entryCount * sizeof(int)));
        if (!readTable(tableAddress, entries))
        {
            return false;
        }

        var resolved = new ulong[entryCount];
        for (var index = 0; index < entryCount; index++)
        {
            var relative = BinaryPrimitives.ReadInt32LittleEndian(entries.AsSpan(index * sizeof(int)));
            if (!TryAddSignedOffset(tableAddress, relative, out var target) ||
                target < functionStart || target >= functionEnd ||
                !indexByAddress.ContainsKey(target))
            {
                return false;
            }

            resolved[index] = target;
        }

        targets = resolved;
        return true;
    }

    // Every path into the load must leave the fallthrough of ja after an unsigned compare of
    // the selector, then reach the load through register copies and zero-extensions only.
    // A compare narrower than the selector bounds it only when a zero-extension on that path
    // cleared the bits above the compared ones.
    private static bool TryProveSelectorBound(
        InstructionInfoFactory infoFactory,
        IReadOnlyList<DecodedInstruction> decoded,
        List<(int Index, bool Fallthrough)>?[] predecessors,
        int loadIndex,
        Register selectorRegister,
        out ulong maximumIndex)
    {
        maximumIndex = 0;
        var bounded = false;
        var visited = new HashSet<(int Index, Register Register, int Bits)>();
        var pending = new Stack<(int Index, Register Register, int Bits)>();
        pending.Push((loadIndex, selectorRegister, 64));
        while (pending.TryPop(out var state))
        {
            if (state.Index == 0)
            {
                return false;
            }

            if (predecessors[state.Index] is not { } incoming)
            {
                continue;
            }

            foreach (var (predecessorIndex, fallthrough) in incoming)
            {
                var predecessor = decoded[predecessorIndex].Instruction;
                if (fallthrough && predecessor.Mnemonic == Mnemonic.Ja &&
                    TryGetNearBranchTarget(predecessor, out var branchTarget) &&
                    branchTarget != predecessor.NextIP &&
                    predecessorIndex >= 1 &&
                    HasOnlyFallthroughPredecessor(predecessors, predecessorIndex) &&
                    TryGetSelectorCompareBound(decoded[predecessorIndex - 1].Instruction, state.Register, state.Bits, out var bound))
                {
                    maximumIndex = Math.Max(maximumIndex, bound);
                    bounded = true;
                    continue;
                }

                var register = state.Register;
                var bits = state.Bits;
                if (WritesRegister(infoFactory, predecessor, register) &&
                    !TryTraceSelectorDefinition(predecessor, ref register, ref bits))
                {
                    return false;
                }

                if (visited.Add((predecessorIndex, register, bits)))
                {
                    pending.Push((predecessorIndex, register, bits));
                }
            }
        }

        return bounded;
    }

    private static bool TryGetSelectorCompareBound(in Instruction compare, Register register, int bits, out ulong bound)
    {
        bound = 0;
        if (compare.Mnemonic != Mnemonic.Cmp || compare.OpCount != 2 ||
            compare.GetOpKind(0) != OpKind.Register ||
            compare.GetOpRegister(0) is Register.AH or Register.CH or Register.DH or Register.BH ||
            compare.GetOpRegister(0).GetFullRegister() != register.GetFullRegister() ||
            !TryGetNonNegativeImmediate(compare, 1, out bound) ||
            bound >= MaximumJumpTableEntries)
        {
            return false;
        }

        // A 32-bit compare of a 64-bit selector keeps the long-standing assumption that
        // compilers emit it only for a zero-extended 32-bit value.
        var compareBits = compare.GetOpRegister(0).GetSize() * 8;
        return compareBits >= bits || compareBits == 32;
    }

    // Follow the selector through movzx and register moves; any other definition is unbounded.
    private static bool TryTraceSelectorDefinition(in Instruction definition, ref Register register, ref int bits)
    {
        if (definition.OpCount != 2 ||
            definition.GetOpKind(0) != OpKind.Register || definition.GetOpKind(1) != OpKind.Register ||
            definition.GetOpRegister(0).GetFullRegister() != register.GetFullRegister())
        {
            return false;
        }

        var destinationSize = definition.GetOpRegister(0).GetSize();
        var source = definition.GetOpRegister(1);
        var sourceSize = source.GetSize();
        if (!source.IsGPR() || destinationSize < sizeof(int) ||
            source is Register.AH or Register.CH or Register.DH or Register.BH)
        {
            return false;
        }

        switch (definition.Mnemonic)
        {
            case Mnemonic.Movzx when sourceSize < sizeof(int):
            case Mnemonic.Mov when sourceSize == destinationSize:
                register = source;
                bits = Math.Min(bits, sourceSize * 8);
                return true;
            default:
                return false;
        }
    }

    // Every path into the load must define the table register with lea of the same RIP-relative table.
    private static bool TryProveTableAddress(
        InstructionInfoFactory infoFactory,
        IReadOnlyList<DecodedInstruction> decoded,
        List<(int Index, bool Fallthrough)>?[] predecessors,
        int loadIndex,
        Register tableRegister,
        out ulong tableAddress)
    {
        tableAddress = 0;
        var defined = false;
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(loadIndex);
        while (pending.TryPop(out var index))
        {
            if (index == 0)
            {
                return false;
            }

            if (predecessors[index] is not { } incoming)
            {
                continue;
            }

            foreach (var (predecessorIndex, _) in incoming)
            {
                var predecessor = decoded[predecessorIndex].Instruction;
                if (!WritesRegister(infoFactory, predecessor, tableRegister))
                {
                    if (visited.Add(predecessorIndex))
                    {
                        pending.Push(predecessorIndex);
                    }

                    continue;
                }

                if (predecessor.Mnemonic != Mnemonic.Lea || predecessor.OpCount != 2 ||
                    predecessor.GetOpKind(0) != OpKind.Register ||
                    predecessor.GetOpRegister(0) != tableRegister ||
                    !predecessor.IsIPRelativeMemoryOperand ||
                    (defined && predecessor.IPRelativeMemoryAddress != tableAddress))
                {
                    return false;
                }

                tableAddress = predecessor.IPRelativeMemoryAddress;
                defined = true;
            }
        }

        return defined;
    }

    private static bool WritesRegister(InstructionInfoFactory infoFactory, in Instruction instruction, Register register)
    {
        var fullRegister = register.GetFullRegister();
        if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall &&
            fullRegister is Register.RAX or Register.RCX or Register.RDX or Register.RSI or Register.RDI or
                Register.R8 or Register.R9 or Register.R10 or Register.R11)
        {
            return true;
        }

        foreach (var used in infoFactory.GetInfo(instruction).GetUsedRegisters())
        {
            if (used.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite &&
                used.Register.GetFullRegister() == fullRegister)
            {
                return true;
            }
        }

        return false;
    }

    // Test entry point: the code occupies the first codeLength bytes, the tables follow it.
    internal static bool TryResolveJumpTableTargets(
        byte[] image,
        ulong baseAddress,
        int codeLength,
        out ulong[] targets)
    {
        var functionEnd = baseAddress + (ulong)codeLength;
        var decoded = DecodeFunction(image, baseAddress, baseAddress, functionEnd);
        var resolved = new HashSet<ulong>();
        var success = TryCollectJumpTableTargets(
            (address, entries) =>
            {
                if (address < baseAddress || address - baseAddress + (ulong)entries.Length > (ulong)image.Length)
                {
                    return false;
                }

                image.AsSpan((int)(address - baseAddress), entries.Length).CopyTo(entries);
                return true;
            },
            decoded,
            baseAddress,
            functionEnd,
            resolved);
        targets = [.. resolved.Order()];
        return success;
    }

    private static bool IsRangeInReadableLoad(
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        ulong address,
        ulong length)
    {
        foreach (var header in programHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.Load ||
                (header.Flags & ProgramHeaderFlags.Read) == 0 ||
                header.FileSize == 0 ||
                header.VirtualAddress > ulong.MaxValue - imageBase)
            {
                continue;
            }

            var start = imageBase + header.VirtualAddress;
            if (address < start || address - start > header.FileSize ||
                length > header.FileSize - (address - start))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TryAddSignedOffset(ulong address, int offset, out ulong result)
    {
        if (offset >= 0)
        {
            var positive = (ulong)offset;
            if (positive <= ulong.MaxValue - address)
            {
                result = address + positive;
                return true;
            }
        }
        else
        {
            var negative = (ulong)-(long)offset;
            if (negative <= address)
            {
                result = address - negative;
                return true;
            }
        }

        result = 0;
        return false;
    }

    private static bool TryGetNearBranchTarget(in Instruction instruction, out ulong target)
    {
        for (var operand = 0; operand < instruction.OpCount; operand++)
        {
            switch (instruction.GetOpKind(operand))
            {
                case OpKind.NearBranch16:
                    target = instruction.NearBranch16;
                    return true;
                case OpKind.NearBranch32:
                    target = instruction.NearBranch32;
                    return true;
                case OpKind.NearBranch64:
                    target = instruction.NearBranch64;
                    return true;
            }
        }

        target = 0;
        return false;
    }

    private static bool TryGetNonNegativeImmediate(in Instruction instruction, int operand, out ulong value)
    {
        switch (instruction.GetOpKind(operand))
        {
            case OpKind.Immediate8:
                value = instruction.Immediate8;
                return true;
            case OpKind.Immediate16:
                value = instruction.Immediate16;
                return true;
            case OpKind.Immediate32:
                value = instruction.Immediate32;
                return true;
            case OpKind.Immediate64:
                value = instruction.Immediate64;
                return true;
            case OpKind.Immediate8to16 when instruction.Immediate8to16 >= 0:
                value = (ulong)instruction.Immediate8to16;
                return true;
            case OpKind.Immediate8to32 when instruction.Immediate8to32 >= 0:
                value = (ulong)instruction.Immediate8to32;
                return true;
            case OpKind.Immediate8to64 when instruction.Immediate8to64 >= 0:
                value = (ulong)instruction.Immediate8to64;
                return true;
            case OpKind.Immediate32to64 when instruction.Immediate32to64 >= 0:
                value = (ulong)instruction.Immediate32to64;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// Runs the enclosing-span search over a raw instruction range, so the
    /// behaviour can be pinned on the byte sequences that occur in real guest
    /// code rather than on a mocked decode.
    /// </summary>
    // Test hook: the forward span a site at siteAddress gets, as the patcher builds it.
    internal static bool TryBuildForwardSpan(
        byte[] code,
        ulong baseAddress,
        ulong siteAddress,
        out int spanLength,
        out int coreStart,
        out int coreCount)
    {
        spanLength = 0;
        coreStart = 0;
        coreCount = 0;
        var decoded = DecodeFunction(code, baseAddress, baseAddress, baseAddress + (ulong)code.Length);
        var siteIndex = -1;
        for (var index = 0; index < decoded.Count; index++)
        {
            if (decoded[index].Instruction.IP == siteAddress)
            {
                siteIndex = index;
                break;
            }
        }

        var branchTargets = new HashSet<ulong>();
        CollectBranchTargets(decoded, branchTargets);
        var groupInstructions = !decoded.Any(static entry => entry.Instruction.FlowControl == FlowControl.IndirectBranch);
        if (siteIndex < 0 ||
            !TryBuildPatchSpan(decoded, siteIndex, branchTargets, true, groupInstructions, out var site, out _))
        {
            return false;
        }

        spanLength = site.ByteLength;
        coreStart = site.CoreStart;
        coreCount = site.CoreCount;
        return true;
    }

    internal static bool TryBuildEnclosingSpan(
        byte[] code,
        ulong baseAddress,
        ulong faultAddress,
        out ulong spanAddress,
        out int spanLength,
        out int coreStart,
        out int coreCount)
    {
        spanAddress = 0;
        spanLength = 0;
        coreStart = 0;
        coreCount = 0;

        var decoded = DecodeFunction(code, baseAddress, baseAddress, baseAddress + (ulong)code.Length);
        var faultIndex = -1;
        for (var index = 0; index < decoded.Count; index++)
        {
            if (decoded[index].Instruction.IP == faultAddress)
            {
                faultIndex = index;
                break;
            }
        }

        if (faultIndex < 0)
        {
            return false;
        }

        var branchTargets = new HashSet<ulong>();
        CollectBranchTargets(decoded, branchTargets);
        var hasIndirectBranch = decoded.Any(static entry =>
            entry.Instruction.FlowControl == FlowControl.IndirectBranch);
        if (hasIndirectBranch ||
            !TryBuildEnclosingPatchSpan(decoded, faultIndex, branchTargets, out var site))
        {
            return false;
        }

        spanAddress = site.Address;
        spanLength = site.ByteLength;
        coreStart = site.CoreStart;
        coreCount = site.CoreCount;
        return true;
    }

    /// <summary>
    /// Finds the faultable instructions of a span. The RSP shift has to bracket
    /// all of them and nothing that reads or writes through RSP, because the
    /// shift would move such an access by 128 bytes.
    /// </summary>
    private static bool TryComputeShiftedCore(IList<Instruction> instructions, out int coreStart, out int coreCount)
    {
        coreStart = -1;
        var coreEnd = -1;
        for (var index = 0; index < instructions.Count; index++)
        {
            if (!IsFaultableGuestMemoryInstruction(instructions[index]) && !CanRewriteInstruction(instructions[index]))
            {
                continue;
            }

            if (coreStart < 0)
            {
                coreStart = index;
            }

            coreEnd = index;
        }

        if (coreStart < 0)
        {
            // Nothing in the span touches guest memory: it is a SHA or vector-store rewrite over
            // register-only instructions. The span builder already refused anything that touches
            // RSP, so the whole span can run inside the shift. Refusing it here left every SHA
            // instruction unrewritten, trapping on each execution on hosts without SHA.
            coreStart = 0;
            coreCount = instructions.Count;
            return true;
        }

        for (var index = coreStart; index <= coreEnd; index++)
        {
            if (TouchesStackPointer(instructions[index]))
            {
                coreCount = 0;
                return false;
            }
        }

        coreCount = coreEnd - coreStart + 1;
        return true;
    }

    /// <summary>
    /// Builds a span that <em>ends</em> at the faulting instruction, taking the
    /// bytes it needs from the instructions before it. The instruction after the
    /// site is never overwritten, so branches into it keep working - which is
    /// what the forward search cannot offer when that instruction is a branch
    /// target or touches RSP.
    /// </summary>
    private static bool TryBuildEnclosingPatchSpan(
        IReadOnlyList<DecodedInstruction> decoded,
        int faultIndex,
        IReadOnlySet<ulong> branchTargets,
        out PatchSite site)
    {
        site = default;
        var faulting = decoded[faultIndex].Instruction;
        if (faulting.FlowControl != FlowControl.Next || TouchesStackPointer(faulting))
        {
            return false;
        }

        // Starting before the faulting instruction only works when nothing
        // branches directly to it: such a branch would land inside the new jump.
        if (branchTargets.Contains(faulting.IP))
        {
            return false;
        }

        var byteLength = faulting.Length;
        for (var startIndex = faultIndex - 1; startIndex >= 0; startIndex--)
        {
            var candidate = decoded[startIndex].Instruction;
            if (candidate.FlowControl != FlowControl.Next)
            {
                return false;
            }

            byteLength += candidate.Length;

            // A branch target on the first instruction is fine: it lands on the
            // jump that replaces the span. One in the middle is not, so stop as
            // soon as the span is long enough rather than growing past it.
            if (byteLength < MinimumJumpBytes)
            {
                if (branchTargets.Contains(candidate.IP))
                {
                    return false;
                }

                continue;
            }

            var instructions = new List<Instruction>(faultIndex - startIndex + 1);
            for (var index = startIndex; index <= faultIndex; index++)
            {
                instructions.Add(decoded[index].Instruction);
            }

            if (!TryComputeShiftedCore(instructions, out var coreStart, out var coreCount))
            {
                return false;
            }

            site = new PatchSite(candidate.IP, byteLength, instructions, coreStart, coreCount);
            return true;
        }

        return false;
    }

    private static bool TryBuildPatchSpan(
        IReadOnlyList<DecodedInstruction> decoded,
        int startIndex,
        IReadOnlySet<ulong> branchTargets,
        bool canBorrowFollowingInstructions,
        bool groupInstructions,
        out PatchSite site,
        out SpanRefusal refusal)
    {
        refusal = SpanRefusal.None;
        var instructions = new List<Instruction>(3);
        Instruction? terminalBranch = null;
        var byteLength = 0;
        for (var index = startIndex; index < decoded.Count; index++)
        {
            var instruction = decoded[index].Instruction;
            if (byteLength < MinimumJumpBytes && index != startIndex &&
                canBorrowFollowingInstructions &&
                !branchTargets.Contains(instruction.IP) &&
                IsRelocatableDirectBranch(instruction))
            {
                // A short access followed by its branch (TEST mem; JZ) closes the
                // span with that branch; the trampoline runs it once RSP is restored.
                terminalBranch = instruction;
                byteLength += instruction.Length;
                break;
            }

            if (index != startIndex && !canBorrowFollowingInstructions)
            {
                refusal = SpanRefusal.IndirectBranch;
            }
            else if (instruction.FlowControl != FlowControl.Next)
            {
                refusal = SpanRefusal.ControlFlow;
            }
            else if (index != startIndex && branchTargets.Contains(instruction.IP))
            {
                refusal = SpanRefusal.BranchTargetAfter;
            }
            else if (TouchesStackPointer(instruction))
            {
                refusal = SpanRefusal.StackAfter;
            }

            if (refusal != SpanRefusal.None || byteLength + instruction.Length > MaximumGroupedBytes)
            {
                break;
            }

            instructions.Add(instruction);
            byteLength += instruction.Length;
            if (!groupInstructions && byteLength >= MinimumJumpBytes)
            {
                break;
            }
        }

        if (byteLength < MinimumJumpBytes)
        {
            if (refusal == SpanRefusal.None) refusal = SpanRefusal.TooShort;
            site = default;
            return false;
        }

        if (!TryComputeShiftedCore(instructions, out var coreStart, out var coreCount))
        {
            refusal = SpanRefusal.TooShort;
            site = default;
            return false;
        }

        refusal = SpanRefusal.None;
        site = new PatchSite(decoded[startIndex].Instruction.IP, byteLength, instructions, coreStart, coreCount, terminalBranch);
        return true;
    }

    // Instructions replaced by a software expansion inside the trampoline.
    internal static bool CanRewriteInstruction(in Instruction instruction) =>
        (ShaInstructionRewrite.IsRequired && ShaInstructionRewrite.CanRewrite(instruction)) ||
        (Sse4aExtractRewrite.IsRequired && Sse4aExtractRewrite.CanRewrite(instruction));

    private static IList<Instruction> ExpandRewrites(IList<Instruction> instructions)
    {
        if (ShaInstructionRewrite.IsRequired)
        {
            instructions = ShaInstructionRewrite.Expand(instructions);
        }

        return Sse4aExtractRewrite.IsRequired ? Sse4aExtractRewrite.Expand(instructions) : instructions;
    }

    private static bool IsRelocatableDirectBranch(in Instruction instruction) =>
        instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch &&
        instruction.OpCount == 1 &&
        instruction.GetOpKind(0) is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;

    internal static bool UsesRedZone(in Instruction instruction)
    {
        if (!HasMemoryOperand(instruction) || instruction.MemoryBase != Register.RSP)
        {
            return false;
        }

        var displacement = unchecked((long)instruction.MemoryDisplacement64);
        return displacement < 0 && displacement >= -GuestRedZoneBytes;
    }


    private static bool UsesStackMemory(in Instruction instruction)
    {
        if (!HasMemoryOperand(instruction))
        {
            return false;
        }

        return instruction.MemoryBase is Register.RSP or Register.ESP or Register.SP;
    }

    private static bool TouchesStackPointer(in Instruction instruction)
    {
        if (UsesStackMemory(instruction) || instruction.IsStackInstruction ||
            instruction.Mnemonic is
                Mnemonic.Push or
                Mnemonic.Pop or
                Mnemonic.Pushfq or
                Mnemonic.Popfq or
                Mnemonic.Enter or
                Mnemonic.Leave)
        {
            return true;
        }

        for (var operand = 0; operand < instruction.OpCount; operand++)
        {
            if (instruction.GetOpKind(operand) == OpKind.Register &&
                instruction.GetOpRegister(operand) is Register.RSP or Register.ESP or Register.SP or Register.SPL)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsFaultableGuestMemoryInstruction(in Instruction instruction)
    {
        return instruction.FlowControl == FlowControl.Next &&
               instruction.Mnemonic is not (Mnemonic.Lea or Mnemonic.Nop) &&
               HasMemoryOperand(instruction) &&
               !TouchesStackPointer(instruction);
    }

    private static bool HasMemoryOperand(in Instruction instruction)
    {
        for (var operand = 0; operand < instruction.OpCount; operand++)
        {
            if (instruction.GetOpKind(operand) is
                OpKind.Memory or
                OpKind.MemorySegSI or
                OpKind.MemorySegESI or
                OpKind.MemorySegRSI or
                OpKind.MemorySegDI or
                OpKind.MemorySegEDI or
                OpKind.MemorySegRDI or
                OpKind.MemoryESDI or
                OpKind.MemoryESEDI or
                OpKind.MemoryESRDI)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryEncodeSegment(
        IList<Instruction> instructions,
        int start,
        int count,
        ulong address,
        out byte[]? encoded)
    {
        encoded = null;
        if (count <= 0)
        {
            return true;
        }

        var writer = new ListCodeWriter();
        var segment = instructions.Skip(start).Take(count).ToList();
        if (!BlockEncoder.TryEncode(64, new InstructionBlock(writer, segment, address), out _, out _, BlockEncoderOptions.None))
        {
            return false;
        }

        encoded = writer.ToArray();
        return true;
    }

    private static bool TryPatchSite(
        IVirtualMemory memory,
        PatchSite site,
        ref ulong trampolineCursor,
        ulong trampolineEnd,
        bool splitVectorStores,
        bool rewriteInstructions)
    {
        // A span that starts before the faulting instruction may relocate an
        // RSP-relative access, which must run before the shift or it would read
        // 128 bytes away. Emit such a span as prefix / shifted core / suffix.
        // A forward span starts at the faulting instruction and never steals a
        // stack-dependent one, so it keeps the original single-block shape.
        byte[]? prefix = null;
        if (site.CoreStart > 0 &&
            !TryEncodeSegment(site.Instructions, 0, site.CoreStart, trampolineCursor, out prefix))
        {
            return false;
        }

        var prefixLength = prefix?.Length ?? 0;
        var writer = new ListCodeWriter();
        var relocatedAddress = trampolineCursor + (ulong)prefixLength + 5;
        var core = prefix is null
            ? site.Instructions
            : site.Instructions.Skip(site.CoreStart).Take(site.CoreCount).ToList();
        var instructions = splitVectorStores ? RosettaVectorStorePatch.SplitVectorStores(core) : core;
        if (rewriteInstructions)
        {
            instructions = ExpandRewrites(instructions);
        }
        var block = new InstructionBlock(writer, instructions, relocatedAddress);
        if (!BlockEncoder.TryEncode(64, block, out _, out _, BlockEncoderOptions.None))
        {
            return false;
        }

        var relocated = writer.ToArray();
        byte[]? branch = null;
        if (site.TerminalBranch is { } terminalBranch &&
            !TryEncodeSegment(
                [terminalBranch],
                0,
                1,
                trampolineCursor + (ulong)prefixLength + 5 + (ulong)relocated.Length + 8,
                out branch))
        {
            return false;
        }

        var branchLength = branch?.Length ?? 0;
        var trampolineLength = checked(prefixLength + 5 + relocated.Length + 8 + branchLength + 5);
        if (trampolineCursor > trampolineEnd || (ulong)trampolineLength > trampolineEnd - trampolineCursor)
        {
            return false;
        }

        var trampoline = new byte[trampolineLength];
        prefix?.CopyTo(trampoline, 0);
        // lea rsp,[rsp-128] -- LEA preserves guest flags.
        trampoline[prefixLength] = 0x48;
        trampoline[prefixLength + 1] = 0x8D;
        trampoline[prefixLength + 2] = 0x64;
        trampoline[prefixLength + 3] = 0x24;
        trampoline[prefixLength + 4] = 0x80;
        relocated.CopyTo(trampoline, prefixLength + 5);
        var restoreOffset = prefixLength + 5 + relocated.Length;
        // lea rsp,[rsp+128]
        trampoline[restoreOffset] = 0x48;
        trampoline[restoreOffset + 1] = 0x8D;
        trampoline[restoreOffset + 2] = 0xA4;
        trampoline[restoreOffset + 3] = 0x24;
        trampoline[restoreOffset + 4] = 0x80;
        trampoline[restoreOffset + 5] = 0;
        trampoline[restoreOffset + 6] = 0;
        trampoline[restoreOffset + 7] = 0;
        // LEA leaves the flags alone, so a conditional branch still sees the
        // result of the relocated compare.
        branch?.CopyTo(trampoline, restoreOffset + 8);
        var returnJumpOffset = restoreOffset + 8 + branchLength;
        if (!TryWriteRelativeJump(
                trampoline.AsSpan(returnJumpOffset, 5),
                trampolineCursor + (ulong)returnJumpOffset,
                site.Address + (ulong)site.ByteLength))
        {
            return false;
        }

        var originalPatch = new byte[site.ByteLength];
        originalPatch.AsSpan().Fill(0x90);
        if (!TryWriteRelativeJump(originalPatch.AsSpan(0, 5), site.Address, trampolineCursor) ||
            !memory.TryWrite(trampolineCursor, trampoline) ||
            !memory.TryWrite(site.Address, originalPatch))
        {
            return false;
        }

        trampolineCursor = AlignUp(trampolineCursor + (ulong)trampolineLength, 16);
        return true;
    }

    internal static bool TryWriteRelativeJump(Span<byte> destination, ulong instructionAddress, ulong targetAddress)
    {
        var displacement = unchecked((long)targetAddress - (long)(instructionAddress + 5));
        if (displacement < int.MinValue || displacement > int.MaxValue)
        {
            return false;
        }

        destination[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(destination[1..], (int)displacement);
        return true;
    }

    private static bool TryAllocateTrampolines(
        PhysicalVirtualMemory memory,
        ulong imageBase,
        ulong imageSize,
        ulong requiredBytes,
        IReadOnlyList<PatchSite> sites,
        out ulong address)
    {
        var minimumSite = sites.Min(static site => site.Address);
        var maximumReturn = sites.Max(static site => checked(site.Address + (ulong)site.ByteLength));
        // Keep the complete allocation in reach of every site and return address.
        // Leave room for the five-byte jump when calculating either displacement.
        var distance = MaximumRelativeJumpDistance - MinimumJumpBytes;
        var start = maximumReturn > distance ? maximumReturn - distance : AllocationAlignment;
        var end = minimumSite > ulong.MaxValue - distance ? ulong.MaxValue : minimumSite + distance;
        address = 0;
        if (start >= end) return false;
        var imageEnd = checked(imageBase + imageSize);
        var preferred = Math.Clamp(imageEnd, start, end);
        return memory.TryAllocateWithinRange(preferred, end, requiredBytes, out address) ||
            memory.TryAllocateWithinRange(start, preferred, requiredBytes, out address);
    }

    private static bool TryDecodeFunctionStarts(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        out ulong[] functionStarts)
    {
        foreach (var header in programHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.GnuEhFrame ||
                header.MemorySize < 8 ||
                header.MemorySize > int.MaxValue)
            {
                continue;
            }

            var headerAddress = imageBase + header.VirtualAddress;
            var bytes = GC.AllocateUninitializedArray<byte>((int)header.MemorySize);
            if (!memory.TryRead(headerAddress, bytes) ||
                !EhFrameHeaderDecoder.TryDecode(bytes, headerAddress, out functionStarts))
            {
                continue;
            }

            return true;
        }

        functionStarts = Array.Empty<ulong>();
        return false;
    }

    private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) & ~(alignment - 1));

    private static ulong AlignDown(ulong value, ulong alignment) => value & ~(alignment - 1);

    private delegate bool JumpTableReader(ulong address, byte[] entries);

    private readonly record struct DecodedInstruction(Instruction Instruction);

    /// <summary>
    /// A run of guest instructions relocated into one trampoline.
    /// <paramref name="CoreStart"/> and <paramref name="CoreCount"/> delimit the
    /// faultable instructions the RSP shift must bracket. Instructions outside
    /// that core run unshifted, which is what lets a span start before the
    /// faulting instruction and still relocate an RSP-relative one correctly.
    /// </summary>
    private readonly record struct PatchSite(
        ulong Address,
        int ByteLength,
        IList<Instruction> Instructions,
        int CoreStart,
        int CoreCount,
        Instruction? TerminalBranch = null);

    internal readonly record struct PatchResult
    {
        public int Functions { get; init; }

        public int RedZoneFunctions { get; init; }

        public int Instructions { get; init; }

        public int CandidateSites { get; init; }

        public int VectorStoreCount { get; init; }

        public int ShaInstructionCount { get; init; }

        public int ExtractCandidates { get; init; }

        public int ExtractRewrites { get; init; }

        public int PatchedSites { get; init; }

        public int FailedSites { get; init; }

        /// <summary>
        /// Faultable instructions inside a red-zone function that no trampoline
        /// span could cover. These stay unprotected and are not counted by
        /// <see cref="FailedSites"/>, which only tracks sites that were selected
        /// and then failed to patch.
        /// </summary>
        public int UnrelocatableSites { get; init; }

        public int ControlFlowRefusals { get; init; }

        public int BranchTargetRefusals { get; init; }

        public int StackRefusals { get; init; }

        public int TooShortRefusals { get; init; }

        public int IndirectBranchRefusals { get; init; }

        public ulong TrampolineBytes { get; init; }
    }

    private sealed class ArraySliceCodeReader : CodeReader
    {
        private readonly byte[] _bytes;
        private readonly int _end;
        private int _offset;

        public ArraySliceCodeReader(byte[] bytes, int offset, int length)
        {
            _bytes = bytes;
            _offset = offset;
            _end = checked(offset + length);
        }

        public bool CanRead => _offset < _end;

        public override int ReadByte() => _offset < _end ? _bytes[_offset++] : -1;
    }

    private sealed class ListCodeWriter : CodeWriter
    {
        private readonly List<byte> _bytes = new();

        public override void WriteByte(byte value) => _bytes.Add(value);

        public byte[] ToArray() => _bytes.ToArray();
    }

    private static class EhFrameHeaderDecoder
    {
        private const byte FormatMask = 0x0F;
        private const byte ApplicationMask = 0x70;
        private const byte PcRelative = 0x10;
        private const byte DataRelative = 0x30;
        private const byte Indirect = 0x80;
        private const byte Omit = 0xFF;

        public static bool TryDecode(ReadOnlySpan<byte> bytes, ulong address, out ulong[] starts)
        {
            starts = Array.Empty<ulong>();
            if (bytes.Length < 4 || bytes[0] != 1)
            {
                return false;
            }

            var cursor = 4;
            if (!TryReadPointer(bytes, ref cursor, bytes[1], address, out _) ||
                bytes[2] == Omit ||
                bytes[3] == Omit ||
                !TryReadPointer(bytes, ref cursor, bytes[2], address, out var count) ||
                count > (ulong)bytes.Length / 2 ||
                count > int.MaxValue)
            {
                return false;
            }

            var result = new ulong[(int)count];
            for (var index = 0; index < result.Length; index++)
            {
                if (!TryReadPointer(bytes, ref cursor, bytes[3], address, out result[index]) ||
                    !TryReadPointer(bytes, ref cursor, bytes[3], address, out _))
                {
                    starts = Array.Empty<ulong>();
                    return false;
                }
            }

            starts = result;
            return true;
        }

        private static bool TryReadPointer(
            ReadOnlySpan<byte> bytes,
            ref int cursor,
            byte encoding,
            ulong dataRelativeBase,
            out ulong value)
        {
            value = 0;
            if (encoding == Omit)
            {
                return false;
            }

            var fieldAddress = dataRelativeBase + (ulong)cursor;
            long signedValue;
            ulong unsignedValue;
            var isSigned = false;
            switch (encoding & FormatMask)
            {
                case 0x00 when TryTake(bytes, ref cursor, 8, out var native):
                    unsignedValue = BinaryPrimitives.ReadUInt64LittleEndian(native);
                    break;
                case 0x02 when TryTake(bytes, ref cursor, 2, out var unsigned16):
                    unsignedValue = BinaryPrimitives.ReadUInt16LittleEndian(unsigned16);
                    break;
                case 0x03 when TryTake(bytes, ref cursor, 4, out var unsigned32):
                    unsignedValue = BinaryPrimitives.ReadUInt32LittleEndian(unsigned32);
                    break;
                case 0x04 when TryTake(bytes, ref cursor, 8, out var unsigned64):
                    unsignedValue = BinaryPrimitives.ReadUInt64LittleEndian(unsigned64);
                    break;
                case 0x0A when TryTake(bytes, ref cursor, 2, out var signed16):
                    signedValue = BinaryPrimitives.ReadInt16LittleEndian(signed16);
                    unsignedValue = unchecked((ulong)signedValue);
                    isSigned = true;
                    break;
                case 0x0B when TryTake(bytes, ref cursor, 4, out var signed32):
                    signedValue = BinaryPrimitives.ReadInt32LittleEndian(signed32);
                    unsignedValue = unchecked((ulong)signedValue);
                    isSigned = true;
                    break;
                case 0x0C when TryTake(bytes, ref cursor, 8, out var signed64):
                    signedValue = BinaryPrimitives.ReadInt64LittleEndian(signed64);
                    unsignedValue = unchecked((ulong)signedValue);
                    isSigned = true;
                    break;
                default:
                    return false;
            }

            var relativeBase = (encoding & ApplicationMask) switch
            {
                0x00 => 0UL,
                PcRelative => fieldAddress,
                DataRelative => dataRelativeBase,
                _ => ulong.MaxValue,
            };
            if (relativeBase == ulong.MaxValue || (encoding & Indirect) != 0)
            {
                return false;
            }

            value = isSigned
                ? unchecked((ulong)((long)relativeBase + (long)unsignedValue))
                : checked(relativeBase + unsignedValue);
            return true;
        }

        private static bool TryTake(ReadOnlySpan<byte> bytes, ref int cursor, int length, out ReadOnlySpan<byte> value)
        {
            if (cursor < 0 || length < 0 || cursor > bytes.Length - length)
            {
                value = default;
                return false;
            }

            value = bytes.Slice(cursor, length);
            cursor += length;
            return true;
        }
    }
}

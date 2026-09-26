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
    private const int EstimatedTrampolineBytesPerSite = 48;
    private const ulong PageSize = 0x1000;
    private const ulong AllocationAlignment = 0x10000;
    private const ulong MaximumRelativeJumpDistance = 0x7FFF_FFFF;

    internal enum SpanRefusal
    {
        None,
        ControlFlow,
        BranchTargetAfter,
        StackAfter,
        TooShort,
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

        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return default;
        }

        var protectRedZone = !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RED_ZONE_PATCH"), "1", StringComparison.Ordinal);
        var splitVectorStores = RosettaVectorStorePatch.IsRequired;
        if (!protectRedZone && !splitVectorStores)
        {
            return default;
        }

        var hostName = OperatingSystem.IsWindows() ? "Windows" : "macOS";

        if (!TryDecodeFunctionStarts(memory, programHeaders, imageBase, out var functionStarts))
        {
            Console.Error.WriteLine($"[LOADER][WARN] {hostName} red-zone patch skipped: no usable .eh_frame_hdr table.");
            return default;
        }

        var sites = CollectPatchSites(memory, programHeaders, imageBase, functionStarts, protectRedZone, splitVectorStores, out var scan);
        if (sites.Count == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER] {hostName} red-zone scan: functions={scan.Functions} red_zone={scan.RedZoneFunctions} " +
                $"sites=0 unrelocatable={scan.UnrelocatableSites}.");
            return scan;
        }

        var requiredBytes = AlignUp(
            checked((ulong)sites.Count * (splitVectorStores ? 80UL : EstimatedTrampolineBytesPerSite) + PageSize),
            PageSize);
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
        var previousEnd = 0UL;
        foreach (var site in sites)
        {
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

            if (!TryPatchSite(memory, site, ref trampolineCursor, trampolineEnd, splitVectorStores))
            {
                failed++;
                continue;
            }

            previousEnd = site.Address + (ulong)site.ByteLength;
            patched++;
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
            TrampolineBytes = trampolineCursor - trampolineBase,
        };
        Console.Error.WriteLine(
            $"[LOADER] {hostName} red-zone patch: functions={result.Functions} red_zone={result.RedZoneFunctions} " +
            $"sites={result.PatchedSites}/{result.CandidateSites} failed={result.FailedSites} " +
            $"unrelocatable={result.UnrelocatableSites} " +
            $"(control_flow={result.ControlFlowRefusals} branch_target_after={result.BranchTargetRefusals} " +
            $"stack_after={result.StackRefusals} too_short={result.TooShortRefusals}) " +
            $"rosetta_vector_stores={result.VectorStoreCount} " +
            $"trampolines=0x{trampolineBase:X16}+0x{result.TrampolineBytes:X}.");
        return result;
    }

    private static List<PatchSite> CollectPatchSites(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        IReadOnlyList<ulong> functionStarts,
        bool protectRedZone,
        bool splitVectorStores,
        out PatchResult result)
    {
        var sites = new List<PatchSite>();
        var functionCount = 0;
        var redZoneFunctionCount = 0;
        var instructionCount = 0;
        var vectorStoreCount = 0;
        var unrelocatableSites = 0;
        var refusalCounts = new int[5];

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
                var usesRedZone = protectRedZone && decoded.Any(static entry => UsesRedZone(entry.Instruction));

                if (!usesRedZone && !splitVectorStores)
                {
                    continue;
                }

                if (usesRedZone)
                {
                    redZoneFunctionCount++;
                }
                var branchTargets = CollectBranchTargets(decoded);

                var lastSiteEnd = 0UL;

                for (var instructionIndex = 0; instructionIndex < decoded.Count; instructionIndex++)
                {
                    var instruction = decoded[instructionIndex].Instruction;
                    if ((!usesRedZone && !(splitVectorStores && RosettaVectorStorePatch.RequiresStoreSplit(instruction))) ||
                        !IsFaultableGuestMemoryInstruction(instruction))
                    {
                        continue;
                    }

                    // A backward span reaches instructions that an earlier site
                    // may already have replaced with its own jump. Two jumps
                    // written over each other corrupt control flow, so a span is
                    // only usable when it starts at or after the end of the last
                    // one. Forward spans cannot overlap by construction; the
                    // check costs nothing and documents the invariant.
                    var backward = false;
                    if (!TryBuildPatchSpan(decoded, instructionIndex, branchTargets, out var span, out var refusal))
                    {
                        backward = TryBuildEnclosingPatchSpan(decoded, instructionIndex, branchTargets, out span);
                    }

                    if ((!backward && refusal != SpanRefusal.None) || span.Address < lastSiteEnd)
                    {
                        // Refused spans never enter the site list, so they are
                        // invisible in FailedSites. Count them separately.
                        unrelocatableSites++;
                        refusalCounts[(int)refusal]++;
                        continue;
                    }

                    sites.Add(span);
                    lastSiteEnd = span.Address + (ulong)span.ByteLength;
                    if (splitVectorStores)
                    {
                        vectorStoreCount += span.Instructions.Count(static instruction => RosettaVectorStorePatch.RequiresStoreSplit(instruction));
                    }

                    // A backward span already ends at the current instruction, so
                    // only a forward one consumes the instructions that follow.
                    if (!backward)
                    {
                        instructionIndex += span.Instructions.Count - 1;
                    }
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

    private static HashSet<ulong> CollectBranchTargets(IReadOnlyList<DecodedInstruction> instructions)
    {
        var targets = new HashSet<ulong>();
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

        return targets;
    }

    /// <summary>
    /// Runs the enclosing-span search over a raw instruction range, so the
    /// behaviour can be pinned on the byte sequences that occur in real guest
    /// code rather than on a mocked decode.
    /// </summary>
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

        if (!TryBuildEnclosingPatchSpan(decoded, faultIndex, CollectBranchTargets(decoded), out var site))
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
            if (!IsFaultableGuestMemoryInstruction(instructions[index]))
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
            coreCount = 0;
            return false;
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
        out PatchSite site) =>
        TryBuildPatchSpan(decoded, startIndex, branchTargets, out site, out _);

    private static bool TryBuildPatchSpan(
        IReadOnlyList<DecodedInstruction> decoded,
        int startIndex,
        IReadOnlySet<ulong> branchTargets,
        out PatchSite site,
        out SpanRefusal refusal)
    {
        refusal = SpanRefusal.None;
        var instructions = new List<Instruction>(3);
        var byteLength = 0;
        for (var index = startIndex; index < decoded.Count && byteLength < MinimumJumpBytes; index++)
        {
            var instruction = decoded[index].Instruction;
            if (instruction.FlowControl != FlowControl.Next)
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

            if (refusal != SpanRefusal.None)
            {
                site = default;
                return false;
            }

            instructions.Add(instruction);
            byteLength += instruction.Length;
        }

        if (byteLength < MinimumJumpBytes)
        {
            refusal = SpanRefusal.TooShort;
            site = default;
            return false;
        }

        if (!TryComputeShiftedCore(instructions, out var coreStart, out var coreCount))
        {
            refusal = SpanRefusal.TooShort;
            site = default;
            return false;
        }

        site = new PatchSite(decoded[startIndex].Instruction.IP, byteLength, instructions, coreStart, coreCount);
        return true;
    }

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
        if (UsesStackMemory(instruction) ||
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
        bool splitVectorStores)
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
        var block = new InstructionBlock(writer, instructions, relocatedAddress);
        if (!BlockEncoder.TryEncode(64, block, out _, out _, BlockEncoderOptions.None))
        {
            return false;
        }

        var relocated = writer.ToArray();
        var trampolineLength = checked(prefixLength + 5 + relocated.Length + 8 + 5);
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
        var returnJumpOffset = restoreOffset + 8;
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
        var maximumSite = sites.Max(static site => site.Address);
        var alignedImageEnd = AlignUp(imageBase + imageSize, AllocationAlignment);
        var candidates = new List<ulong>(34) { alignedImageEnd };
        for (var step = 1UL; step <= 16; step++)
        {
            var distance = step * 0x0200_0000UL;
            if (imageBase > distance + requiredBytes)
            {
                candidates.Add(AlignDown(imageBase - distance - requiredBytes, AllocationAlignment));
            }

            if (alignedImageEnd <= ulong.MaxValue - distance)
            {
                candidates.Add(AlignUp(alignedImageEnd + distance, AllocationAlignment));
            }
        }

        foreach (var candidate in candidates)
        {
            if (!CanReach(candidate, minimumSite) ||
                !CanReach(candidate + requiredBytes - 1, maximumSite))
            {
                continue;
            }

            try
            {
                address = memory.AllocateAt(candidate, requiredBytes, executable: true, allowAlternative: false);
                if (address == candidate)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Try another address inside the relative-jump window.
            }
        }

        address = 0;
        return false;
    }

    private static bool CanReach(ulong left, ulong right)
    {
        var distance = left >= right ? left - right : right - left;
        return distance <= MaximumRelativeJumpDistance;
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
        int CoreCount);

    internal readonly record struct PatchResult
    {
        public int Functions { get; init; }

        public int RedZoneFunctions { get; init; }

        public int Instructions { get; init; }

        public int CandidateSites { get; init; }

        public int VectorStoreCount { get; init; }

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

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Protects the System V AMD64 red zone when guest code runs on Windows.
/// </summary>
/// <remarks>
/// Windows can use memory below RSP while it dispatches an exception. Guest code
/// follows the System V ABI and can keep live values in the 128 bytes below RSP.
/// A guest-memory access that faults for write tracking can therefore corrupt a
/// live guest value before the vectored exception handler resumes the instruction.
///
/// This pass finds functions that use the red zone and moves their ordinary
/// memory instructions into small trampolines. Each trampoline shifts RSP by 128
/// bytes only while the memory instruction runs. Windows then uses the temporary
/// area if that instruction faults, while the guest red zone remains intact.
/// </remarks>
internal static class WindowsGuestRedZonePatcher
{
    private const int GuestRedZoneBytes = 128;
    private const int MinimumJumpBytes = 5;
    private const int EstimatedTrampolineBytesPerSite = 48;
    private const ulong PageSize = 0x1000;
    private const ulong AllocationAlignment = 0x10000;
    private const ulong MaximumRelativeJumpDistance = 0x7FFF_FFFF;

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

        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_RED_ZONE_PATCH"), "1", StringComparison.Ordinal))
        {
            return default;
        }

        if (!TryDecodeFunctionStarts(memory, programHeaders, imageBase, out var functionStarts))
        {
            Console.Error.WriteLine("[LOADER][WARN] Windows red-zone patch skipped: no usable .eh_frame_hdr table.");
            return default;
        }

        var sites = CollectPatchSites(memory, programHeaders, imageBase, functionStarts, out var scan);
        if (sites.Count == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER] Windows red-zone scan: functions={scan.Functions} red_zone={scan.RedZoneFunctions} sites=0.");
            return scan;
        }

        var requiredBytes = AlignUp(
            checked((ulong)sites.Count * EstimatedTrampolineBytesPerSite + PageSize),
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
                $"[LOADER][WARN] Windows red-zone patch skipped: no nearby trampoline range for {sites.Count} sites.");
            return scan with { FailedSites = sites.Count };
        }

        var trampolineCursor = trampolineBase;
        var trampolineEnd = trampolineBase + requiredBytes;
        var patched = 0;
        var failed = 0;
        foreach (var site in sites)
        {
            if (!TryPatchSite(memory, site, ref trampolineCursor, trampolineEnd))
            {
                failed++;
                continue;
            }

            patched++;
        }

        var result = scan with
        {
            CandidateSites = sites.Count,
            PatchedSites = patched,
            FailedSites = failed,
            TrampolineBytes = trampolineCursor - trampolineBase,
        };
        Console.Error.WriteLine(
            $"[LOADER] Windows red-zone patch: functions={result.Functions} red_zone={result.RedZoneFunctions} " +
            $"sites={result.PatchedSites}/{result.CandidateSites} failed={result.FailedSites} " +
            $"trampolines=0x{trampolineBase:X16}+0x{result.TrampolineBytes:X}.");
        return result;
    }

    private static List<PatchSite> CollectPatchSites(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        IReadOnlyList<ulong> functionStarts,
        out PatchResult result)
    {
        var sites = new List<PatchSite>();
        var functionCount = 0;
        var redZoneFunctionCount = 0;
        var instructionCount = 0;

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
                if (!decoded.Any(static entry => UsesRedZone(entry.Instruction)))
                {
                    continue;
                }

                redZoneFunctionCount++;
                var branchTargets = CollectBranchTargets(decoded);
                for (var instructionIndex = 0; instructionIndex < decoded.Count; instructionIndex++)
                {
                    if (!IsFaultableGuestMemoryInstruction(decoded[instructionIndex].Instruction) ||
                        !TryBuildPatchSpan(decoded, instructionIndex, branchTargets, out var span))
                    {
                        continue;
                    }

                    sites.Add(span);
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

    private static bool TryBuildPatchSpan(
        IReadOnlyList<DecodedInstruction> decoded,
        int startIndex,
        IReadOnlySet<ulong> branchTargets,
        out PatchSite site)
    {
        var instructions = new List<Instruction>(3);
        var byteLength = 0;
        for (var index = startIndex; index < decoded.Count && byteLength < MinimumJumpBytes; index++)
        {
            var instruction = decoded[index].Instruction;
            if (instruction.FlowControl != FlowControl.Next ||
                (index != startIndex && branchTargets.Contains(instruction.IP)) ||
                TouchesStackPointer(instruction))
            {
                site = default;
                return false;
            }

            instructions.Add(instruction);
            byteLength += instruction.Length;
        }

        if (byteLength < MinimumJumpBytes)
        {
            site = default;
            return false;
        }

        site = new PatchSite(decoded[startIndex].Instruction.IP, byteLength, instructions);
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

    private static bool TryPatchSite(
        IVirtualMemory memory,
        PatchSite site,
        ref ulong trampolineCursor,
        ulong trampolineEnd)
    {
        var writer = new ListCodeWriter();
        var relocatedAddress = trampolineCursor + 5;
        var block = new InstructionBlock(writer, site.Instructions, relocatedAddress);
        if (!BlockEncoder.TryEncode(64, block, out _, out _, BlockEncoderOptions.None))
        {
            return false;
        }

        var relocated = writer.ToArray();
        var trampolineLength = checked(5 + relocated.Length + 8 + 5);
        if (trampolineCursor > trampolineEnd || (ulong)trampolineLength > trampolineEnd - trampolineCursor)
        {
            return false;
        }

        var trampoline = new byte[trampolineLength];
        // lea rsp,[rsp-128] -- LEA preserves guest flags.
        trampoline[0] = 0x48;
        trampoline[1] = 0x8D;
        trampoline[2] = 0x64;
        trampoline[3] = 0x24;
        trampoline[4] = 0x80;
        relocated.CopyTo(trampoline, 5);
        var restoreOffset = 5 + relocated.Length;
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

    private readonly record struct PatchSite(ulong Address, int ByteLength, IList<Instruction> Instructions);

    internal readonly record struct PatchResult
    {
        public int Functions { get; init; }

        public int RedZoneFunctions { get; init; }

        public int Instructions { get; init; }

        public int CandidateSites { get; init; }

        public int PatchedSites { get; init; }

        public int FailedSites { get; init; }

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

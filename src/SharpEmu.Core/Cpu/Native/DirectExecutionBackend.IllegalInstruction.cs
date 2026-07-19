// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Threading;
using Iced.Intel;
using SharpEmu.Core.Cpu.Emulation;

namespace SharpEmu.Core.Cpu.Native;

// Software fallback for unsupported guest CPU instructions.
//
// Guest code runs natively, so the guest and host share the same virtual address space and the
// same registers (the OS delivers them in the CONTEXT record on a fault). When the host CPU lacks
// one of these extensions it raises #UD instead of executing the opcode; without this the title
// simply aborts. Here we decode the faulting instruction, evaluate it against the trapped register
// and memory state, write the result back into the CONTEXT, step RIP past the instruction and ask
// the OS to continue. BMI1/BMI2/ABM GPR operations and the Intel SHA-1 XMM operations are handled;
// anything else falls through to the existing diagnostics unchanged.
public sealed partial class DirectExecutionBackend
{
    // Windows x64 CONTEXT.EFlags lives just past the segment selectors. The GPR offsets it shares
    // with the rest of the backend are the CTX_* constants declared in DirectExecutionBackend.cs.
    private const int CTX_EFLAGS = 68;
    private const int CTX_XMM0 = 0x1A0;

    // STATUS_ILLEGAL_INSTRUCTION (#UD surfaced by the Windows vectored handler).
    private const uint StatusIllegalInstruction = 0xC000001Du;

    private const int MaxInstructionBytes = 15;

    // Instruction-window sizes tried in turn so a fault near a page boundary still decodes.
    private static readonly int[] DecodeWindowSizes = { MaxInstructionBytes, 11, 8, 4, 2 };

    private static int _bmiSoftwareFallbackAnnounced;
    private static long _bmiInstructionsEmulated;
    private static int _sha1SoftwareFallbackAnnounced;
    private static long _sha1InstructionsEmulated;

    private unsafe bool TryRecoverIllegalInstruction(void* contextRecord, ulong rip)
    {
        if (!TryReadFaultingInstruction(rip, out var instruction))
        {
            return false;
        }

        if (TryRecoverSha1Instruction(contextRecord, rip, in instruction))
        {
            return true;
        }

        if (instruction.Op0Kind != OpKind.Register ||
            !TryGetGprSlot(instruction.Op0Register, out var destOffset, out var size))
        {
            return false;
        }

        if (!TryEvaluate(contextRecord, in instruction, size, out var result, out var flagsChanged, out var eflags))
        {
            return false;
        }

        WriteCtxU64(contextRecord, destOffset, result);
        if (flagsChanged)
        {
            WriteCtxU32(contextRecord, CTX_EFLAGS, eflags);
        }

        WriteCtxU64(contextRecord, CTX_RIP, rip + (ulong)instruction.Length);

        Interlocked.Increment(ref _bmiInstructionsEmulated);
        if (Interlocked.Exchange(ref _bmiSoftwareFallbackAnnounced, 1) == 0)
        {
            Console.Error.WriteLine(
                "[LOADER][INFO] Host lacks a BMI/ABM extension used by the guest; " +
                "emulating those instructions in software.");
        }

        return true;
    }

    private unsafe bool TryRecoverSha1Instruction(
        void* contextRecord,
        ulong rip,
        in Instruction instruction)
    {
        if ((!OperatingSystem.IsWindows() && !_posixVectorContextAvailable) ||
            instruction.Mnemonic is not (Mnemonic.Sha1msg1 or Mnemonic.Sha1msg2 or
            Mnemonic.Sha1nexte or Mnemonic.Sha1rnds4) ||
            instruction.Op0Kind != OpKind.Register ||
            !TryGetXmmIndex(instruction.Op0Register, out var destinationIndex) ||
            !TryReadSha1VectorOperand(contextRecord, in instruction, 1, out var source))
        {
            return false;
        }

        var destination = ReadXmm(contextRecord, destinationIndex);
        Sha1Vector result;
        switch (instruction.Mnemonic)
        {
            case Mnemonic.Sha1msg1:
                result = Sha1InstructionEmulator.MessageSchedule1(destination, source);
                break;
            case Mnemonic.Sha1msg2:
                result = Sha1InstructionEmulator.MessageSchedule2(destination, source);
                break;
            case Mnemonic.Sha1nexte:
                result = Sha1InstructionEmulator.NextE(destination, source);
                break;
            case Mnemonic.Sha1rnds4:
                if (instruction.Op2Kind != OpKind.Immediate8)
                {
                    return false;
                }

                result = Sha1InstructionEmulator.FourRounds(destination, source, instruction.Immediate8);
                break;
            default:
                return false;
        }

        WriteXmm(contextRecord, destinationIndex, result);
        WriteCtxU64(contextRecord, CTX_RIP, rip + (ulong)instruction.Length);

        if (!_posixSignalWarmup)
        {
            Interlocked.Increment(ref _sha1InstructionsEmulated);
            if (Interlocked.Exchange(ref _sha1SoftwareFallbackAnnounced, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] Host lacks Intel SHA instructions used by the guest; " +
                    "emulating SHA-1 instructions in software.");
            }
        }

        return true;
    }

    private unsafe bool TryReadSha1VectorOperand(
        void* contextRecord,
        in Instruction instruction,
        int operandIndex,
        out Sha1Vector value)
    {
        switch (instruction.GetOpKind(operandIndex))
        {
            case OpKind.Register:
                if (TryGetXmmIndex(instruction.GetOpRegister(operandIndex), out var sourceIndex))
                {
                    value = ReadXmm(contextRecord, sourceIndex);
                    return true;
                }
                break;

            case OpKind.Memory:
                if (TryComputeMemoryAddress(contextRecord, in instruction, out var address))
                {
                    Span<byte> bytes = stackalloc byte[16];
                    if (TryReadHostBytes(address, bytes))
                    {
                        value = new Sha1Vector(
                            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0, 4)),
                            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)),
                            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4)),
                            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4)));
                        return true;
                    }
                }
                break;
        }

        value = default;
        return false;
    }

    private static unsafe Sha1Vector ReadXmm(void* contextRecord, int index)
    {
        uint* lanes = (uint*)((byte*)contextRecord + CTX_XMM0 + index * 16);
        return new Sha1Vector(lanes[0], lanes[1], lanes[2], lanes[3]);
    }

    private static unsafe void WriteXmm(void* contextRecord, int index, Sha1Vector value)
    {
        uint* lanes = (uint*)((byte*)contextRecord + CTX_XMM0 + index * 16);
        lanes[0] = value.Lane0;
        lanes[1] = value.Lane1;
        lanes[2] = value.Lane2;
        lanes[3] = value.Lane3;
    }

    private static bool TryGetXmmIndex(Register register, out int index)
    {
        switch (register)
        {
            case Register.XMM0: index = 0; return true;
            case Register.XMM1: index = 1; return true;
            case Register.XMM2: index = 2; return true;
            case Register.XMM3: index = 3; return true;
            case Register.XMM4: index = 4; return true;
            case Register.XMM5: index = 5; return true;
            case Register.XMM6: index = 6; return true;
            case Register.XMM7: index = 7; return true;
            case Register.XMM8: index = 8; return true;
            case Register.XMM9: index = 9; return true;
            case Register.XMM10: index = 10; return true;
            case Register.XMM11: index = 11; return true;
            case Register.XMM12: index = 12; return true;
            case Register.XMM13: index = 13; return true;
            case Register.XMM14: index = 14; return true;
            case Register.XMM15: index = 15; return true;
            default: index = 0; return false;
        }
    }

    private unsafe bool TryEvaluate(
        void* contextRecord,
        in Instruction instruction,
        GprOperandSize size,
        out ulong result,
        out bool flagsChanged,
        out uint eflags)
    {
        result = 0;
        flagsChanged = false;
        eflags = ReadCtxU32(contextRecord, CTX_EFLAGS);

        switch (instruction.Mnemonic)
        {
            case Mnemonic.Andn:
                if (!TryReadOperand(contextRecord, in instruction, 1, size, out var andnSrc1) ||
                    !TryReadOperand(contextRecord, in instruction, 2, size, out var andnSrc2))
                {
                    return false;
                }

                result = BmiInstructionEmulator.Andn(andnSrc1, andnSrc2, size, ref eflags);
                flagsChanged = true;
                return true;

            case Mnemonic.Blsi:
            case Mnemonic.Blsmsk:
            case Mnemonic.Blsr:
                if (!TryReadOperand(contextRecord, in instruction, 1, size, out var blsSrc))
                {
                    return false;
                }

                result = instruction.Mnemonic switch
                {
                    Mnemonic.Blsi => BmiInstructionEmulator.Blsi(blsSrc, size, ref eflags),
                    Mnemonic.Blsmsk => BmiInstructionEmulator.Blsmsk(blsSrc, size, ref eflags),
                    _ => BmiInstructionEmulator.Blsr(blsSrc, size, ref eflags),
                };
                flagsChanged = true;
                return true;

            case Mnemonic.Bextr:
                if (!TryReadOperand(contextRecord, in instruction, 1, size, out var bextrSrc) ||
                    !TryReadOperand(contextRecord, in instruction, 2, size, out var bextrControl))
                {
                    return false;
                }

                result = BmiInstructionEmulator.Bextr(bextrSrc, bextrControl, size, ref eflags);
                flagsChanged = true;
                return true;

            case Mnemonic.Bzhi:
                if (!TryReadOperand(contextRecord, in instruction, 1, size, out var bzhiSrc) ||
                    !TryReadOperand(contextRecord, in instruction, 2, size, out var bzhiIndex))
                {
                    return false;
                }

                result = BmiInstructionEmulator.Bzhi(bzhiSrc, bzhiIndex, size, ref eflags);
                flagsChanged = true;
                return true;

            case Mnemonic.Tzcnt:
            case Mnemonic.Lzcnt:
                if (!TryReadOperand(contextRecord, in instruction, 1, size, out var cntSrc))
                {
                    return false;
                }

                result = instruction.Mnemonic == Mnemonic.Tzcnt
                    ? BmiInstructionEmulator.Tzcnt(cntSrc, size, ref eflags)
                    : BmiInstructionEmulator.Lzcnt(cntSrc, size, ref eflags);
                flagsChanged = true;
                return true;

            case Mnemonic.Rorx:
                if (instruction.Op2Kind != OpKind.Immediate8 ||
                    !TryReadOperand(contextRecord, in instruction, 1, size, out var rorxSrc))
                {
                    return false;
                }

                result = BmiInstructionEmulator.Rorx(rorxSrc, instruction.Immediate8, size);
                return true;

            case Mnemonic.Sarx:
            case Mnemonic.Shlx:
            case Mnemonic.Shrx:
                if (!TryReadOperand(contextRecord, in instruction, 1, size, out var shiftSrc) ||
                    !TryReadOperand(contextRecord, in instruction, 2, size, out var shiftCount))
                {
                    return false;
                }

                result = instruction.Mnemonic switch
                {
                    Mnemonic.Sarx => BmiInstructionEmulator.Sarx(shiftSrc, (int)shiftCount, size),
                    Mnemonic.Shlx => BmiInstructionEmulator.Shlx(shiftSrc, (int)shiftCount, size),
                    _ => BmiInstructionEmulator.Shrx(shiftSrc, (int)shiftCount, size),
                };
                return true;

            case Mnemonic.Pdep:
            case Mnemonic.Pext:
                if (!TryReadOperand(contextRecord, in instruction, 1, size, out var packSrc) ||
                    !TryReadOperand(contextRecord, in instruction, 2, size, out var packMask))
                {
                    return false;
                }

                result = instruction.Mnemonic == Mnemonic.Pdep
                    ? BmiInstructionEmulator.Pdep(packSrc, packMask, size)
                    : BmiInstructionEmulator.Pext(packSrc, packMask, size);
                return true;

            default:
                return false;
        }
    }

    // A managed allocation inside a signal handler is unsafe: the fault can
    // interrupt the GC mid-operation, and re-entering the allocator corrupts
    // the thread's allocation context (observed as a hard "Invalid Program" at
    // the next managed alloc, amplified by tight SHA-1 recovery loops). These
    // thread-static objects are created once per thread and reused so the
    // recovery path never allocates on the hot path.
    [ThreadStatic]
    private static byte[]? _decodeBuffer;
    [ThreadStatic]
    private static ByteArrayCodeReader? _decodeReader;
    [ThreadStatic]
    private static Decoder? _decoder;

    private unsafe bool TryReadFaultingInstruction(ulong rip, out Instruction instruction)
    {
        var buffer = _decodeBuffer ??= new byte[MaxInstructionBytes];
        var reader = _decodeReader ??= new ByteArrayCodeReader(buffer);
        var decoder = _decoder ??= Decoder.Create(64, reader);

        // Try the full instruction window first, then shrink so a fault near the end of a mapped
        // page (where fewer than 15 bytes are readable) still decodes. The buffer is reused, so
        // clear the tail past the readable window to keep decoding deterministic.
        foreach (var attempt in DecodeWindowSizes)
        {
            if (!TryReadHostBytes(rip, buffer.AsSpan(0, attempt)))
            {
                continue;
            }

            buffer.AsSpan(attempt).Clear();
            reader.Position = 0;
            decoder.IP = rip;
            decoder.Decode(out instruction);
            if (instruction.Code != Code.INVALID && instruction.Length > 0 && instruction.Length <= attempt)
            {
                return true;
            }
        }

        instruction = default;
        return false;
    }

    private unsafe bool TryReadOperand(
        void* contextRecord,
        in Instruction instruction,
        int operandIndex,
        GprOperandSize size,
        out ulong value)
    {
        value = 0;
        switch (instruction.GetOpKind(operandIndex))
        {
            case OpKind.Register:
                if (!TryGetGprSlot(instruction.GetOpRegister(operandIndex), out var offset, out _))
                {
                    return false;
                }

                var raw = ReadCtxU64(contextRecord, offset);
                value = size == GprOperandSize.Bits64 ? raw : raw & 0xFFFF_FFFFUL;
                return true;

            case OpKind.Memory:
                if (!TryComputeMemoryAddress(contextRecord, in instruction, out var address))
                {
                    return false;
                }

                var byteCount = size == GprOperandSize.Bits64 ? 8 : 4;
                Span<byte> buffer = stackalloc byte[8];
                if (!TryReadHostBytes(address, buffer[..byteCount]))
                {
                    return false;
                }

                value = byteCount == 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(buffer)
                    : BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                return true;

            default:
                return false;
        }
    }

    private unsafe bool TryComputeMemoryAddress(void* contextRecord, in Instruction instruction, out ulong address)
    {
        address = 0;

        // FS/GS-relative operands need the guest segment base, which is not modelled here.
        if (instruction.SegmentPrefix != Register.None)
        {
            return false;
        }

        if (instruction.IsIPRelativeMemoryOperand)
        {
            address = instruction.IPRelativeMemoryAddress;
            return true;
        }

        var effective = instruction.MemoryDisplacement64;
        if (instruction.MemoryBase != Register.None)
        {
            if (!TryGetGpr64Offset(instruction.MemoryBase, out var baseOffset))
            {
                return false;
            }

            effective += ReadCtxU64(contextRecord, baseOffset);
        }

        if (instruction.MemoryIndex != Register.None)
        {
            if (!TryGetGpr64Offset(instruction.MemoryIndex, out var indexOffset))
            {
                return false;
            }

            effective += ReadCtxU64(contextRecord, indexOffset) * (ulong)instruction.MemoryIndexScale;
        }

        address = effective;
        return true;
    }

    // Maps a 32- or 64-bit GPR to its CONTEXT offset and reports the operand width it implies.
    private static bool TryGetGprSlot(Register register, out int offset, out GprOperandSize size)
    {
        switch (register)
        {
            case Register.EAX: offset = CTX_RAX; size = GprOperandSize.Bits32; return true;
            case Register.ECX: offset = CTX_RCX; size = GprOperandSize.Bits32; return true;
            case Register.EDX: offset = CTX_RDX; size = GprOperandSize.Bits32; return true;
            case Register.EBX: offset = CTX_RBX; size = GprOperandSize.Bits32; return true;
            case Register.ESP: offset = CTX_RSP; size = GprOperandSize.Bits32; return true;
            case Register.EBP: offset = CTX_RBP; size = GprOperandSize.Bits32; return true;
            case Register.ESI: offset = CTX_RSI; size = GprOperandSize.Bits32; return true;
            case Register.EDI: offset = CTX_RDI; size = GprOperandSize.Bits32; return true;
            case Register.R8D: offset = CTX_R8; size = GprOperandSize.Bits32; return true;
            case Register.R9D: offset = CTX_R9; size = GprOperandSize.Bits32; return true;
            case Register.R10D: offset = CTX_R10; size = GprOperandSize.Bits32; return true;
            case Register.R11D: offset = CTX_R11; size = GprOperandSize.Bits32; return true;
            case Register.R12D: offset = CTX_R12; size = GprOperandSize.Bits32; return true;
            case Register.R13D: offset = CTX_R13; size = GprOperandSize.Bits32; return true;
            case Register.R14D: offset = CTX_R14; size = GprOperandSize.Bits32; return true;
            case Register.R15D: offset = CTX_R15; size = GprOperandSize.Bits32; return true;
            default:
                if (TryGetGpr64Offset(register, out offset))
                {
                    size = GprOperandSize.Bits64;
                    return true;
                }

                size = GprOperandSize.Bits32;
                return false;
        }
    }

    private static bool TryGetGpr64Offset(Register register, out int offset)
    {
        switch (register)
        {
            case Register.RAX: offset = CTX_RAX; return true;
            case Register.RCX: offset = CTX_RCX; return true;
            case Register.RDX: offset = CTX_RDX; return true;
            case Register.RBX: offset = CTX_RBX; return true;
            case Register.RSP: offset = CTX_RSP; return true;
            case Register.RBP: offset = CTX_RBP; return true;
            case Register.RSI: offset = CTX_RSI; return true;
            case Register.RDI: offset = CTX_RDI; return true;
            case Register.R8: offset = CTX_R8; return true;
            case Register.R9: offset = CTX_R9; return true;
            case Register.R10: offset = CTX_R10; return true;
            case Register.R11: offset = CTX_R11; return true;
            case Register.R12: offset = CTX_R12; return true;
            case Register.R13: offset = CTX_R13; return true;
            case Register.R14: offset = CTX_R14; return true;
            case Register.R15: offset = CTX_R15; return true;
            default: offset = 0; return false;
        }
    }
}

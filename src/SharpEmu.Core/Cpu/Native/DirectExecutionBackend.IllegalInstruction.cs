// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Threading;
using Iced.Intel;
using SharpEmu.Core.Cpu.Emulation;

namespace SharpEmu.Core.Cpu.Native;

// Software fallback for the BMI1/BMI2/ABM general-purpose-register instructions.
//
// Guest code runs natively, so the guest and host share the same virtual address space and the
// same registers (the OS delivers them in the CONTEXT record on a fault). When the host CPU lacks
// one of these extensions it raises #UD instead of executing the opcode; without this the title
// simply aborts. Here we decode the faulting instruction, evaluate it against the trapped register
// and memory state, write the result back into the CONTEXT, step RIP past the instruction and ask
// the OS to continue. Only the register-only BMI/ABM forms are handled; anything else returns false
// and falls through to the existing diagnostics unchanged, so this can never mis-handle an opcode it
// does not fully model.
public sealed partial class DirectExecutionBackend
{
    // Windows x64 CONTEXT.EFlags lives just past the segment selectors. The GPR offsets it shares
    // with the rest of the backend are the CTX_* constants declared in DirectExecutionBackend.cs.
    private const int CTX_EFLAGS = 68;

    // STATUS_ILLEGAL_INSTRUCTION (#UD surfaced by the Windows vectored handler).
    private const uint StatusIllegalInstruction = 0xC000001Du;

    private const int MaxInstructionBytes = 15;

    // Instruction-window sizes tried in turn so a fault near a page boundary still decodes.
    private static readonly int[] DecodeWindowSizes = { MaxInstructionBytes, 11, 8, 4, 2 };

    private static int _bmiSoftwareFallbackAnnounced;
    private static long _bmiInstructionsEmulated;

    private unsafe bool TryRecoverIllegalInstruction(void* contextRecord, ulong rip)
    {
        if (!TryReadFaultingInstruction(rip, out var instruction))
        {
            return false;
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

    private unsafe bool TryReadFaultingInstruction(ulong rip, out Instruction instruction)
    {
        // Try the full instruction window first, then shrink so a fault near the end of a mapped
        // page (where fewer than 15 bytes are readable) still decodes.
        foreach (var attempt in DecodeWindowSizes)
        {
            var buffer = new byte[attempt];
            if (!TryReadHostBytes(rip, buffer))
            {
                continue;
            }

            var decoder = Decoder.Create(64, new ByteArrayCodeReader(buffer));
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
                var buffer = new byte[byteCount];
                if (!TryReadHostBytes(address, buffer))
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

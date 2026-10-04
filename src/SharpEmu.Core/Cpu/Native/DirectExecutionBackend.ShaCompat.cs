// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Threading;
using Iced.Intel;
using SharpEmu.Core.Cpu.Emulation;

namespace SharpEmu.Core.Cpu.Native;

// The PS5 CPU has the SHA extensions and titles use them without a CPUID check.
// Rosetta 2 does not translate them, so they raise #UD on Apple Silicon hosts.
public sealed partial class DirectExecutionBackend
{
    private static int _shaSoftwareFallbackAnnounced;
    private static long _shaInstructionsEmulated;

    private unsafe bool TryRecoverShaInstruction(void* contextRecord, ulong rip)
    {
        if (!OperatingSystem.IsWindows() && !_posixXmmContextBridged ||
            !TryReadFaultingInstruction(rip, out var instruction))
        {
            return false;
        }

        switch (instruction.Mnemonic)
        {
            case Mnemonic.Sha1rnds4:
            case Mnemonic.Sha1nexte:
            case Mnemonic.Sha1msg1:
            case Mnemonic.Sha1msg2:
            case Mnemonic.Sha256rnds2:
            case Mnemonic.Sha256msg1:
            case Mnemonic.Sha256msg2:
                break;
            default:
                return false;
        }

        if (instruction.GetOpKind(0) != OpKind.Register ||
            !TryGetXmmOffset(instruction.GetOpRegister(0), out var destOffset) ||
            !TryReadXmmOperand(contextRecord, in instruction, 1, out var source2))
        {
            return false;
        }

        var source1 = ReadCtxXmm(contextRecord, destOffset);
        var result = instruction.Mnemonic switch
        {
            Mnemonic.Sha1rnds4 => ShaInstructionEmulator.Sha1Rnds4(source1, source2, instruction.Immediate8),
            Mnemonic.Sha1nexte => ShaInstructionEmulator.Sha1Nexte(source1, source2),
            Mnemonic.Sha1msg1 => ShaInstructionEmulator.Sha1Msg1(source1, source2),
            Mnemonic.Sha1msg2 => ShaInstructionEmulator.Sha1Msg2(source1, source2),
            Mnemonic.Sha256rnds2 => ShaInstructionEmulator.Sha256Rnds2(
                source1, source2, ReadCtxXmm(contextRecord, Win64ContextXmm0Offset)),
            Mnemonic.Sha256msg1 => ShaInstructionEmulator.Sha256Msg1(source1, source2),
            _ => ShaInstructionEmulator.Sha256Msg2(source1, source2),
        };

        WriteCtxU64(contextRecord, destOffset, (ulong)result);
        WriteCtxU64(contextRecord, destOffset + 8, (ulong)(result >> 64));
        WriteCtxU64(contextRecord, CTX_RIP, rip + (ulong)instruction.Length);

        Interlocked.Increment(ref _shaInstructionsEmulated);
        if (Interlocked.Exchange(ref _shaSoftwareFallbackAnnounced, 1) == 0)
        {
            Console.Error.WriteLine(
                "[LOADER][INFO] Host lacks the SHA extensions used by the guest; " +
                "emulating those instructions in software.");
        }

        return true;
    }

    private unsafe bool TryReadXmmOperand(void* contextRecord, in Instruction instruction, int operandIndex, out UInt128 value)
    {
        value = 0;
        switch (instruction.GetOpKind(operandIndex))
        {
            case OpKind.Register:
                if (!TryGetXmmOffset(instruction.GetOpRegister(operandIndex), out var offset))
                {
                    return false;
                }

                value = ReadCtxXmm(contextRecord, offset);
                return true;

            case OpKind.Memory:
                var buffer = new byte[16];
                if (!TryComputeMemoryAddress(contextRecord, in instruction, out var address) ||
                    !TryReadHostBytes(address, buffer))
                {
                    return false;
                }

                value = BinaryPrimitives.ReadUInt128LittleEndian(buffer);
                return true;

            default:
                return false;
        }
    }

    // Map an Iced XMM register to its byte offset in the synthetic Win64 CONTEXT
    // record used by both Windows recovery and the POSIX signal bridge.
    private static bool TryGetXmmOffset(Register register, out int offset)
    {
        switch (register)
        {
            case Register.XMM0: offset = Win64ContextXmm0Offset + 16 * 0; return true;
            case Register.XMM1: offset = Win64ContextXmm0Offset + 16 * 1; return true;
            case Register.XMM2: offset = Win64ContextXmm0Offset + 16 * 2; return true;
            case Register.XMM3: offset = Win64ContextXmm0Offset + 16 * 3; return true;
            case Register.XMM4: offset = Win64ContextXmm0Offset + 16 * 4; return true;
            case Register.XMM5: offset = Win64ContextXmm0Offset + 16 * 5; return true;
            case Register.XMM6: offset = Win64ContextXmm0Offset + 16 * 6; return true;
            case Register.XMM7: offset = Win64ContextXmm0Offset + 16 * 7; return true;
            case Register.XMM8: offset = Win64ContextXmm0Offset + 16 * 8; return true;
            case Register.XMM9: offset = Win64ContextXmm0Offset + 16 * 9; return true;
            case Register.XMM10: offset = Win64ContextXmm0Offset + 16 * 10; return true;
            case Register.XMM11: offset = Win64ContextXmm0Offset + 16 * 11; return true;
            case Register.XMM12: offset = Win64ContextXmm0Offset + 16 * 12; return true;
            case Register.XMM13: offset = Win64ContextXmm0Offset + 16 * 13; return true;
            case Register.XMM14: offset = Win64ContextXmm0Offset + 16 * 14; return true;
            case Register.XMM15: offset = Win64ContextXmm0Offset + 16 * 15; return true;
            default:
                offset = 0;
                return false;
        }
    }

    private static unsafe UInt128 ReadCtxXmm(void* contextRecord, int offset) =>
        new(ReadCtxU64(contextRecord, offset + 8), ReadCtxU64(contextRecord, offset));
}

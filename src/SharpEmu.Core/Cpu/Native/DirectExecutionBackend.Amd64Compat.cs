// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using Iced.Intel;
using SharpEmu.Core.Cpu.Emulation;

namespace SharpEmu.Core.Cpu.Native;

// General software fallback for the AMD-only instructions PS5 titles occasionally emit that a
// Zen 2-only host implements but Intel hosts (and Rosetta 2 on Apple Silicon) do not:
//   - SSE4a EXTRQ/INSERTQ, both the immediate and the register forms
//   - MONITORX/MWAITX
//
// The SSE4a bit math and MONITORX/MWAITX handling follow Kyty's
// Loader::X64InstructionEmulator (TryEmulateSse4a / TryEmulateMonitorxMwaitx); the register
// forms (66 0F 79 /r and F2 0F 79 /r, which take their length/index from a register rather than
// from immediates) are not in Kyty and are decoded here from the AMD64 definition.
//
// SharpEmu already special-cases exactly one compiled EXTRQ+VPBLENDD byte sequence at load time
// (Sse4aExtrqBlendPatch), which only helps the one idiom it was reverse-engineered from. This
// file is a general, fault-time fallback that engages for any EXTRQ/INSERTQ or MONITORX/MWAITX
// the narrower patch (or a title using a different compiler/register allocation) does not cover,
// complementing rather than replacing it: the load-time patch still avoids paying the
// fault-and-recover cost on the hot path it was built for, while this method is the safety net
// for everything else.
//
// This is deliberately additive: DirectExecutionBackend.IllegalInstruction.cs (the BMI1/BMI2/ABM
// fallback) is untouched, and this method is only reached from VectoredHandler after that one
// has already declined to handle the fault.
public sealed partial class DirectExecutionBackend
{
    // Byte offset of Xmm0 within the Win64 CONTEXT record: FltSave (the XMM_SAVE_AREA32/FXSAVE
    // image) starts right after Rip at offset 256, and XmmRegisters[0] sits 160 bytes into that
    // area (32-byte header + 8 legacy x87/MMX slots x 16 bytes). 256 + 160 = 416 (0x1A0). Cross-
    // checked against this file's own Win64ContextSize (0x4D0): rebuilding the whole CONTEXT
    // layout field-by-field from offset 0 lands on the same 0x4D0 total, which would not happen
    // if this offset (or anything before it) were wrong.
    private const int Win64ContextXmm0Offset = 0x1A0;

    private static int _sse4aSoftwareFallbackAnnounced;
    private static long _sse4aInstructionsEmulated;
    private static int _monitorxSoftwareFallbackAnnounced;
    private static long _monitorxInstructionsEmulated;

    private unsafe bool TryRecoverAmdCompatInstruction(void* contextRecord, ulong rip)
    {
        if (TryRecoverMonitorxMwaitx(contextRecord, rip))
        {
            return true;
        }

        // MONITORX/MWAITX above only ever reads guest code memory and rewrites RIP, both of
        // which the POSIX signal bridge (DirectExecutionBackend.PosixSignals.cs) faithfully
        // round-trips through the real ucontext, so it works on every supported OS. EXTRQ/
        // INSERTQ additionally read and write an XMM register: on Windows contextRecord is the
        // live CONTEXT the OS resumes the thread from, so touching the Xmm0.. slots is visible
        // to the guest, and on Linux the bridge copies the mcontext's FXSAVE image into the
        // Xmm0.. slots and writes them back through sigreturn (_posixXmmContextBridged). On
        // Darwin the XMM area is still a zeroed scratch buffer - running this there would
        // silently compute a result from stale bytes and then discard whatever it "wrote", so
        // the recovery declines until that bridge exists.
        return (OperatingSystem.IsWindows() || _posixXmmContextBridged) &&
            TryRecoverSse4aExtractInsert(contextRecord, rip);
    }

    private unsafe bool TryRecoverMonitorxMwaitx(void* contextRecord, ulong rip)
    {
        // MONITORX (0F 01 FA) and MWAITX (0F 01 FB) are fixed 3-byte encodings with no
        // ModRM/SIB/displacement/immediate, so a raw byte compare is sufficient and unambiguous.
        var opcode = new byte[3];
        if (!TryReadHostBytes(rip, opcode) ||
            opcode[0] != 0x0F || opcode[1] != 0x01 || (opcode[2] != 0xFA && opcode[2] != 0xFB))
        {
            return false;
        }

        // PS5 titles use this pair in idle/wait loops: MONITORX arms a monitor on a cache line
        // and MWAITX blocks until that line is written (or a timeout elapses). Hosts without
        // the extension raise #UD on either one. We do not model the monitor itself, only its
        // observable effect on guest forward progress: MONITORX becomes a no-op (arming a
        // watch we never honour has no side effect of its own) and MWAITX becomes a plain
        // thread yield, i.e. treat the awaited condition as already satisfied so the guest
        // loop keeps making progress instead of executing an illegal opcode forever.
        if (opcode[2] == 0xFB)
        {
            Thread.Yield();
        }

        WriteCtxU64(contextRecord, CTX_RIP, rip + 3);

        Interlocked.Increment(ref _monitorxInstructionsEmulated);
        if (Interlocked.Exchange(ref _monitorxSoftwareFallbackAnnounced, 1) == 0)
        {
            Console.Error.WriteLine(
                "[LOADER][INFO] Host lacks AMD MONITORX/MWAITX used by the guest; " +
                "emulating those instructions in software.");
        }

        return true;
    }

    private unsafe bool TryRecoverSse4aExtractInsert(void* contextRecord, ulong rip)
    {
        if (!OperatingSystem.IsWindows() && !_posixXmmContextBridged ||
            !TryReadFaultingInstruction(rip, out var instruction))
        {
            return false;
        }

        if (instruction.GetOpKind(0) != OpKind.Register ||
            !TryGetXmmOffset(instruction.GetOpRegister(0), out var destOffset))
        {
            return false;
        }

        // Every form except the immediate EXTRQ carries a second XMM operand. INSERTQ takes the
        // bits to insert from it; the register forms additionally read their length/index out of
        // it instead of out of immediates.
        var srcOffset = 0;
        if (instruction.Code != Code.Extrq_xmm_imm8_imm8 &&
            (instruction.GetOpKind(1) != OpKind.Register ||
                !TryGetXmmOffset(instruction.GetOpRegister(1), out srcOffset)))
        {
            return false;
        }

        // Read the destination before writing it: the register forms allow the source and
        // destination to be the same XMM, and EXTRQ extracts out of its destination either way.
        var destLow = ReadCtxU64(contextRecord, destOffset);
        int length;
        int index;
        ulong source;
        switch (instruction.Code)
        {
            case Code.Extrq_xmm_imm8_imm8:
                length = (int)instruction.GetImmediate(1);
                index = (int)instruction.GetImmediate(2);
                source = destLow;
                break;

            case Code.Extrq_xmm_xmm:
                Sse4aBitFieldEmulator.DecodeRegisterControl(
                    ReadCtxU64(contextRecord, srcOffset), out length, out index);
                source = destLow;
                break;

            case Code.Insertq_xmm_xmm_imm8_imm8:
                length = (int)instruction.GetImmediate(2);
                index = (int)instruction.GetImmediate(3);
                source = ReadCtxU64(contextRecord, srcOffset);
                break;

            // The one asymmetry between the two register forms: INSERTQ's control fields live in
            // the *high* quadword of the source (xmm2[69:64] length, xmm2[77:72] index) while the
            // bits it inserts still come from the low one. EXTRQ reads both from the low quadword.
            case Code.Insertq_xmm_xmm:
                Sse4aBitFieldEmulator.DecodeRegisterControl(
                    ReadCtxU64(contextRecord, srcOffset + 8), out length, out index);
                source = ReadCtxU64(contextRecord, srcOffset);
                break;

            default:
                return false;
        }

        // AMD leaves the result undefined when the field runs off the end of the quadword. Decline
        // rather than invent one, matching how the BMI fallback refuses anything it does not fully
        // model, so an encoding we mis-read still reaches the existing diagnostics.
        if (!Sse4aBitFieldEmulator.IsValidBitField(length, index))
        {
            return false;
        }

        var isExtract = instruction.Code is Code.Extrq_xmm_imm8_imm8 or Code.Extrq_xmm_xmm;
        WriteCtxU64(
            contextRecord,
            destOffset,
            isExtract
                ? Sse4aBitFieldEmulator.ExtractBitField(source, length, index)
                : Sse4aBitFieldEmulator.InsertBitField(destLow, source, length, index));
        WriteCtxU64(contextRecord, destOffset + 8, 0);

        WriteCtxU64(contextRecord, CTX_RIP, rip + (ulong)instruction.Length);

        Interlocked.Increment(ref _sse4aInstructionsEmulated);
        if (Interlocked.Exchange(ref _sse4aSoftwareFallbackAnnounced, 1) == 0)
        {
            Console.Error.WriteLine(
                "[LOADER][INFO] Host lacks SSE4a EXTRQ/INSERTQ used by the guest; " +
                "emulating those instructions in software.");
        }

        return true;
    }

    // Maps an Iced XMM register to its byte offset in the Win64 CONTEXT record. Written as an
    // explicit switch (rather than arithmetic on the Register enum) to match the style already
    // used by TryGetGprSlot/TryGetGpr64Offset in DirectExecutionBackend.IllegalInstruction.cs.
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
}

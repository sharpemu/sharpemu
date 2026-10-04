// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.Core.Cpu.Emulation;

namespace SharpEmu.Core.Cpu.Native;

// Recover supported instructions only after an illegal-instruction fault.
// Leave native instructions and unrelated register values unchanged.
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
        // Read the faulting bytes once. Both AMD compatibility families are short and fixed;
        // sharing this probe avoids a second VirtualQuery/kernel read on every recovered #UD.
        Span<byte> bytes = stackalloc byte[7];
        var byteCount = bytes.Length;
        while (byteCount >= 3 && !TryReadExecutableBytes(rip, bytes[..byteCount]))
        {
            byteCount--;
        }

        if (byteCount < 3)
        {
            return false;
        }

        var instructionBytes = bytes[..byteCount];
        if (TryRecoverMonitorxMwaitx(contextRecord, rip, instructionBytes))
        {
            return true;
        }

        // EXTRQ and INSERTQ need the current vector register values.
        // Use recovery only when the signal context contains these values.
        return (OperatingSystem.IsWindows() || _posixXmmContextBridged) &&
            TryRecoverSse4aExtractInsert(contextRecord, rip, instructionBytes);
    }

    private unsafe bool TryRecoverMonitorxMwaitx(
        void* contextRecord,
        ulong rip,
        ReadOnlySpan<byte> opcode)
    {
        // MONITORX (0F 01 FA) and MWAITX (0F 01 FB) are fixed 3-byte encodings with no
        // ModRM/SIB/displacement/immediate, so a raw byte compare is sufficient and unambiguous.
        if (opcode.Length < 3 ||
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

    private unsafe bool TryRecoverSse4aExtractInsert(
        void* contextRecord,
        ulong rip,
        ReadOnlySpan<byte> bytes)
    {
        if (!OperatingSystem.IsWindows() && !_posixXmmContextBridged)
        {
            return false;
        }

        // This path runs in an illegal-instruction handler and can execute many times at a
        // hot guest site on Intel hosts. Iced decoding allocated a byte array and decoder for
        // every #UD. Decode the three fixed SSE4a encodings directly, as the architecture
        // defines them, so recovery itself is allocation-free. The staged reads retain the
        // old page-boundary behaviour for a short register-form EXTRQ.
        if (!TryDecodeSse4aInstruction(bytes, out var decoded))
        {
            return false;
        }

        var destOffset = Win64ContextXmm0Offset + decoded.DestinationRegister * 16;
        var destLow = ReadCtxU64(contextRecord, destOffset);
        if (!decoded.IsInsert)
        {
            var length = decoded.Length;
            var index = decoded.Index;
            if (decoded.UsesRegisterControl)
            {
                // AMD defines the register form's field length in xmm2[5:0] and its start
                // index in xmm2[13:8]. Other control bits do not affect the instruction.
                var controlOffset = Win64ContextXmm0Offset + decoded.SourceRegister * 16;
                var control = ReadCtxU64(contextRecord, controlOffset);
                length = (int)(control & 0x3F);
                index = (int)((control >> 8) & 0x3F);
            }

            WriteCtxU64(contextRecord, destOffset, Sse4aBitFieldEmulator.ExtractBitField(destLow, length, index));
            WriteCtxU64(contextRecord, destOffset + 8, 0);
        }
        else
        {
            var srcOffset = Win64ContextXmm0Offset + decoded.SourceRegister * 16;
            WriteCtxU64(contextRecord, destOffset, Sse4aBitFieldEmulator.InsertBitField(
                destLow, ReadCtxU64(contextRecord, srcOffset), decoded.Length, decoded.Index));
            // Unlike EXTRQ, INSERTQ modifies only the low 64-bit quadword. Preserve the
            // destination's upper quadword exactly as the hardware instruction does.
        }

        WriteCtxU64(contextRecord, CTX_RIP, rip + (ulong)decoded.InstructionLength);

        Interlocked.Increment(ref _sse4aInstructionsEmulated);
        if (Interlocked.Exchange(ref _sse4aSoftwareFallbackAnnounced, 1) == 0)
        {
            Console.Error.WriteLine(
                "[LOADER][INFO] Host lacks SSE4a EXTRQ/INSERTQ used by the guest; " +
                "emulating those instructions in software.");
        }

        return true;
    }

    internal static bool TryDecodeSse4aInstruction(
        ReadOnlySpan<byte> bytes,
        out Sse4aDecodedInstruction instruction)
    {
        instruction = default;
        if (bytes.Length < 4 || (bytes[0] != 0x66 && bytes[0] != 0xF2))
        {
            return false;
        }

        var prefix = bytes[0];
        var offset = 1;
        byte rex = 0;
        if (offset < bytes.Length && (bytes[offset] & 0xF0) == 0x40)
        {
            rex = bytes[offset++];
        }

        if (bytes.Length < offset + 3 || bytes[offset] != 0x0F)
        {
            return false;
        }

        var opcode = bytes[offset + 1];
        var modRm = bytes[offset + 2];
        if ((modRm & 0xC0) != 0xC0)
        {
            // EXTRQ/INSERTQ have register operands only. Refuse memory-looking encodings so
            // exception recovery cannot reinterpret an unrelated opcode.
            return false;
        }

        var reg = ((modRm >> 3) & 7) | ((rex & 0x04) << 1);
        var rm = (modRm & 7) | ((rex & 0x01) << 3);
        if (prefix == 0x66 && opcode == 0x79)
        {
            instruction = new Sse4aDecodedInstruction(
                IsInsert: false,
                UsesRegisterControl: true,
                DestinationRegister: reg,
                SourceRegister: rm,
                Length: 0,
                Index: 0,
                InstructionLength: offset + 3);
            return true;
        }

        if (opcode != 0x78 || bytes.Length < offset + 5)
        {
            return false;
        }

        var isInsert = prefix == 0xF2;
        instruction = new Sse4aDecodedInstruction(
            IsInsert: isInsert,
            UsesRegisterControl: false,
            DestinationRegister: isInsert ? reg : rm,
            SourceRegister: rm,
            Length: bytes[offset + 3],
            Index: bytes[offset + 4],
            InstructionLength: offset + 5);
        return true;
    }

    internal readonly record struct Sse4aDecodedInstruction(
        bool IsInsert,
        bool UsesRegisterControl,
        int DestinationRegister,
        int SourceRegister,
        int Length,
        int Index,
        int InstructionLength);
}

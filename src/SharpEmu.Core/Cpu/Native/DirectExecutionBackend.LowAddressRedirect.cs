// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using Iced.Intel;

namespace SharpEmu.Core.Cpu.Native;

// Some IL2CPP-compiled titles (observed: Metal Slug Tactics) emit thread/type-static
// zero-initialization as absolute-address memory accesses against very low
// displacements (0x0, 0x10, 0x18, ...) with no base register, no index register, and
// no FS/GS segment override -- i.e. a literal `mov [0x10], ...`-style encoding rather
// than a TLS-relative `mov fs:[0x10], ...` or a register-relative null-pointer
// dereference. On real hardware this presumably lands in a small reserved low-address
// ABI region. SharpEmu cannot map guest memory there at all: it executes guest code
// directly on the host CPU, so guest addresses are literal host virtual addresses, and
// the host OS refuses to map anything under its minimum mmap address (64 KiB on the
// Linux default, and Windows/macOS restrict the null page similarly) even for a
// privileged emulator process.
//
// Instead of mapping memory, this recognizes the narrow access shape at fault time and
// treats the whole low region as permanently-zero scratch storage: stores are
// discarded, loads read back as zero. The structural check (no base, no index, no
// segment, absolute disp32 below the threshold) is deliberately strict so this can
// never swallow an ordinary null-pointer bug -- those go through register-relative
// addressing (e.g. `[rax+0x18]` with rax=0), which this does not match.
public sealed partial class DirectExecutionBackend
{
    private const ulong LowAddressRedirectLimit = 0x1000;
    private static long _lowAddressRedirectCount;

    private unsafe bool TryRecoverLowAddressAccess(EXCEPTION_RECORD* exceptionRecord, void* contextRecord, ulong rip)
    {
        if (exceptionRecord->NumberParameters < 2)
        {
            return false;
        }

        var target = exceptionRecord->ExceptionInformation[1];
        if (target >= LowAddressRedirectLimit)
        {
            return false;
        }

        if (!TryReadFaultingInstruction(rip, out var instruction))
        {
            return false;
        }

        if (instruction.SegmentPrefix != Register.None ||
            instruction.IsIPRelativeMemoryOperand ||
            instruction.MemoryBase != Register.None ||
            instruction.MemoryIndex != Register.None ||
            instruction.MemoryDisplacement64 != target)
        {
            return false;
        }

        string kind;
        if (instruction.Op0Kind == OpKind.Memory)
        {
            // Store form (memory is the destination operand): the source value is
            // either an immediate or a register the compiler just computed; either
            // way, discarding the write reproduces what a real zero-backed page
            // would observe for this "clear a field" idiom.
            kind = "store";
        }
        else if (instruction.Op0Kind == OpKind.Register && TryGetGprSlot(instruction.Op0Register, out var destOffset, out _))
        {
            // Load form into a general-purpose register: report the permanently-zero
            // contents of the scratch region.
            WriteCtxU64(contextRecord, destOffset, 0);
            kind = "load";
        }
        else
        {
            // XMM/YMM destination loads are not modelled -- this idiom has only ever
            // been observed writing to these addresses, never reading from them.
            return false;
        }

        WriteCtxU64(contextRecord, CTX_RIP, rip + (ulong)instruction.Length);

        var count = Interlocked.Increment(ref _lowAddressRedirectCount);
        if (count <= 16 || (count & (count - 1)) == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] Redirected low-address {kind} #{count}: rip=0x{rip:X16} " +
                $"target=0x{target:X16} ({instruction.Mnemonic}) to permanently-zero scratch storage.");
        }

        return true;
    }
}

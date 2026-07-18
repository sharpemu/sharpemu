// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Debugging;

/// <summary>
/// A live view of the guest CPU state at a dispatch boundary, handed to an
/// <see cref="ICpuDebugHook"/> so a debugger can read and mutate registers and
/// guest memory without taking a dependency on the concrete
/// <c>CpuContext</c>/<c>CpuDispatcher</c> types.
/// </summary>
/// <remarks>
/// The frame instance is only valid for the duration of the hook call that
/// receives it (between <see cref="ICpuDebugHook.OnFrameEnter"/> and the
/// matching <see cref="ICpuDebugHook.OnFrameExit"/>). Reads and writes are
/// forwarded straight to the underlying guest context, so mutations made from
/// a hook are observed by the CPU backend when it resumes the frame.
/// </remarks>
public interface ICpuDebugFrame
{
    /// <summary>The kind of frame being executed.</summary>
    CpuDebugFrameKind Kind { get; }

    /// <summary>The guest ABI generation this frame targets.</summary>
    Generation Generation { get; }

    /// <summary>The guest virtual address the frame begins executing at.</summary>
    ulong EntryPoint { get; }

    /// <summary>
    /// A human-readable label for the frame (process image name or module name).
    /// </summary>
    string Label { get; }

    /// <summary>Guest-addressable memory for this frame.</summary>
    ICpuMemory Memory { get; }

    /// <summary>Reads a general-purpose register.</summary>
    ulong GetRegister(CpuRegister register);

    /// <summary>Overwrites a general-purpose register.</summary>
    void SetRegister(CpuRegister register, ulong value);

    /// <summary>The instruction pointer.</summary>
    ulong Rip { get; set; }

    /// <summary>The flags register.</summary>
    ulong Rflags { get; set; }

    /// <summary>The FS segment base (guest TLS pointer).</summary>
    ulong FsBase { get; }

    /// <summary>The GS segment base.</summary>
    ulong GsBase { get; }

    /// <summary>Reads the 128-bit value of an XMM register.</summary>
    void GetXmm(int registerIndex, out ulong low, out ulong high);

    /// <summary>
    /// The import stubs (guest address to NID) resolved for this frame, so a
    /// debugger can annotate calls into HLE exports.
    /// </summary>
    IReadOnlyDictionary<ulong, string> ImportStubs { get; }
}

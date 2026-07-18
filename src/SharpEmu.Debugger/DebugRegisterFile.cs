// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.HLE;

namespace SharpEmu.Debugger;

/// <summary>
/// An immutable snapshot of the guest integer register state at a stop. XMM/YMM
/// state is intentionally omitted here and read on demand through the target to
/// keep the common register-dump path cheap.
/// </summary>
public readonly struct DebugRegisterFile
{
    private readonly ulong[] _generalPurpose;

    public DebugRegisterFile(
        ulong[] generalPurpose,
        ulong rip,
        ulong rflags,
        ulong fsBase,
        ulong gsBase)
    {
        ArgumentNullException.ThrowIfNull(generalPurpose);
        if (generalPurpose.Length != 16)
        {
            throw new ArgumentException("Expected 16 general-purpose registers.", nameof(generalPurpose));
        }

        _generalPurpose = generalPurpose;
        Rip = rip;
        Rflags = rflags;
        FsBase = fsBase;
        GsBase = gsBase;
    }

    public ulong Rip { get; }

    public ulong Rflags { get; }

    public ulong FsBase { get; }

    public ulong GsBase { get; }

    /// <summary>Reads a register by identifier.</summary>
    public ulong this[DebugRegisterId id] => id switch
    {
        DebugRegisterId.Rip => Rip,
        DebugRegisterId.Rflags => Rflags,
        DebugRegisterId.FsBase => FsBase,
        DebugRegisterId.GsBase => GsBase,
        _ when id.IsGeneralPurpose() => _generalPurpose[(int)id],
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    /// <summary>Reads a general-purpose register.</summary>
    public ulong this[CpuRegister register] => _generalPurpose[(int)register];

    /// <summary>Captures the integer register state of a live debug frame.</summary>
    public static DebugRegisterFile Capture(ICpuDebugFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var gpr = new ulong[16];
        for (var i = 0; i < gpr.Length; i++)
        {
            gpr[i] = frame.GetRegister((CpuRegister)i);
        }

        return new DebugRegisterFile(gpr, frame.Rip, frame.Rflags, frame.FsBase, frame.GsBase);
    }
}

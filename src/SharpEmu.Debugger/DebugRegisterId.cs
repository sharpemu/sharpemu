// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Debugger;

/// <summary>
/// The registers a debugger can name. The first sixteen values line up with
/// <see cref="CpuRegister"/> so a general-purpose register can be converted
/// between the two enums by casting; the remaining values cover the special
/// registers a debug frame exposes.
/// </summary>
public enum DebugRegisterId
{
    Rax = 0,
    Rcx = 1,
    Rdx = 2,
    Rbx = 3,
    Rsp = 4,
    Rbp = 5,
    Rsi = 6,
    Rdi = 7,
    R8 = 8,
    R9 = 9,
    R10 = 10,
    R11 = 11,
    R12 = 12,
    R13 = 13,
    R14 = 14,
    R15 = 15,

    Rip = 16,
    Rflags = 17,
    FsBase = 18,
    GsBase = 19,
}

/// <summary>Helpers for mapping between debug and CPU register identifiers.</summary>
public static class DebugRegisterIdExtensions
{
    /// <summary>
    /// True when the identifier names one of the sixteen general-purpose
    /// registers and can be cast to <see cref="CpuRegister"/>.
    /// </summary>
    public static bool IsGeneralPurpose(this DebugRegisterId id)
        => id is >= DebugRegisterId.Rax and <= DebugRegisterId.R15;

    /// <summary>
    /// Converts a general-purpose identifier to its <see cref="CpuRegister"/>.
    /// Throws when <paramref name="id"/> is a special register.
    /// </summary>
    public static CpuRegister ToCpuRegister(this DebugRegisterId id)
    {
        if (!id.IsGeneralPurpose())
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Not a general-purpose register.");
        }

        return (CpuRegister)(int)id;
    }
}

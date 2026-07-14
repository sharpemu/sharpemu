// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native.Windows;

/// <summary>
/// Windows NTSTATUS exception codes and EXCEPTION_RECORD access-type values the
/// fault handlers filter on. Values are the same numbers the handlers previously
/// compared as bare literals; only the spelling changed.
/// </summary>
internal static class WindowsFaultCodes
{
    public const uint AccessViolation = 0xC0000005u;         // 3221225477
    public const uint Breakpoint = 0x80000003u;              // 2147483651
    public const uint IllegalInstruction = 0xC000001Du;      // 3221225501
    public const uint FastFail = 0xC0000409u;                // 3221226505
    public const uint StackOverflow = 0xC00000FDu;
    public const uint ClrManagedException = 0xE0434352u;

    // EXCEPTION_RECORD.ExceptionInformation[0] for access violations.
    public const ulong AccessRead = 0;
    public const ulong AccessWrite = 1;
    public const ulong AccessExecute = 8;
}

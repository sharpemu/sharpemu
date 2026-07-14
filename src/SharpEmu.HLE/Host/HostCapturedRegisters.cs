// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// General-purpose register snapshot of a suspended thread, produced by
/// <see cref="IHostThreading.TryCaptureThreadRegisters"/>. Registers are named
/// after the guest ISA (x86-64), which every supported host executes natively.
/// </summary>
public readonly record struct HostCapturedRegisters(
    ulong Rip,
    ulong Rsp,
    ulong Rbp,
    ulong Rax,
    ulong Rbx,
    ulong Rcx,
    ulong Rdx);

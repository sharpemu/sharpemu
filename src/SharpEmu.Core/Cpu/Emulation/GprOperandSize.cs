// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Operand width for the emulated general-purpose-register instructions. The numeric value is the
/// bit width, so it can double as the "count leading/trailing zeros of an all-zero source" result.
/// </summary>
public enum GprOperandSize
{
    Bits32 = 32,
    Bits64 = 64,
}

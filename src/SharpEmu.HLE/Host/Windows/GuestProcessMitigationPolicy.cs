// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Windows;

public static class GuestProcessMitigationPolicy
{
    private const ulong ControlFlowGuardAlwaysOff = 2UL << 40;
    private const ulong HighEntropyAddressRandomizationAlwaysOff = 2UL << 20;
    private const ulong UserShadowStacksAlwaysOff = 2UL << 28;
    private const ulong UserContextInstructionPointerValidationAlwaysOff = 2UL << 32;

    // Limit startup address variance without disabling bottom-up randomization.
    public const ulong Primary = ControlFlowGuardAlwaysOff | HighEntropyAddressRandomizationAlwaysOff;

    public const ulong Secondary = UserShadowStacksAlwaysOff | UserContextInstructionPointerValidationAlwaysOff;
}

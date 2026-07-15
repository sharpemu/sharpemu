// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Host-neutral gamepad button flags. Named after the PlayStation layout the guest API
/// exposes, but the numeric values are the seam's own — the HLE pad exports translate
/// them to SCE_PAD_BUTTON bits, so guest ABI values never leak into host backends.
/// </summary>
[Flags]
public enum HostGamepadButtons : uint
{
    None = 0,
    Up = 1 << 0,
    Down = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    Cross = 1 << 4,
    Circle = 1 << 5,
    Square = 1 << 6,
    Triangle = 1 << 7,
    L1 = 1 << 8,
    R1 = 1 << 9,
    L2 = 1 << 10,
    R2 = 1 << 11,
    L3 = 1 << 12,
    R3 = 1 << 13,
    Options = 1 << 14,
    TouchPad = 1 << 15,
}

/// <summary>
/// Snapshot of one host gamepad: sticks are 0..255 with 128 centered and Y growing
/// downward; triggers 0..255. Unmanaged on purpose so per-frame polls can stackalloc
/// snapshot buffers.
/// </summary>
public readonly record struct HostGamepadState(
    bool Connected,
    HostGamepadButtons Buttons,
    byte LeftX,
    byte LeftY,
    byte RightX,
    byte RightY,
    byte LeftTrigger,
    byte RightTrigger);

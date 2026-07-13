// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Snapshot of a physical controller's state, already translated to ORBIS
/// pad conventions (SCE_PAD_BUTTON bits; sticks 0..255 with 128 centered
/// and Y growing downward; triggers 0..255).
/// </summary>
internal readonly record struct PadState(
    bool Connected,
    uint Buttons,
    byte LeftX,
    byte LeftY,
    byte RightX,
    byte RightY,
    byte L2,
    byte R2);

/// <summary>SCE_PAD_BUTTON bit values.</summary>
internal static class OrbisPadButton
{
    internal const uint L3 = 0x0002;
    internal const uint R3 = 0x0004;
    internal const uint Options = 0x0008;
    internal const uint Up = 0x0010;
    internal const uint Right = 0x0020;
    internal const uint Down = 0x0040;
    internal const uint Left = 0x0080;
    internal const uint L2 = 0x0100;
    internal const uint R2 = 0x0200;
    internal const uint L1 = 0x0400;
    internal const uint R1 = 0x0800;
    internal const uint Triangle = 0x1000;
    internal const uint Circle = 0x2000;
    internal const uint Cross = 0x4000;
    internal const uint Square = 0x8000;
    internal const uint TouchPad = 0x100000;
}

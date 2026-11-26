// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Overlay;

public enum OverlayInputScheme
{
    Keyboard,
    PlayStation,
    Xbox,
}

public enum OverlayButton
{
    Confirm,
    Cancel,
    Triangle,
    Square,
    DirectionalPad,
    L1,
    R1,
    L2,
    R2,
    L3,
    R3,
}

public readonly record struct OverlayPrompt(OverlayButton Button, string Label, string KeyLabel);

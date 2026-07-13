// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Pad;

internal enum PadLogicalControl
{
    DpadLeft,
    DpadRight,
    DpadUp,
    DpadDown,
    Cross,
    Circle,
    Square,
    Triangle,
    L1,
    R1,
    L2,
    R2,
    L3,
    R3,
    Options,
    TouchPad,
}

internal enum PadInputBindingKind
{
    KeyboardKey,
    MouseButton,
}

internal enum PadStickSource
{
    Keyboard,
    Mouse,
    ExternalController,
}

internal enum PadStickSide
{
    Left,
    Right,
}

internal sealed class PadInputBinding
{
    public PadInputBindingKind Kind { get; set; } = PadInputBindingKind.KeyboardKey;

    public string Code { get; set; } = string.Empty;

    public static PadInputBinding Key(string code) => new()
    {
        Kind = PadInputBindingKind.KeyboardKey,
        Code = code,
    };

    public static PadInputBinding MouseButton(string code) => new()
    {
        Kind = PadInputBindingKind.MouseButton,
        Code = code,
    };
}

internal sealed class PadButtonMapping
{
    public List<PadInputBinding> Bindings { get; set; } = [];

    public static PadButtonMapping Keys(params string[] keys) => new()
    {
        Bindings = keys.Select(PadInputBinding.Key).ToList(),
    };
}

internal sealed class PadStickMapping
{
    public PadStickSource Source { get; set; } = PadStickSource.Keyboard;

    public string NegativeXKey { get; set; } = string.Empty;

    public string PositiveXKey { get; set; } = string.Empty;

    public string NegativeYKey { get; set; } = string.Empty;

    public string PositiveYKey { get; set; } = string.Empty;

    public double MouseSensitivity { get; set; } = 1.0;

    public bool InvertX { get; set; }

    public bool InvertY { get; set; }

    public static PadStickMapping Keyboard(string negativeX, string positiveX, string negativeY, string positiveY) => new()
    {
        Source = PadStickSource.Keyboard,
        NegativeXKey = negativeX,
        PositiveXKey = positiveX,
        NegativeYKey = negativeY,
        PositiveYKey = positiveY,
    };
}

internal sealed class PadInputProfile
{
    public int Version { get; set; } = 1;

    public bool EnableKeyboardAndMouse { get; set; } = true;

    public bool EnableExternalController { get; set; } = true;

    public Dictionary<PadLogicalControl, PadButtonMapping> Buttons { get; set; } = [];

    public Dictionary<PadStickSide, PadStickMapping> Sticks { get; set; } = [];

    public static PadInputProfile CreateDefault()
    {
        var profile = new PadInputProfile
        {
            Buttons =
            {
                [PadLogicalControl.DpadLeft] = PadButtonMapping.Keys("Left"),
                [PadLogicalControl.DpadRight] = PadButtonMapping.Keys("Right"),
                [PadLogicalControl.DpadUp] = PadButtonMapping.Keys("Up"),
                [PadLogicalControl.DpadDown] = PadButtonMapping.Keys("Down"),
                [PadLogicalControl.Cross] = PadButtonMapping.Keys("Z", "Enter"),
                [PadLogicalControl.Circle] = PadButtonMapping.Keys("X", "Escape"),
                [PadLogicalControl.Square] = PadButtonMapping.Keys("C"),
                [PadLogicalControl.Triangle] = PadButtonMapping.Keys("V"),
                [PadLogicalControl.L1] = PadButtonMapping.Keys("Q"),
                [PadLogicalControl.R1] = PadButtonMapping.Keys("E"),
                [PadLogicalControl.L2] = PadButtonMapping.Keys("R"),
                [PadLogicalControl.R2] = PadButtonMapping.Keys("F"),
                [PadLogicalControl.L3] = new PadButtonMapping(),
                [PadLogicalControl.R3] = new PadButtonMapping(),
                [PadLogicalControl.Options] = PadButtonMapping.Keys("Tab", "Back"),
                [PadLogicalControl.TouchPad] = new PadButtonMapping(),
            },
            Sticks =
            {
                [PadStickSide.Left] = PadStickMapping.Keyboard("A", "D", "W", "S"),
                [PadStickSide.Right] = PadStickMapping.Keyboard("J", "L", "I", "K"),
            },
        };

        return profile;
    }

    public void EnsureDefaults()
    {
        var defaults = CreateDefault();

        foreach (var (control, mapping) in defaults.Buttons)
        {
            Buttons.TryAdd(control, mapping);
        }

        foreach (var (side, mapping) in defaults.Sticks)
        {
            Sticks.TryAdd(side, mapping);
        }
    }
}

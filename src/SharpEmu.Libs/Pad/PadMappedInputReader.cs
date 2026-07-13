// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Pad;

internal readonly record struct MouseDelta(int X, int Y);

internal static class PadMappedInputReader
{
    private static readonly IPlatformPadInputSource InputSource = PlatformPadInputSource.Create();

    public static PadState Read(PadInputProfile profile)
    {
        if (!profile.EnableKeyboardAndMouse || !InputSource.IsInputActive)
        {
            return NeutralState();
        }

        var mouseDelta = InputSource.ReadMouseDelta();
        var buttons = ReadButtons(profile);
        var left = ReadStick(profile, PadStickSide.Left, mouseDelta);
        var right = ReadStick(profile, PadStickSide.Right, mouseDelta);
        var l2 = IsButtonDown(profile, PadLogicalControl.L2) ? (byte)255 : (byte)0;
        var r2 = IsButtonDown(profile, PadLogicalControl.R2) ? (byte)255 : (byte)0;

        return new PadState(
            Connected: true,
            Buttons: buttons,
            LeftX: left.X,
            LeftY: left.Y,
            RightX: right.X,
            RightY: right.Y,
            L2: l2,
            R2: r2);
    }

    private static PadState NeutralState() => new(
        Connected: true,
        Buttons: 0,
        LeftX: 128,
        LeftY: 128,
        RightX: 128,
        RightY: 128,
        L2: 0,
        R2: 0);

    private static uint ReadButtons(PadInputProfile profile)
    {
        uint buttons = 0;
        AddIfDown(profile, PadLogicalControl.DpadLeft, OrbisPadButton.Left, ref buttons);
        AddIfDown(profile, PadLogicalControl.DpadRight, OrbisPadButton.Right, ref buttons);
        AddIfDown(profile, PadLogicalControl.DpadUp, OrbisPadButton.Up, ref buttons);
        AddIfDown(profile, PadLogicalControl.DpadDown, OrbisPadButton.Down, ref buttons);
        AddIfDown(profile, PadLogicalControl.Cross, OrbisPadButton.Cross, ref buttons);
        AddIfDown(profile, PadLogicalControl.Circle, OrbisPadButton.Circle, ref buttons);
        AddIfDown(profile, PadLogicalControl.Square, OrbisPadButton.Square, ref buttons);
        AddIfDown(profile, PadLogicalControl.Triangle, OrbisPadButton.Triangle, ref buttons);
        AddIfDown(profile, PadLogicalControl.L1, OrbisPadButton.L1, ref buttons);
        AddIfDown(profile, PadLogicalControl.R1, OrbisPadButton.R1, ref buttons);
        AddIfDown(profile, PadLogicalControl.L2, OrbisPadButton.L2, ref buttons);
        AddIfDown(profile, PadLogicalControl.R2, OrbisPadButton.R2, ref buttons);
        AddIfDown(profile, PadLogicalControl.L3, OrbisPadButton.L3, ref buttons);
        AddIfDown(profile, PadLogicalControl.R3, OrbisPadButton.R3, ref buttons);
        AddIfDown(profile, PadLogicalControl.Options, OrbisPadButton.Options, ref buttons);
        AddIfDown(profile, PadLogicalControl.TouchPad, OrbisPadButton.TouchPad, ref buttons);
        return buttons;
    }

    private static void AddIfDown(
        PadInputProfile profile,
        PadLogicalControl control,
        uint bit,
        ref uint buttons)
    {
        if (IsButtonDown(profile, control))
        {
            buttons |= bit;
        }
    }

    private static bool IsButtonDown(PadInputProfile profile, PadLogicalControl control) =>
        profile.Buttons.TryGetValue(control, out var mapping) &&
        mapping.Bindings.Any(InputSource.IsBindingDown);

    private static (byte X, byte Y) ReadStick(PadInputProfile profile, PadStickSide side, MouseDelta mouseDelta)
    {
        if (!profile.Sticks.TryGetValue(side, out var mapping))
        {
            return (128, 128);
        }

        return mapping.Source switch
        {
            PadStickSource.Keyboard => (
                ReadKeyboardAxis(mapping.NegativeXKey, mapping.PositiveXKey),
                ReadKeyboardAxis(mapping.NegativeYKey, mapping.PositiveYKey)),
            PadStickSource.Mouse => (
                ReadMouseAxis(mouseDelta.X, mapping.MouseSensitivity, mapping.InvertX),
                ReadMouseAxis(mouseDelta.Y, mapping.MouseSensitivity, mapping.InvertY)),
            PadStickSource.ExternalController => (128, 128),
            _ => (128, 128),
        };
    }

    private static byte ReadKeyboardAxis(string negativeKey, string positiveKey)
    {
        var negative = InputSource.IsBindingDown(PadInputBinding.Key(negativeKey));
        var positive = InputSource.IsBindingDown(PadInputBinding.Key(positiveKey));
        if (negative && !positive) return 0;
        if (positive && !negative) return 255;
        return 128;
    }

    private static byte ReadMouseAxis(int delta, double sensitivity, bool invert)
    {
        var value = delta * Math.Clamp(sensitivity, 0.05, 10.0);
        if (invert)
        {
            value = -value;
        }

        return (byte)Math.Clamp(128 + (int)Math.Round(value), 0, 255);
    }
}

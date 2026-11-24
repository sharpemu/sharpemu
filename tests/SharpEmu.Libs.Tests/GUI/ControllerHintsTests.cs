// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using SharpEmu.GUI.Controls;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using SDL;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class ControllerHintsTests
{
    [Theory]
    [InlineData(SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOX360)]
    [InlineData(SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOXONE)]
    public void XboxPadsUseTheirOwnFaceAndShoulderLabels(SDL_GamepadType sdlType)
    {
        var type = SdlGamepadStateReader.MapGamepadType(sdlType);

        Assert.Equal(HostGamepadType.Xbox, type);
        Assert.Equal("A", PadGlyph.GetButtonLetter(PadSymbol.Cross, type));
        Assert.Equal("B", PadGlyph.GetButtonLetter(PadSymbol.Circle, type));
        Assert.Equal("X", PadGlyph.GetButtonLetter(PadSymbol.Square, type));
        Assert.Equal("Y", PadGlyph.GetButtonLetter(PadSymbol.Triangle, type));
        Assert.Equal(("LB", "RB"), MainWindow.ShoulderHintLabels(type));
    }

    [Theory]
    [InlineData(SDL_GamepadType.SDL_GAMEPAD_TYPE_PS4, HostGamepadType.DualShock4)]
    [InlineData(SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5, HostGamepadType.DualSense)]
    [InlineData(SDL_GamepadType.SDL_GAMEPAD_TYPE_UNKNOWN, HostGamepadType.Generic)]
    public void OtherPadsKeepTheExistingSymbols(SDL_GamepadType sdlType, HostGamepadType expectedType)
    {
        var type = SdlGamepadStateReader.MapGamepadType(sdlType);

        Assert.Equal(expectedType, type);
        foreach (var symbol in Enum.GetValues<PadSymbol>())
        {
            Assert.Null(PadGlyph.GetButtonLetter(symbol, type));
        }

        Assert.Equal(("L1", "R1"), MainWindow.ShoulderHintLabels(type));
    }
}
